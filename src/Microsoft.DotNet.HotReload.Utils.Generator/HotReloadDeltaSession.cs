// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.ExternalAccess.HotReload.Api;
using Microsoft.CodeAnalysis.MSBuild;
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
    private readonly MSBuildWorkspace workspace;
    private Solution solution;
    private readonly ProjectId projectId;
    private ImmutableArray<byte> baselinePe;
    private ImmutableArray<byte> baselinePdb;
    private Solution? pendingSolution;
    private ImmutableArray<byte>? pendingPe;
    private ImmutableArray<byte>? pendingPdb;
    private bool ended;

    private HotReloadDeltaSession(
        HotReloadService hotReloadService,
        MSBuildWorkspace workspace,
        Solution solution,
        ProjectId projectId,
        ImmutableArray<byte> baselinePe,
        ImmutableArray<byte> baselinePdb,
        HotReloadDeltaSessionInfo info)
    {
        this.hotReloadService = hotReloadService;
        this.workspace = workspace;
        this.solution = solution;
        this.projectId = projectId;
        this.baselinePe = baselinePe;
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
        BaselineArtifacts artifacts;
        try
        {
            artifacts = await baselineProject.PrepareBaseline(cancellationToken);
        }
        catch
        {
            baselineProject.Workspace.Dispose();
            throw;
        }
        var outputAssembly = Path.GetFullPath(artifacts.BaselineOutputAsmPath);
        var pdbPath = FindPortablePdbPath(outputAssembly);
        try
        {
            if (!File.Exists(pdbPath))
            {
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
                artifacts.Workspace,
                artifacts.BaselineSolution,
                artifacts.BaselineProjectId,
                (await File.ReadAllBytesAsync(outputAssembly, cancellationToken)).ToImmutableArray(),
                (await File.ReadAllBytesAsync(pdbPath, cancellationToken)).ToImmutableArray(),
                info);
        }
        catch
        {
            try
            {
                artifacts.HotReloadService.EndSession();
            }
            finally
            {
                artifacts.Workspace.Dispose();
            }
            throw;
        }
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
            TextDocument? document = project.Documents.SingleOrDefault(candidate =>
                string.Equals(Path.GetFullPath(candidate.FilePath ?? string.Empty), path, PathComparison));
            var isAdditionalDocument = false;
            if (document is null)
            {
                document = project.AdditionalDocuments.SingleOrDefault(candidate =>
                    string.Equals(Path.GetFullPath(candidate.FilePath ?? string.Empty), path, PathComparison));
                isAdditionalDocument = document is not null;
            }

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

            if (ContainsLineDirective(oldText) ||
                ContainsLineDirective(newText))
            {
                return RestartRequired(
                    changes,
                    "Line-moving and #line-mapped edits require restart/replay until exact Roslyn sequence-point updates are exposed.");
            }

            hasLineMovingChanges |= HasLineMovingChanges(oldText, newText);

            updatedSolution = isAdditionalDocument
                ? updatedSolution.WithAdditionalDocumentText(document.Id, newText)
                : updatedSolution.WithDocumentText(document.Id, newText);
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

        try
        {
            var update = updates.ProjectUpdates[0];
            var updatedMethods = GetUpdatedMethodTokens(update.MetadataDelta);
            var fullPdb = await EmitFullPdbAsync(updatedSolution.GetProject(projectId)!, cancellationToken);
            if (!fullPdb.Success)
            {
                hotReloadService.DiscardUpdate();
                return RestartRequired(changes, fullPdb.Error!);
            }

            var tokenValidation = ValidateMethodTokenIdentity(baselinePe, fullPdb.Pe);
            if (tokenValidation is not null)
            {
                hotReloadService.DiscardUpdate();
                return RestartRequired(changes, tokenValidation);
            }

            ImmutableArray<HotReloadDeltaLineUpdate> lineUpdates = [];
            var includesUpdatedMethodMappings = false;
            if (hasLineMovingChanges)
            {
                var mapping = await CreateExactLineUpdatesAsync(
                    solution.GetProject(projectId)!,
                    updatedSolution.GetProject(projectId)!,
                    fullPdb.Pdb,
                    changedFiles.ToImmutable(),
                    updatedMethods,
                    cancellationToken);
                if (!mapping.Success)
                {
                    hotReloadService.DiscardUpdate();
                    return RestartRequired(changes, mapping.Error!);
                }

                lineUpdates = mapping.LineUpdates;
                includesUpdatedMethodMappings = mapping.IncludesUpdatedMethods;
            }

            pendingSolution = updatedSolution;
            pendingPe = fullPdb.Pe;
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
                    ? [includesUpdatedMethodMappings
                        ? "Exact sequence-point updates were derived from committed and updated source/PDB evidence, including updated methods."
                        : "Exact sequence-point updates were derived from committed and updated portable PDBs for unchanged methods."]
                    : [])
            {
                LineUpdates = lineUpdates
            };
        }
        catch (Exception preparationFailure)
        {
            try
            {
                hotReloadService.DiscardUpdate();
                pendingSolution = null;
                pendingPe = null;
                pendingPdb = null;
            }
            catch (Exception discardFailure)
            {
                throw new AggregateException(
                    "Hot Reload preparation failed after Roslyn staged an update, and that update could not be discarded.",
                    preparationFailure,
                    discardFailure);
            }

            throw;
        }
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
        baselinePe = pendingPe ?? baselinePe;
        baselinePdb = pendingPdb ?? baselinePdb;
        pendingSolution = null;
        pendingPe = null;
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
        pendingPe = null;
        pendingPdb = null;
    }

    public void Dispose()
    {
        if (ended)
        {
            return;
        }

        try
        {
            if (pendingSolution is not null)
            {
                hotReloadService.DiscardUpdate();
                pendingSolution = null;
                pendingPe = null;
                pendingPdb = null;
            }
        }
        finally
        {
            try
            {
                hotReloadService.EndSession();
            }
            finally
            {
                workspace.Dispose();
                ended = true;
            }
        }
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
        CSharpSyntaxTree.ParseText(text)
            .GetRoot()
            .DescendantTrivia(descendIntoTrivia: true)
            .Any(static trivia => trivia.IsKind(SyntaxKind.LineDirectiveTrivia));

    private static bool HasLineMovingChanges(SourceText oldText, SourceText newText)
    {
        if (oldText.Lines.Count != newText.Lines.Count)
        {
            return true;
        }

        var oldPositions = GetLinePositions(oldText);
        var newPositions = GetLinePositions(newText);
        foreach (var (line, positions) in oldPositions)
        {
            if (!newPositions.TryGetValue(line, out var updatedPositions))
            {
                continue;
            }

            var sharedCount = Math.Min(positions.Count, updatedPositions.Count);
            for (var index = 0; index < sharedCount; index++)
            {
                if (positions[index] != updatedPositions[index])
                {
                    return true;
                }
            }
        }

        var oldSyntaxPositions = GetStableSyntaxLinePositions(oldText);
        var newSyntaxPositions = GetStableSyntaxLinePositions(newText);
        foreach (var (identity, positions) in oldSyntaxPositions)
        {
            if (!newSyntaxPositions.TryGetValue(identity, out var updatedPositions))
            {
                continue;
            }

            var sharedCount = Math.Min(positions.Count, updatedPositions.Count);
            for (var index = 0; index < sharedCount; index++)
            {
                if (positions[index] != updatedPositions[index])
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Dictionary<string, List<int>> GetLinePositions(SourceText text)
    {
        var result = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < text.Lines.Count; index++)
        {
            var line = text.Lines[index].ToString();
            if (!result.TryGetValue(line, out var positions))
            {
                positions = [];
                result.Add(line, positions);
            }

            positions.Add(index);
        }

        return result;
    }

    private static Dictionary<string, List<int>> GetStableSyntaxLinePositions(SourceText text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetRoot();
        var result = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var node in root.DescendantNodes())
        {
            string? identity = node switch
            {
                MethodDeclarationSyntax method => "method:" + MethodIdentity(method),
                LocalDeclarationStatementSyntax local => "local:" + LocalIdentity(local),
                StatementSyntax statement => "statement:" + StatementIdentity(statement),
                _ => null
            };
            if (identity is null)
            {
                continue;
            }

            if (!result.TryGetValue(identity, out var positions))
            {
                positions = [];
                result.Add(identity, positions);
            }

            positions.Add(tree.GetLineSpan(node.Span).StartLinePosition.Line);
        }

        return result;
    }

    private static string MethodIdentity(MethodDeclarationSyntax method)
    {
        var containingTypes = method.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .Reverse()
            .Select(static type => type.Identifier.ValueText);
        var parameters = method.ParameterList.Parameters.Select(static parameter =>
            $"{parameter.Modifiers}:{parameter.Type?.WithoutTrivia()}");
        return $"{string.Join(".", containingTypes)}::{method.Identifier.ValueText}({string.Join(",", parameters)})";
    }

    private static string LocalIdentity(LocalDeclarationStatementSyntax local)
    {
        var method = local.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var variables = string.Join(",", local.Declaration.Variables.Select(static variable => variable.Identifier.ValueText));
        return $"{(method is null ? string.Empty : MethodIdentity(method))}:{local.Declaration.Type.WithoutTrivia()}:{variables}";
    }

    private static string StatementIdentity(StatementSyntax statement)
    {
        var method = statement.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var tokens = statement.DescendantTokens().Select(static token =>
            token.Parent is LiteralExpressionSyntax
                ? $"<{token.Kind()}>"
                : token.ValueText);
        return $"{(method is null ? string.Empty : MethodIdentity(method))}:{statement.Kind()}:{string.Join("|", tokens)}";
    }

    private static string FindPortablePdbPath(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var embeddedCandidates = peReader.ReadDebugDirectory()
            .Where(static entry => entry.Type == DebugDirectoryEntryType.CodeView)
            .Select(entry => peReader.ReadCodeViewDebugDirectoryData(entry).Path)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(
                Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(Path.GetDirectoryName(assemblyPath)!, path)))
            .Distinct(PathComparer)
            .ToArray();
        var existingEmbeddedCandidates = embeddedCandidates.Where(File.Exists).ToArray();
        if (existingEmbeddedCandidates.Length == 1)
        {
            return existingEmbeddedCandidates[0];
        }

        var siblingPdb = Path.ChangeExtension(assemblyPath, ".pdb");
        if (existingEmbeddedCandidates.Length == 0 && File.Exists(siblingPdb))
        {
            return siblingPdb;
        }

        if (embeddedCandidates.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one portable PDB path in the baseline assembly debug directory, found {embeddedCandidates.Length}.");
        }

        return embeddedCandidates[0];
    }

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

        return FullPdbResult.Ok(
            peStream.ToArray().ToImmutableArray(),
            pdbStream.ToArray().ToImmutableArray());
    }

    private async Task<ExactLineUpdateResult> CreateExactLineUpdatesAsync(
        Project committedProject,
        Project updatedProject,
        ImmutableArray<byte> updatedPdb,
        ImmutableArray<string> changedFiles,
        ImmutableArray<int> updatedMethods,
        CancellationToken cancellationToken)
    {
        var changedPaths = changedFiles
            .Select(Path.GetFullPath)
            .ToHashSet(PathComparer);
        var updatedMethodSet = updatedMethods.ToHashSet();
        var oldPoints = ReadSequencePoints(baselinePdb);
        var newPoints = ReadSequencePoints(updatedPdb);
        var mappings = new Dictionary<string, Dictionary<int, int>>(PathComparer);
        var reverseMappings = new Dictionary<string, Dictionary<int, int>>(PathComparer);
        var includesUpdatedMethods = false;
        var committedDocuments = await ReadSyntaxDocumentsAsync(
            committedProject,
            changedPaths,
            cancellationToken);
        var updatedDocuments = await ReadSyntaxDocumentsAsync(
            updatedProject,
            changedPaths,
            cancellationToken);

        var resolvedOldPoints = ResolveChangedDocumentPoints(oldPoints, changedPaths, committedDocuments);
        var resolvedNewPoints = ResolveChangedDocumentPoints(newPoints, changedPaths, updatedDocuments);

        foreach (var methodGroup in resolvedOldPoints
            .Where(static point => !point.Hidden)
            .GroupBy(point => point.MethodToken))
        {
            if (updatedMethodSet.Contains(methodGroup.Key))
            {
                includesUpdatedMethods = true;
                var updatedMapping = MapUpdatedMethod(
                    methodGroup.OrderBy(point => point.IlOffset).ToImmutableArray(),
                    resolvedNewPoints
                        .Where(point => !point.Hidden && point.MethodToken == methodGroup.Key)
                        .OrderBy(point => point.IlOffset)
                        .ToImmutableArray(),
                    committedDocuments,
                    updatedDocuments);
                if (!updatedMapping.Success)
                {
                    return ExactLineUpdateResult.Fail(updatedMapping.Error!);
                }

                foreach (var pair in updatedMapping.Points)
                {
                    var addResult = AddLineMapping(mappings, reverseMappings, pair.Old, pair.New);
                    if (addResult is not null)
                    {
                        return ExactLineUpdateResult.Fail(addResult);
                    }
                }

                continue;
            }

            foreach (var oldPoint in methodGroup)
            {
                var matches = resolvedNewPoints.Where(point =>
                    !point.Hidden &&
                    point.MethodToken == oldPoint.MethodToken &&
                    point.IlOffset == oldPoint.IlOffset &&
                    PathComparer.Equals(Path.GetFullPath(oldPoint.FilePath), Path.GetFullPath(point.FilePath))).ToArray();
                if (matches.Length != 1)
                {
                    return ExactLineUpdateResult.Fail(
                        $"An unchanged sequence point could not be mapped exactly for method 0x{oldPoint.MethodToken:x8} at IL offset {oldPoint.IlOffset}.");
                }

                var addResult = AddLineMapping(mappings, reverseMappings, oldPoint, matches[0]);
                if (addResult is not null)
                {
                    return ExactLineUpdateResult.Fail(addResult);
                }
            }
        }

        var lineUpdates = mappings
            .SelectMany(static mapping => mapping.Value
                .Where(static pair => pair.Key != pair.Value)
                .OrderBy(static pair => pair.Key)
                .Select(pair => new HotReloadDeltaLineUpdate(mapping.Key, pair.Value, pair.Key)))
            .ToImmutableArray();
        if (lineUpdates.IsEmpty)
        {
            return ExactLineUpdateResult.Fail(
                "The source contains line-moving changes, but no exact sequence-point relocation was produced.");
        }

        return ExactLineUpdateResult.Ok(lineUpdates, includesUpdatedMethods);
    }

    private static ImmutableArray<PortableSequencePoint> ResolveChangedDocumentPoints(
        ImmutableArray<PortableSequencePoint> points,
        HashSet<string> changedPaths,
        Dictionary<string, DocumentSyntaxSnapshot> documents)
    {
        var documentChecksums = documents.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Checksum,
            PathComparer);
        var result = ImmutableArray.CreateBuilder<PortableSequencePoint>();
        foreach (var point in points)
        {
            var resolvedPath = ResolveChangedDocumentPath(
                point.FilePath,
                point.DocumentChecksum,
                changedPaths,
                documentChecksums);
            if (resolvedPath is not null)
            {
                result.Add(point with { FilePath = resolvedPath });
            }
        }

        return result.ToImmutable();
    }

    private static string? ResolveChangedDocumentPath(
        string pdbDocumentPath,
        ImmutableArray<byte> pdbDocumentChecksum,
        IReadOnlySet<string> changedPaths,
        IReadOnlyDictionary<string, ImmutableArray<byte>> documentChecksums)
    {
        var fullPath = TryGetFullPath(pdbDocumentPath);
        var directMatches = changedPaths.Where(path => PathComparer.Equals(path, fullPath)).ToArray();
        if (directMatches.Length == 1)
        {
            return directMatches[0];
        }

        if (pdbDocumentChecksum.IsDefaultOrEmpty)
        {
            return null;
        }

        var checksumMatches = documentChecksums
            .Where(pair =>
                changedPaths.Contains(pair.Key) &&
                pdbDocumentChecksum.AsSpan().SequenceEqual(pair.Value.AsSpan()))
            .Select(static pair => pair.Key)
            .Distinct(PathComparer)
            .ToArray();
        return checksumMatches.Length == 1 ? checksumMatches[0] : null;
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static async Task<Dictionary<string, DocumentSyntaxSnapshot>> ReadSyntaxDocumentsAsync(
        Project project,
        HashSet<string> changedPaths,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, DocumentSyntaxSnapshot>(PathComparer);
        foreach (var document in project.Documents)
        {
            if (document.FilePath is null)
            {
                continue;
            }

            var path = Path.GetFullPath(document.FilePath);
            if (!changedPaths.Contains(path))
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var text = await document.GetTextAsync(cancellationToken);
            if (root is null)
            {
                throw new InvalidOperationException($"The syntax root is unavailable for '{path}'.");
            }

            result.Add(path, new(path, root, text, text.GetChecksum()));
        }

        return result;
    }

    private static UpdatedMethodMappingResult MapUpdatedMethod(
        ImmutableArray<PortableSequencePoint> oldPoints,
        ImmutableArray<PortableSequencePoint> newPoints,
        Dictionary<string, DocumentSyntaxSnapshot> committedDocuments,
        Dictionary<string, DocumentSyntaxSnapshot> updatedDocuments)
    {
        if (oldPoints.IsEmpty)
        {
            return UpdatedMethodMappingResult.Ok([]);
        }

        var methodToken = oldPoints[0].MethodToken;
        var oldAnchors = oldPoints.Select(point => CreateSyntaxAnchor(point, committedDocuments)).ToArray();
        var newAnchors = newPoints.Select(point => CreateSyntaxAnchor(point, updatedDocuments)).ToArray();
        var mappedIndexes = new Dictionary<int, int>();
        for (var oldIndex = 0; oldIndex < oldPoints.Length; oldIndex++)
        {
            var anchor = oldAnchors[oldIndex];
            if (anchor is null || oldAnchors.Count(candidate => candidate == anchor) != 1)
            {
                continue;
            }

            var matches = newAnchors
                .Select((candidate, candidateIndex) => (candidate, candidateIndex))
                .Where(item => item.candidate == anchor)
                .Select(item => item.candidateIndex)
                .ToArray();
            if (matches.Length == 1)
            {
                mappedIndexes.Add(oldIndex, matches[0]);
            }
        }

        var orderedExactMappings = mappedIndexes.OrderBy(pair => pair.Key).ToArray();
        if (!IsStrictlyIncreasing(orderedExactMappings.Select(pair => pair.Value)))
        {
            return UpdatedMethodMappingResult.Fail(
                $"Updated method 0x{methodToken:x8} reordered exact syntax anchors and cannot be mapped unambiguously.");
        }

        var usedNewIndexes = mappedIndexes.Values.ToHashSet();
        for (var oldIndex = 0; oldIndex < oldPoints.Length; oldIndex++)
        {
            if (mappedIndexes.ContainsKey(oldIndex))
            {
                continue;
            }

            var lowerBound = mappedIndexes
                .Where(pair => pair.Key < oldIndex)
                .Select(pair => pair.Value)
                .DefaultIfEmpty(-1)
                .Max();
            var upperBound = mappedIndexes
                .Where(pair => pair.Key > oldIndex)
                .Select(pair => pair.Value)
                .DefaultIfEmpty(newPoints.Length)
                .Min();
            var candidates = Enumerable.Range(0, newPoints.Length)
                .Where(newIndex =>
                    newIndex > lowerBound &&
                    newIndex < upperBound &&
                    !usedNewIndexes.Contains(newIndex) &&
                    oldAnchors[oldIndex] is not null &&
                    newAnchors[newIndex] is not null &&
                    HasEqualVisibleSequencePointStructure(
                        oldPoints[oldIndex],
                        newPoints[newIndex],
                        oldAnchors[oldIndex],
                        newAnchors[newIndex]))
                .ToArray();
            if (candidates.Length != 1)
            {
                return UpdatedMethodMappingResult.Fail(
                    $"Updated method 0x{methodToken:x8} has an ambiguous sequence-point mapping for old point {oldIndex}.");
            }

            mappedIndexes.Add(oldIndex, candidates[0]);
            usedNewIndexes.Add(candidates[0]);
        }

        var orderedMappings = mappedIndexes.OrderBy(pair => pair.Key).ToArray();
        if (orderedMappings.Length != oldPoints.Length ||
            !IsStrictlyIncreasing(orderedMappings.Select(pair => pair.Value)))
        {
            return UpdatedMethodMappingResult.Fail(
                $"Updated method 0x{methodToken:x8} does not have a complete, ordered, unique sequence-point mapping.");
        }

        return UpdatedMethodMappingResult.Ok(orderedMappings
            .Select(pair => new SequencePointPair(oldPoints[pair.Key], newPoints[pair.Value]))
            .ToImmutableArray());
    }

    private static bool HasEqualVisibleSequencePointStructure(
        PortableSequencePoint oldPoint,
        PortableSequencePoint newPoint,
        SyntaxAnchor? oldAnchor,
        SyntaxAnchor? newAnchor) =>
        PathComparer.Equals(oldPoint.FilePath, newPoint.FilePath) &&
        oldPoint.StartColumn == newPoint.StartColumn &&
        oldPoint.EndLine - oldPoint.StartLine == newPoint.EndLine - newPoint.StartLine &&
        oldAnchor?.Kind == newAnchor?.Kind;

    private static bool IsStrictlyIncreasing(IEnumerable<int> values)
    {
        var previous = -1;
        foreach (var value in values)
        {
            if (value <= previous)
            {
                return false;
            }

            previous = value;
        }

        return true;
    }

    private static SyntaxAnchor? CreateSyntaxAnchor(
        PortableSequencePoint point,
        Dictionary<string, DocumentSyntaxSnapshot> documents)
    {
        if (!documents.TryGetValue(Path.GetFullPath(point.FilePath), out var document) ||
            point.StartLine < 0 ||
            point.StartLine >= document.Text.Lines.Count)
        {
            return null;
        }

        var line = document.Text.Lines[point.StartLine];
        var position = Math.Clamp(line.Start + Math.Max(point.StartColumn - 1, 0), line.Start, line.End);
        var token = document.Root.FindToken(position);
        var method = token.Parent?.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        if (method?.Body is not null)
        {
            if (token == method.Body.OpenBraceToken)
            {
                return new("MethodOpenBrace", "{");
            }

            if (token == method.Body.CloseBraceToken)
            {
                return new("MethodCloseBrace", "}");
            }
        }

        var syntax = token.Parent?.AncestorsAndSelf().FirstOrDefault(static node =>
            node is StatementSyntax or ArrowExpressionClauseSyntax);
        return syntax is null
            ? null
            : new(syntax.Kind().ToString(), NormalizeSyntax(syntax));
    }

    private static string NormalizeSyntax(SyntaxNode syntax) =>
        string.Concat(syntax.WithoutTrivia().ToFullString().Where(static character => !char.IsWhiteSpace(character)));

    private static string? AddLineMapping(
        Dictionary<string, Dictionary<int, int>> mappings,
        Dictionary<string, Dictionary<int, int>> reverseMappings,
        PortableSequencePoint oldPoint,
        PortableSequencePoint newPoint)
    {
        if (!PathComparer.Equals(Path.GetFullPath(oldPoint.FilePath), Path.GetFullPath(newPoint.FilePath)))
        {
            return $"Sequence point in updated method 0x{oldPoint.MethodToken:x8} moved to another document.";
        }

        if (!mappings.TryGetValue(oldPoint.FilePath, out var fileMappings))
        {
            fileMappings = new Dictionary<int, int>();
            mappings.Add(oldPoint.FilePath, fileMappings);
            reverseMappings.Add(oldPoint.FilePath, new Dictionary<int, int>());
        }

        var fileReverseMappings = reverseMappings[oldPoint.FilePath];
        if (fileMappings.TryGetValue(oldPoint.StartLine, out var existingNewLine) && existingNewLine != newPoint.StartLine)
        {
            return $"Source line {oldPoint.StartLine + 1} maps to multiple updated lines in '{oldPoint.FilePath}'.";
        }

        if (fileReverseMappings.TryGetValue(newPoint.StartLine, out var existingOldLine) && existingOldLine != oldPoint.StartLine)
        {
            return $"Updated line {newPoint.StartLine + 1} maps from multiple source lines in '{oldPoint.FilePath}'.";
        }

        fileMappings[oldPoint.StartLine] = newPoint.StartLine;
        fileReverseMappings[newPoint.StartLine] = oldPoint.StartLine;
        return null;
    }

    private static ImmutableArray<PortableSequencePoint> ReadSequencePoints(
        ImmutableArray<byte> pdbImage)
    {
        using var provider = MetadataReaderProvider.FromPortablePdbImage(pdbImage);
        var reader = provider.GetMetadataReader();
        var result = ImmutableArray.CreateBuilder<PortableSequencePoint>();
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
                result.Add(new(
                    methodToken,
                    point.Offset,
                    filePath,
                    point.StartLine - 1,
                    point.StartColumn,
                    point.EndLine - 1,
                    point.EndColumn,
                    point.IsHidden,
                    reader.GetBlobBytes(document.Hash).ToImmutableArray()));
            }
        }

        return result.ToImmutable();
    }

    private static string? ValidateMethodTokenIdentity(
        ImmutableArray<byte> committedPe,
        ImmutableArray<byte> updatedPe)
    {
        var committedMethods = ReadMethodTokenIdentities(committedPe);
        var updatedMethods = ReadMethodTokenIdentities(updatedPe);
        if (committedMethods.Count != updatedMethods.Count)
        {
            return "The edit changes the method-definition table. Exact relocation requires restart until full-PDB method tokens can be correlated with EnC runtime tokens.";
        }

        foreach (var (token, identity) in committedMethods)
        {
            if (!updatedMethods.TryGetValue(token, out var updatedIdentity) ||
                !string.Equals(identity, updatedIdentity, StringComparison.Ordinal))
            {
                return $"Method token 0x{token:x8} changed identity in the full portable-PDB emit. Exact relocation requires restart to preserve runtime token alignment.";
            }
        }

        return null;
    }

    private static Dictionary<int, string> ReadMethodTokenIdentities(ImmutableArray<byte> peImage)
    {
        using var peReader = new PEReader(peImage);
        var reader = peReader.GetMetadataReader();
        var result = new Dictionary<int, string>();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var typeName = GetQualifiedTypeName(reader, typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var token = MetadataTokens.GetToken(methodHandle);
                var signature = Convert.ToHexString(reader.GetBlobBytes(method.Signature));
                result.Add(token, $"{typeName}::{reader.GetString(method.Name)}:{signature}");
            }
        }

        return result;
    }

    private static string GetQualifiedTypeName(MetadataReader reader, TypeDefinitionHandle typeHandle)
    {
        var names = new Stack<string>();
        var current = typeHandle;
        string? namespaceName = null;
        while (!current.IsNil)
        {
            var type = reader.GetTypeDefinition(current);
            names.Push(reader.GetString(type.Name));
            var currentNamespace = reader.GetString(type.Namespace);
            if (!string.IsNullOrEmpty(currentNamespace))
            {
                namespaceName = currentNamespace;
            }

            current = type.GetDeclaringType();
        }

        var nestedName = string.Join("+", names);
        return string.IsNullOrEmpty(namespaceName) ? nestedName : $"{namespaceName}.{nestedName}";
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
        int StartColumn,
        int EndLine,
        int EndColumn,
        bool Hidden,
        ImmutableArray<byte> DocumentChecksum);

    private sealed record DocumentSyntaxSnapshot(
        string FilePath,
        SyntaxNode Root,
        SourceText Text,
        ImmutableArray<byte> Checksum);

    private sealed record SyntaxAnchor(string Kind, string Text);

    private sealed record SequencePointPair(PortableSequencePoint Old, PortableSequencePoint New);

    private sealed record UpdatedMethodMappingResult(
        bool Success,
        ImmutableArray<SequencePointPair> Points,
        string? Error)
    {
        public static UpdatedMethodMappingResult Ok(ImmutableArray<SequencePointPair> points) =>
            new(true, points, null);

        public static UpdatedMethodMappingResult Fail(string error) =>
            new(false, [], error);
    }

    private sealed record ExactLineUpdateResult(
        bool Success,
        ImmutableArray<HotReloadDeltaLineUpdate> LineUpdates,
        bool IncludesUpdatedMethods,
        string? Error)
    {
        public static ExactLineUpdateResult Ok(
            ImmutableArray<HotReloadDeltaLineUpdate> lineUpdates,
            bool includesUpdatedMethods) =>
            new(true, lineUpdates, includesUpdatedMethods, null);

        public static ExactLineUpdateResult Fail(string error) =>
            new(false, [], false, error);
    }

    private sealed record FullPdbResult(
        bool Success,
        ImmutableArray<byte> Pe,
        ImmutableArray<byte> Pdb,
        string? Error)
    {
        public static FullPdbResult Ok(ImmutableArray<byte> pe, ImmutableArray<byte> pdb) =>
            new(true, pe, pdb, null);

        public static FullPdbResult Fail(string error) => new(false, [], [], error);
    }
}
