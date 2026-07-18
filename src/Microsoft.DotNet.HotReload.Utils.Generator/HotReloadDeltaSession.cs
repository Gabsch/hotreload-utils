// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.ExternalAccess.HotReload.Api;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.DotNet.HotReload.Utils.Generator;

public enum HotReloadDeltaUpdateStatus
{
    Ready,
    NoChanges,
    Blocked,
    RestartRequired
}

public sealed record HotReloadDeltaDiagnostic(
    string Id,
    string Severity,
    string Message,
    string? FilePath,
    int? Line,
    int? Column,
    bool IsRudeEdit);

public sealed record HotReloadDeltaSessionInfo(
    string ProjectPath,
    string Configuration,
    string? TargetFramework,
    string OutputAssemblyPath,
    string PdbPath,
    string ModuleName);

public sealed record HotReloadDeltaDocumentChange(string FilePath, string Text);

public sealed record HotReloadDeltaChangedDocumentEvidence(
    string FilePath,
    string BaselineSha256,
    string UpdatedSha256);

public sealed record HotReloadDeltaLineUpdate(
    string FilePath,
    int NewLine,
    int OldLine);

public sealed record HotReloadDeltaPreparedUpdate(
    HotReloadDeltaUpdateStatus Status,
    string ModuleName,
    Guid ModuleId,
    ImmutableArray<string> ChangedFiles,
    ImmutableArray<byte> MetadataDelta,
    ImmutableArray<byte> IlDelta,
    ImmutableArray<byte> PdbDelta,
    ImmutableArray<int> UpdatedTypes,
    ImmutableArray<int> UpdatedMethods,
    ImmutableArray<HotReloadDeltaChangedDocumentEvidence> ChangedDocuments,
    ImmutableArray<string> RequiredCapabilities,
    ImmutableArray<HotReloadDeltaDiagnostic> Diagnostics,
    bool LineUpdatesComplete,
    ImmutableArray<string> Warnings)
{
    public ImmutableArray<HotReloadDeltaLineUpdate> LineUpdates { get; init; } = [];
}

/// <summary>
/// Session-oriented wrapper around the official Roslyn Hot Reload service.
/// Unlike the experimental CLI runner, preparing an update does not advance
/// the baseline until the caller explicitly commits it.
/// </summary>
public sealed class HotReloadDeltaSession : IDisposable
{
    private readonly HotReloadService hotReloadService;
    private Solution solution;
    private readonly ProjectId projectId;
    private ImmutableArray<byte> baselinePdb;
    private Solution? pendingSolution;
    private ImmutableArray<byte>? pendingPdb;
    private bool ended;

    private HotReloadDeltaSession(
        HotReloadService hotReloadService,
        Solution solution,
        ProjectId projectId,
        ImmutableArray<byte> baselinePdb,
        HotReloadDeltaSessionInfo info)
    {
        this.hotReloadService = hotReloadService;
        this.solution = solution;
        this.projectId = projectId;
        this.baselinePdb = baselinePdb;
        Info = info;
    }

    public HotReloadDeltaSessionInfo Info { get; }

    public bool HasPendingUpdate => pendingSolution is not null;

    public static async Task<HotReloadDeltaSession> StartAsync(
        string projectPath,
        string configuration,
        string? targetFramework,
        IReadOnlyDictionary<string, string>? properties,
        IReadOnlyList<string>? runtimeCapabilities,
        CancellationToken cancellationToken = default)
    {
        var builder = Config.Builder();
        builder.ProjectPath = Path.GetFullPath(projectPath);
        builder.Properties.Add(new("Configuration", configuration));
        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            builder.Properties.Add(new("TargetFramework", targetFramework));
        }

        if (properties is not null)
        {
            foreach (var property in properties)
            {
                builder.Properties.Add(property);
            }
        }

        var capabilities = EnC.EditAndContinueCapabilities.Baseline;
        if (runtimeCapabilities is { Count: > 0 })
        {
            capabilities = EnC.EditAndContinueCapabilities.None;
            foreach (var value in runtimeCapabilities)
            {
                if (!Enum.TryParse<EnC.EditAndContinueCapabilities>(value, ignoreCase: true, out var capability))
                {
                    throw new InvalidOperationException($"Unknown Edit and Continue capability '{value}'.");
                }

                capabilities |= capability;
            }
        }

        var baselineProject = await BaselineProject.Make(builder.Bake(), capabilities, cancellationToken);
        var artifacts = await baselineProject.PrepareBaseline(cancellationToken);
        var outputAssembly = Path.GetFullPath(artifacts.BaselineOutputAsmPath);
        var pdbPath = Path.ChangeExtension(outputAssembly, ".pdb");
        if (!File.Exists(pdbPath))
        {
            artifacts.HotReloadService.EndSession();
            throw new InvalidOperationException($"Portable PDB not found for baseline assembly: {pdbPath}");
        }

        var info = new HotReloadDeltaSessionInfo(
            builder.ProjectPath,
            configuration,
            targetFramework,
            outputAssembly,
            pdbPath,
            Path.GetFileName(outputAssembly));
        return new HotReloadDeltaSession(
            artifacts.HotReloadService,
            artifacts.BaselineSolution,
            artifacts.BaselineProjectId,
            (await File.ReadAllBytesAsync(pdbPath, cancellationToken)).ToImmutableArray(),
            info);
    }

    public async Task<HotReloadDeltaPreparedUpdate> PrepareUpdateAsync(
        IReadOnlyList<HotReloadDeltaDocumentChange> changes,
        CancellationToken cancellationToken = default)
    {
        ThrowIfEnded();
        if (pendingSolution is not null)
        {
            throw new InvalidOperationException("A Hot Reload update is already pending.");
        }

        if (changes.Count == 0)
        {
            throw new ArgumentException("At least one changed document is required.", nameof(changes));
        }

        var updatedSolution = solution;
        var changedFiles = ImmutableArray.CreateBuilder<string>(changes.Count);
        var changedDocuments = ImmutableArray.CreateBuilder<HotReloadDeltaChangedDocumentEvidence>(changes.Count);
        var hasTextChanges = false;
        var hasLineMovingChanges = false;
        foreach (var change in changes)
        {
            var path = Path.GetFullPath(change.FilePath);
            var project = updatedSolution.GetProject(projectId)!;
            var document = project.Documents.SingleOrDefault(candidate =>
                string.Equals(Path.GetFullPath(candidate.FilePath ?? string.Empty), path, PathComparison));
            if (document is null)
            {
                return RestartRequired(changes, $"Document is not part of the baseline project: {path}");
            }

            var oldText = await document.GetTextAsync(cancellationToken);
            var newText = SourceText.From(change.Text, Encoding.UTF8);
            if (oldText.ContentEquals(newText))
            {
                continue;
            }

            var lineCountChanged = oldText.Lines.Count != newText.Lines.Count;
            if (ContainsLineDirective(oldText) ||
                ContainsLineDirective(newText))
            {
                return RestartRequired(
                    changes,
                    "Line-moving and #line-mapped edits require restart/replay until exact Roslyn sequence-point updates are exposed.");
            }

            hasLineMovingChanges |= lineCountChanged;

            updatedSolution = updatedSolution.WithDocumentText(document.Id, newText);
            changedFiles.Add(path);
            changedDocuments.Add(new(
                path,
                ContentHash(oldText),
                ContentHash(newText)));
            hasTextChanges = true;
        }

        if (!hasTextChanges)
        {
            return new(
                HotReloadDeltaUpdateStatus.NoChanges,
                Info.ModuleName,
                Guid.Empty,
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                LineUpdatesComplete: true,
                []);
        }

        var updates = await hotReloadService.GetUpdatesAsync(
            updatedSolution,
            ImmutableDictionary<ProjectId, HotReloadService.RunningProjectInfo>.Empty,
            cancellationToken);
        var persistentDiagnostics = updates.PersistentDiagnostics
            .Select(diagnostic => ToDiagnostic(diagnostic))
            .ToImmutableArray();
        var rudeDiagnostics = updates.TransientDiagnostics
            .SelectMany(entry => entry.diagnostics.Select(diagnostic => ToDiagnostic(diagnostic, isRudeEdit: true)))
            .ToImmutableArray();
        var diagnostics = persistentDiagnostics.Concat(rudeDiagnostics).ToImmutableArray();

        if (updates.Status == HotReloadService.Status.Blocked ||
            persistentDiagnostics.Any(diagnostic => string.Equals(diagnostic.Severity, "Error", StringComparison.OrdinalIgnoreCase)))
        {
            hotReloadService.DiscardUpdate();
            return new(
                HotReloadDeltaUpdateStatus.Blocked,
                Info.ModuleName,
                Guid.Empty,
                changedFiles.ToImmutable(),
                [],
                [],
                [],
                [],
                [],
                changedDocuments.ToImmutable(),
                [],
                diagnostics,
                LineUpdatesComplete: true,
                []);
        }

        if (!rudeDiagnostics.IsEmpty ||
            !updates.ProjectsToRestart.IsEmpty ||
            !updates.ProjectsToRebuild.IsEmpty ||
            !updates.ProjectsToRedeploy.IsEmpty)
        {
            hotReloadService.DiscardUpdate();
            return new(
                HotReloadDeltaUpdateStatus.RestartRequired,
                Info.ModuleName,
                Guid.Empty,
                changedFiles.ToImmutable(),
                [],
                [],
                [],
                [],
                [],
                changedDocuments.ToImmutable(),
                [],
                diagnostics,
                LineUpdatesComplete: true,
                ["Roslyn classified the edit as requiring rebuild, redeploy, or restart."]);
        }

        if (updates.Status == HotReloadService.Status.NoChangesToApply || updates.ProjectUpdates.IsEmpty)
        {
            return new(
                HotReloadDeltaUpdateStatus.NoChanges,
                Info.ModuleName,
                Guid.Empty,
                changedFiles.ToImmutable(),
                [],
                [],
                [],
                [],
                [],
                changedDocuments.ToImmutable(),
                [],
                diagnostics,
                LineUpdatesComplete: true,
                []);
        }

        if (updates.ProjectUpdates.Length != 1 || updates.ProjectUpdates[0].ProjectId != projectId)
        {
            hotReloadService.DiscardUpdate();
            return RestartRequired(changes, "Hot Reload delta v1 supports exactly one emitting project per update.");
        }

        var update = updates.ProjectUpdates[0];
        var updatedMethods = GetUpdatedMethodTokens(update.MetadataDelta);
        var fullPdb = await EmitFullPdbAsync(updatedSolution.GetProject(projectId)!, cancellationToken);
        if (!fullPdb.Success)
        {
            hotReloadService.DiscardUpdate();
            return RestartRequired(changes, fullPdb.Error!);
        }

        ImmutableArray<HotReloadDeltaLineUpdate> lineUpdates = [];
        if (hasLineMovingChanges)
        {
            var mapping = CreateExactLineUpdates(
                fullPdb.Pdb,
                changedFiles.ToImmutable(),
                updatedMethods);
            if (!mapping.Success)
            {
                hotReloadService.DiscardUpdate();
                return RestartRequired(changes, mapping.Error!);
            }

            lineUpdates = mapping.LineUpdates;
        }

        pendingSolution = updatedSolution;
        pendingPdb = fullPdb.Pdb;
        return new(
            HotReloadDeltaUpdateStatus.Ready,
            Info.ModuleName,
            update.ModuleId,
            changedFiles.ToImmutable(),
            update.MetadataDelta,
            update.ILDelta,
            update.PdbDelta,
            update.UpdatedTypes,
            updatedMethods,
            changedDocuments.ToImmutable(),
            update.RequiredCapabilities,
            diagnostics,
            LineUpdatesComplete: true,
            hasLineMovingChanges
                ? ["Exact sequence-point updates were derived from committed and updated portable PDBs for unchanged methods."]
                : [])
        {
            LineUpdates = lineUpdates
        };
    }

    public void CommitUpdate()
    {
        ThrowIfEnded();
        if (pendingSolution is null)
        {
            throw new InvalidOperationException("No Hot Reload update is pending.");
        }

        hotReloadService.CommitUpdate();
        solution = pendingSolution;
        baselinePdb = pendingPdb ?? baselinePdb;
        pendingSolution = null;
        pendingPdb = null;
    }

    public void DiscardUpdate()
    {
        ThrowIfEnded();
        if (pendingSolution is null)
        {
            throw new InvalidOperationException("No Hot Reload update is pending.");
        }

        hotReloadService.DiscardUpdate();
        pendingSolution = null;
        pendingPdb = null;
    }

    public void Dispose()
    {
        if (ended)
        {
            return;
        }

        if (pendingSolution is not null)
        {
            hotReloadService.DiscardUpdate();
            pendingSolution = null;
            pendingPdb = null;
        }

        hotReloadService.EndSession();
        ended = true;
    }

    private HotReloadDeltaPreparedUpdate RestartRequired(
        IReadOnlyList<HotReloadDeltaDocumentChange> changes,
        string warning) => new(
            HotReloadDeltaUpdateStatus.RestartRequired,
            Info.ModuleName,
            Guid.Empty,
            changes.Select(change => Path.GetFullPath(change.FilePath)).ToImmutableArray(),
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            LineUpdatesComplete: false,
            [warning]);

    private static HotReloadDeltaDiagnostic ToDiagnostic(Diagnostic diagnostic, bool isRudeEdit = false)
    {
        var span = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan() : default;
        return new(
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            diagnostic.GetMessage(),
            diagnostic.Location.IsInSource ? span.Path : null,
            diagnostic.Location.IsInSource ? span.StartLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? span.StartLinePosition.Character + 1 : null,
            isRudeEdit);
    }

    private static bool ContainsLineDirective(SourceText text) =>
        text.ToString().Contains("#line", StringComparison.Ordinal);

    private async Task<FullPdbResult> EmitFullPdbAsync(
        Project updatedProject,
        CancellationToken cancellationToken)
    {
        var compilation = await updatedProject.GetCompilationAsync(cancellationToken);
        if (compilation is null)
        {
            return FullPdbResult.Fail("The updated project compilation is unavailable for sequence-point mapping.");
        }

        using var peStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var emit = compilation.Emit(
            peStream,
            pdbStream,
            options: new EmitOptions(
                debugInformationFormat: DebugInformationFormat.PortablePdb,
                pdbFilePath: Info.PdbPath),
            cancellationToken: cancellationToken);
        if (!emit.Success)
        {
            var message = string.Join("; ", emit.Diagnostics
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(static diagnostic => diagnostic.GetMessage()));
            return FullPdbResult.Fail($"The updated portable PDB could not be emitted: {message}");
        }

        return FullPdbResult.Ok(pdbStream.ToArray().ToImmutableArray());
    }

    private ExactLineUpdateResult CreateExactLineUpdates(
        ImmutableArray<byte> updatedPdb,
        ImmutableArray<string> changedFiles,
        ImmutableArray<int> updatedMethods)
    {
        var changedPaths = changedFiles
            .Select(Path.GetFullPath)
            .ToHashSet(PathComparer);
        var updatedMethodSet = updatedMethods.ToHashSet();
        var oldPoints = ReadSequencePoints(baselinePdb);
        var newPoints = ReadSequencePoints(updatedPdb);
        var mappings = new Dictionary<string, Dictionary<int, int>>(PathComparer);

        foreach (var oldPoint in oldPoints.Values)
        {
            if (oldPoint.Hidden ||
                updatedMethodSet.Contains(oldPoint.MethodToken) ||
                !changedPaths.Contains(Path.GetFullPath(oldPoint.FilePath)))
            {
                continue;
            }

            if (!newPoints.TryGetValue((oldPoint.MethodToken, oldPoint.IlOffset), out var newPoint) ||
                newPoint.Hidden ||
                !PathComparer.Equals(Path.GetFullPath(oldPoint.FilePath), Path.GetFullPath(newPoint.FilePath)))
            {
                return ExactLineUpdateResult.Fail(
                    $"An unchanged sequence point could not be mapped exactly for method 0x{oldPoint.MethodToken:x8} at IL offset {oldPoint.IlOffset}.");
            }

            if (!mappings.TryGetValue(oldPoint.FilePath, out var fileMappings))
            {
                fileMappings = new Dictionary<int, int>();
                mappings.Add(oldPoint.FilePath, fileMappings);
            }

            if (fileMappings.TryGetValue(oldPoint.StartLine, out var existingLine) && existingLine != newPoint.StartLine)
            {
                return ExactLineUpdateResult.Fail(
                    $"Source line {oldPoint.StartLine + 1} maps to multiple updated lines in '{oldPoint.FilePath}'.");
            }

            fileMappings[oldPoint.StartLine] = newPoint.StartLine;
        }

        var lineUpdates = mappings
            .Where(static mapping => mapping.Value.Any(static pair => pair.Key != pair.Value))
            .SelectMany(static mapping => mapping.Value
                .OrderBy(static pair => pair.Key)
                .Select(pair => new HotReloadDeltaLineUpdate(mapping.Key, pair.Value, pair.Key)))
            .ToImmutableArray();
        if (lineUpdates.IsEmpty)
        {
            return ExactLineUpdateResult.Fail(
                "The source line count changed, but no exact unchanged-method sequence-point mapping was produced.");
        }

        return ExactLineUpdateResult.Ok(lineUpdates);
    }

    private static Dictionary<(int MethodToken, int IlOffset), PortableSequencePoint> ReadSequencePoints(
        ImmutableArray<byte> pdbImage)
    {
        using var provider = MetadataReaderProvider.FromPortablePdbImage(pdbImage);
        var reader = provider.GetMetadataReader();
        var result = new Dictionary<(int MethodToken, int IlOffset), PortableSequencePoint>();
        var methodCount = reader.GetTableRowCount(TableIndex.MethodDebugInformation);
        for (var row = 1; row <= methodCount; row++)
        {
            var methodToken = MetadataTokens.GetToken(MetadataTokens.MethodDefinitionHandle(row));
            var method = reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(row));
            var defaultDocument = method.Document;
            foreach (var point in method.GetSequencePoints())
            {
                var documentHandle = point.Document.IsNil ? defaultDocument : point.Document;
                if (documentHandle.IsNil)
                {
                    continue;
                }

                var document = reader.GetDocument(documentHandle);
                var filePath = reader.GetString(document.Name);
                result[(methodToken, point.Offset)] = new(
                    methodToken,
                    point.Offset,
                    filePath,
                    point.StartLine - 1,
                    point.IsHidden);
            }
        }

        return result;
    }

    private static string ContentHash(SourceText text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();

    private static ImmutableArray<int> GetUpdatedMethodTokens(ImmutableArray<byte> metadataDelta)
    {
        using var provider = MetadataReaderProvider.FromMetadataImage(metadataDelta);
        var reader = provider.GetMetadataReader();
        return reader.GetEditAndContinueMapEntries()
            .Where(static handle => handle.Kind == HandleKind.MethodDefinition)
            .Select(static handle => MetadataTokens.GetToken(handle))
            .Distinct()
            .Order()
            .ToImmutableArray();
    }

    private void ThrowIfEnded()
    {
        ObjectDisposedException.ThrowIf(ended, this);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record PortableSequencePoint(
        int MethodToken,
        int IlOffset,
        string FilePath,
        int StartLine,
        bool Hidden);

    private sealed record ExactLineUpdateResult(
        bool Success,
        ImmutableArray<HotReloadDeltaLineUpdate> LineUpdates,
        string? Error)
    {
        public static ExactLineUpdateResult Ok(ImmutableArray<HotReloadDeltaLineUpdate> lineUpdates) =>
            new(true, lineUpdates, null);

        public static ExactLineUpdateResult Fail(string error) =>
            new(false, [], error);
    }

    private sealed record FullPdbResult(
        bool Success,
        ImmutableArray<byte> Pdb,
        string? Error)
    {
        public static FullPdbResult Ok(ImmutableArray<byte> pdb) => new(true, pdb, null);

        public static FullPdbResult Fail(string error) => new(false, [], error);
    }
}
