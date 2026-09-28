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
    string ModuleName)
{
    public Guid ModuleId { get; init; }
}

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
    private Solution pdbSolution;
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
        pdbSolution = solution;
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
        try
        {
            var baselinePe = (await File.ReadAllBytesAsync(outputAssembly, cancellationToken)).ToImmutableArray();
            var baselineSymbols = CapturePortablePdb(outputAssembly, baselinePe);
            var pdbPath = baselineSymbols.Path;
            var baselinePdb = baselineSymbols.Image;
            var info = new HotReloadDeltaSessionInfo(
                builder.ProjectPath,
                configuration,
                targetFramework,
                outputAssembly,
                pdbPath,
                Path.GetFileName(outputAssembly))
            {
                ModuleId = ReadModuleId(baselinePe)
            };
            return new HotReloadDeltaSession(
                artifacts.HotReloadService,
                artifacts.Workspace,
                artifacts.BaselineSolution,
                artifacts.BaselineProjectId,
                baselinePe,
                baselinePdb,
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

        var normalizedChanges = new List<HotReloadDeltaDocumentChange>(changes.Count);
        var changedPaths = new HashSet<string>(PathComparer);
        foreach (var change in changes)
        {
            var path = Path.GetFullPath(change.FilePath);
            var project = solution.GetProject(projectId)!;
            var matchingDocuments = project.Documents
                .Cast<TextDocument>()
                .Concat(project.AdditionalDocuments)
                .Where(candidate =>
                    candidate.FilePath is not null &&
                    PathsReferToSameFile(candidate.FilePath, path))
                .ToArray();
            if (matchingDocuments.Length == 1)
            {
                path = Path.GetFullPath(matchingDocuments[0].FilePath!);
            }
            else if (matchingDocuments.Length > 1)
            {
                throw new ArgumentException(
                    $"A changed document path resolves to multiple project documents: {path}",
                    nameof(changes));
            }

            if (!changedPaths.Add(path))
            {
                throw new ArgumentException(
                    $"A document may appear only once in an update: {path}",
                    nameof(changes));
            }

            normalizedChanges.Add(new(path, change.Text));
        }

        var updatedSolution = solution;
        var changedFiles = ImmutableArray.CreateBuilder<string>(normalizedChanges.Count);
        var changedDocuments = ImmutableArray.CreateBuilder<HotReloadDeltaChangedDocumentEvidence>(normalizedChanges.Count);
        var hasTextChanges = false;
        foreach (var change in normalizedChanges)
        {
            var path = change.FilePath;
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
                return RestartRequired(normalizedChanges, $"Document is not part of the baseline project: {path}");
            }

            var oldText = await document.GetTextAsync(cancellationToken);
            var newText = SourceText.From(change.Text, Encoding.UTF8);
            if (oldText.ContentEquals(newText))
            {
                continue;
            }

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

        var runtimeDocumentChanges = await AnalyzeRuntimeDocumentChangesAsync(
            pdbSolution.GetProject(projectId)!,
            updatedSolution.GetProject(projectId)!,
            cancellationToken);
        if (!runtimeDocumentChanges.Success)
        {
            return RestartRequired(normalizedChanges, runtimeDocumentChanges.Error!);
        }

        var runtimeChangedFiles = runtimeDocumentChanges.Files
            .Concat(runtimeDocumentChanges.GeneratedFiles)
            .ToHashSet(PathComparer);

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
            // Roslyn deliberately creates no pending update for NoChangesToApply, so there is
            // nothing to commit or discard. Advance only our source snapshot so later hashes and
            // edits are based on the text Roslyn just evaluated.
            solution = updatedSolution;

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
            return RestartRequired(normalizedChanges, "Hot Reload delta v1 supports exactly one emitting project per update.");
        }

        try
        {
            var update = updates.ProjectUpdates[0];
            var updatedMethods = GetUpdatedMethodTokens(update.MetadataDelta);
            var fullPdb = await EmitFullPdbAsync(updatedSolution.GetProject(projectId)!, cancellationToken);
            if (!fullPdb.Success)
            {
                hotReloadService.DiscardUpdate();
                return RestartRequired(normalizedChanges, fullPdb.Error!);
            }

            var tokenValidation = ValidateMethodTokenIdentity(baselinePe, fullPdb.Pe);
            if (tokenValidation is not null)
            {
                hotReloadService.DiscardUpdate();
                return RestartRequired(normalizedChanges, tokenValidation);
            }

            ImmutableArray<HotReloadDeltaLineUpdate> lineUpdates = [];
            var includesUpdatedMethodMappings = false;
            if (runtimeChangedFiles.Count > 0)
            {
                var mapping = await CreateExactLineUpdatesAsync(
                    pdbSolution.GetProject(projectId)!,
                    updatedSolution.GetProject(projectId)!,
                    fullPdb.Pdb,
                    runtimeChangedFiles.ToImmutableArray(),
                    updatedMethods,
                    cancellationToken);
                if (!mapping.Success)
                {
                    hotReloadService.DiscardUpdate();
                    return RestartRequired(normalizedChanges, mapping.Error!);
                }

                lineUpdates = mapping.LineUpdates;
                includesUpdatedMethodMappings = mapping.IncludesUpdatedMethods;
            }

            if (!runtimeDocumentChanges.AdditionalFiles.IsEmpty)
            {
                var additionalValidation = await ValidateAdditionalDocumentPositionsAsync(
                    pdbSolution.GetProject(projectId)!,
                    updatedSolution.GetProject(projectId)!,
                    fullPdb.Pdb,
                    runtimeDocumentChanges.AdditionalFiles,
                    cancellationToken);
                if (!additionalValidation.Success)
                {
                    hotReloadService.DiscardUpdate();
                    return RestartRequired(normalizedChanges, additionalValidation.Error!);
                }
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
                !lineUpdates.IsEmpty
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
        pdbSolution = pendingSolution;
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

    private static bool ContainsUnaccountedLineDirective(
        SourceText text,
        IEnumerable<string> changedAdditionalFiles)
    {
        var accountedPaths = changedAdditionalFiles.ToHashSet(PathComparer);
        return CSharpSyntaxTree.ParseText(text)
            .GetRoot()
            .DescendantTrivia(descendIntoTrivia: true)
            .Where(static trivia => trivia.IsKind(SyntaxKind.LineDirectiveTrivia))
            .Select(static trivia => (LineDirectiveTriviaSyntax)trivia.GetStructure()!)
            .Any(directive =>
            {
                if (directive.Line.IsKind(SyntaxKind.DefaultKeyword) ||
                    directive.Line.IsKind(SyntaxKind.HiddenKeyword))
                {
                    return false;
                }

                var mappedPath = TryGetFullPath(directive.File.ValueText);
                return mappedPath is null || !accountedPaths.Contains(mappedPath);
            });
    }

    private static async Task<RuntimeDocumentChangeAnalysisResult> AnalyzeRuntimeDocumentChangesAsync(
        Project pdbProject,
        Project updatedProject,
        CancellationToken cancellationToken)
    {
        var changedFiles = ImmutableArray.CreateBuilder<string>();
        foreach (var updatedDocument in updatedProject.Documents)
        {
            var pdbDocument = pdbProject.GetDocument(updatedDocument.Id);
            if (pdbDocument is null || updatedDocument.FilePath is null)
            {
                continue;
            }

            var pdbText = await pdbDocument.GetTextAsync(cancellationToken);
            var updatedText = await updatedDocument.GetTextAsync(cancellationToken);
            if (pdbText.ContentEquals(updatedText))
            {
                continue;
            }

            if (ContainsLineDirective(pdbText) || ContainsLineDirective(updatedText))
            {
                return RuntimeDocumentChangeAnalysisResult.Fail(
                    "Line-moving and #line-mapped edits require restart/replay until exact Roslyn sequence-point updates are exposed.");
            }

            changedFiles.Add(Path.GetFullPath(updatedDocument.FilePath));
        }

        var additionalFiles = ImmutableArray.CreateBuilder<string>();
        foreach (var updatedDocument in updatedProject.AdditionalDocuments)
        {
            var pdbDocument = pdbProject.GetAdditionalDocument(updatedDocument.Id);
            if (pdbDocument is null || updatedDocument.FilePath is null)
            {
                continue;
            }

            var pdbText = await pdbDocument.GetTextAsync(cancellationToken);
            var updatedText = await updatedDocument.GetTextAsync(cancellationToken);
            if (!pdbText.ContentEquals(updatedText))
            {
                additionalFiles.Add(Path.GetFullPath(updatedDocument.FilePath));
            }
        }

        var generatedFiles = ImmutableArray.CreateBuilder<string>();
        var committedGeneratedDocuments = await pdbProject.GetSourceGeneratedDocumentsAsync(cancellationToken);
        var updatedGeneratedDocuments = await updatedProject.GetSourceGeneratedDocumentsAsync(cancellationToken);
        var committedGeneratedById = committedGeneratedDocuments.ToDictionary(static document => document.Id);
        var updatedGeneratedById = updatedGeneratedDocuments.ToDictionary(static document => document.Id);
        foreach (var committedDocument in committedGeneratedDocuments)
        {
            if (!updatedGeneratedById.ContainsKey(committedDocument.Id))
            {
                return RuntimeDocumentChangeAnalysisResult.Fail(
                    $"Source-generated document '{committedDocument.Name}' was removed and cannot be correlated exactly; restart/replay is required.");
            }
        }

        foreach (var updatedDocument in updatedGeneratedDocuments)
        {
            if (!committedGeneratedById.TryGetValue(updatedDocument.Id, out var committedDocument))
            {
                return RuntimeDocumentChangeAnalysisResult.Fail(
                    $"Source-generated document '{updatedDocument.Name}' was added and cannot be correlated exactly; restart/replay is required.");
            }

            var committedText = await committedDocument.GetTextAsync(cancellationToken);
            var updatedText = await updatedDocument.GetTextAsync(cancellationToken);
            if (committedText.ContentEquals(updatedText))
            {
                continue;
            }

            if (ContainsUnaccountedLineDirective(committedText, additionalFiles) ||
                ContainsUnaccountedLineDirective(updatedText, additionalFiles))
            {
                return RuntimeDocumentChangeAnalysisResult.Fail(
                    $"Changed source-generated document '{updatedDocument.Name}' contains #line mappings that are not owned by a changed additional document and cannot be correlated exactly; restart/replay is required.");
            }

            var generatedPath = TryGetFullPath(updatedDocument.FilePath ?? string.Empty);
            if (generatedPath is null)
            {
                return RuntimeDocumentChangeAnalysisResult.Fail(
                    $"Changed source-generated document '{updatedDocument.Name}' does not have a stable physical or PDB path; restart/replay is required.");
            }

            generatedFiles.Add(generatedPath);
        }

        return RuntimeDocumentChangeAnalysisResult.Ok(
            changedFiles.ToImmutable(),
            additionalFiles.ToImmutable(),
            generatedFiles.ToImmutable());
    }

    private static PortablePdbCapture CapturePortablePdb(string assemblyPath, ImmutableArray<byte> peImage)
    {
        using var peReader = new PEReader(peImage);
        var references = peReader.ReadDebugDirectory()
            .Where(static entry => entry.Type == DebugDirectoryEntryType.CodeView)
            .Select(entry =>
            {
                var data = peReader.ReadCodeViewDebugDirectoryData(entry);
                var path = Path.GetFullPath(
                    Path.IsPathRooted(data.Path)
                        ? data.Path
                        : Path.Combine(Path.GetDirectoryName(assemblyPath)!, data.Path));
                return new PortablePdbReference(path, data.Guid, entry.Stamp);
            })
            .ToArray();
        var matchingReferencedPdbs = references
            .Where(reference => File.Exists(reference.Path))
            .Select(reference => TryCapturePortablePdb(reference.Path, [reference]))
            .Where(static capture => capture is not null)
            .Select(static capture => capture!)
            .DistinctBy(static capture => capture.Path, PathComparer)
            .ToArray();
        if (matchingReferencedPdbs.Length == 1)
        {
            return matchingReferencedPdbs[0];
        }

        var siblingPdb = Path.ChangeExtension(assemblyPath, ".pdb");
        if (matchingReferencedPdbs.Length == 0 &&
            references.Length > 0 &&
            File.Exists(siblingPdb))
        {
            var siblingCapture = TryCapturePortablePdb(siblingPdb, references);
            if (siblingCapture is not null)
            {
                return siblingCapture;
            }
        }

        throw new InvalidOperationException(
            "The baseline assembly does not identify exactly one matching external portable PDB.");
    }

    private static PortablePdbCapture? TryCapturePortablePdb(
        string pdbPath,
        IReadOnlyList<PortablePdbReference> references)
    {
        try
        {
            var image = File.ReadAllBytes(pdbPath).ToImmutableArray();
            using var provider = MetadataReaderProvider.FromPortablePdbImage(image);
            var header = provider.GetMetadataReader().DebugMetadataHeader;
            if (header is null)
            {
                return null;
            }

            var contentId = new BlobContentId(header.Id);
            return references.Any(reference =>
                reference.Guid == contentId.Guid &&
                reference.Stamp == contentId.Stamp)
                ? new(Path.GetFullPath(pdbPath), image, contentId.Guid, contentId.Stamp)
                : null;
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException)
        {
            return null;
        }
    }

    private static Guid ReadModuleId(ImmutableArray<byte> peImage)
    {
        using var peReader = new PEReader(peImage);
        var metadata = peReader.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
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
        var oldPdb = ReadPortablePdbData(baselinePdb);
        var newPdb = ReadPortablePdbData(updatedPdb);
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

        var committedChecksums = committedDocuments.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Checksum,
            PathComparer);
        var updatedChecksums = updatedDocuments.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Checksum,
            PathComparer);
        var resolvedOldDocuments = ResolveChangedDocumentPaths(
            oldPdb.Documents,
            changedPaths,
            committedChecksums);
        var resolvedNewDocuments = ResolveChangedDocumentPaths(
            newPdb.Documents,
            changedPaths,
            updatedChecksums);
        var resolvedOldPoints = ResolveChangedDocumentPoints(
            oldPdb.SequencePoints,
            changedPaths,
            committedChecksums);
        var resolvedNewPoints = ResolveChangedDocumentPoints(
            newPdb.SequencePoints,
            changedPaths,
            updatedChecksums);
        var unresolvedOldPaths = FindMissingPaths(
            changedPaths,
            resolvedOldDocuments);
        var unresolvedNewPaths = FindMissingPaths(
            changedPaths,
            resolvedNewDocuments);
        if (!unresolvedOldPaths.IsEmpty || !unresolvedNewPaths.IsEmpty)
        {
            var unresolvedPaths = unresolvedOldPaths.Concat(unresolvedNewPaths).Distinct(PathComparer);
            return ExactLineUpdateResult.Fail(
                $"Exact sequence-point relocation could not resolve every line-moving document: {string.Join(", ", unresolvedPaths)}");
        }

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
        return ExactLineUpdateResult.Ok(lineUpdates, includesUpdatedMethods);
    }

    private async Task<AdditionalDocumentValidationResult> ValidateAdditionalDocumentPositionsAsync(
        Project committedProject,
        Project updatedProject,
        ImmutableArray<byte> updatedPdb,
        ImmutableArray<string> changedFiles,
        CancellationToken cancellationToken)
    {
        var changedPaths = changedFiles
            .Select(Path.GetFullPath)
            .ToHashSet(PathComparer);
        var committedDocuments = await ReadAdditionalDocumentSnapshotsAsync(
            committedProject,
            changedPaths,
            cancellationToken);
        var updatedDocuments = await ReadAdditionalDocumentSnapshotsAsync(
            updatedProject,
            changedPaths,
            cancellationToken);
        var committedChecksums = committedDocuments.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Checksum,
            PathComparer);
        var updatedChecksums = updatedDocuments.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Checksum,
            PathComparer);
        var oldPdb = ReadPortablePdbData(baselinePdb);
        var newPdb = ReadPortablePdbData(updatedPdb);
        var resolvedOldDocuments = ResolveChangedDocumentPaths(
            oldPdb.Documents,
            changedPaths,
            committedChecksums);
        var resolvedNewDocuments = ResolveChangedDocumentPaths(
            newPdb.Documents,
            changedPaths,
            updatedChecksums);
        var unresolvedOldPaths = FindMissingPaths(changedPaths, resolvedOldDocuments);
        var unresolvedNewPaths = FindMissingPaths(changedPaths, resolvedNewDocuments);
        if (!unresolvedOldPaths.IsEmpty || !unresolvedNewPaths.IsEmpty)
        {
            var unresolvedPaths = unresolvedOldPaths.Concat(unresolvedNewPaths).Distinct(PathComparer);
            return AdditionalDocumentValidationResult.Fail(
                $"Generated or additional document positions could not be resolved in both portable PDB generations: {string.Join(", ", unresolvedPaths)}");
        }

        var resolvedOldPoints = ResolveChangedDocumentPoints(
            oldPdb.SequencePoints,
            changedPaths,
            committedChecksums);
        var resolvedNewPoints = ResolveChangedDocumentPoints(
            newPdb.SequencePoints,
            changedPaths,
            updatedChecksums);
        foreach (var path in changedPaths)
        {
            var oldPoints = resolvedOldPoints
                .Where(point => !point.Hidden && PathComparer.Equals(point.FilePath, path))
                .OrderBy(static point => point.MethodToken)
                .ThenBy(static point => point.IlOffset)
                .ToArray();
            var newPoints = resolvedNewPoints
                .Where(point => !point.Hidden && PathComparer.Equals(point.FilePath, path))
                .OrderBy(static point => point.MethodToken)
                .ThenBy(static point => point.IlOffset)
                .ToArray();
            string? identityError = null;
            if (!committedDocuments.TryGetValue(path, out var committedDocument) ||
                !updatedDocuments.TryGetValue(path, out var updatedDocument) ||
                !HasExactAdditionalDocumentPointIdentity(
                    oldPoints,
                    newPoints,
                    committedDocument.Text,
                    updatedDocument.Text,
                    out identityError))
            {
                return AdditionalDocumentValidationResult.Fail(
                    $"Visible sequence-point positions or identities changed ambiguously in generated or additional document '{path}' and cannot be correlated exactly ({identityError ?? "document evidence was unavailable"}); restart/replay is required.");
            }
        }

        return AdditionalDocumentValidationResult.Ok();
    }

    private static async Task<Dictionary<string, AdditionalDocumentSnapshot>> ReadAdditionalDocumentSnapshotsAsync(
        Project project,
        IReadOnlySet<string> changedPaths,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, AdditionalDocumentSnapshot>(PathComparer);
        foreach (var document in project.AdditionalDocuments)
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

            var text = await document.GetTextAsync(cancellationToken);
            result.Add(path, new(path, text, text.GetChecksum()));
        }

        return result;
    }

    private static bool HasExactAdditionalDocumentPointIdentity(
        IReadOnlyList<PortableSequencePoint> oldPoints,
        IReadOnlyList<PortableSequencePoint> newPoints,
        SourceText oldText,
        SourceText newText,
        out string? error)
    {
        if (oldPoints.Count != newPoints.Count)
        {
            error = $"visible point count changed from {oldPoints.Count} to {newPoints.Count}";
            return false;
        }

        var oldExactAnchors = oldPoints.Select(point => CreateSourcePointAnchor(point, oldText)).ToArray();
        var newExactAnchors = newPoints.Select(point => CreateSourcePointAnchor(point, newText)).ToArray();
        var oldIdentityAnchors = oldPoints.Select(point => CreateSourcePointIdentityAnchor(point, oldText)).ToArray();
        var newIdentityAnchors = newPoints.Select(point => CreateSourcePointIdentityAnchor(point, newText)).ToArray();
        var structurallyMatchedIndexes = new List<int>();
        for (var index = 0; index < oldPoints.Count; index++)
        {
            var oldPoint = oldPoints[index];
            var newPoint = newPoints[index];
            if (oldPoint.MethodToken != newPoint.MethodToken ||
                oldPoint.StartLine != newPoint.StartLine ||
                oldPoint.EndLine != newPoint.EndLine)
            {
                error = $"point {index} changed method or source line span";
                return false;
            }

            var oldExactAnchor = oldExactAnchors[index];
            var newExactAnchor = newExactAnchors[index];
            var oldIdentityAnchor = oldIdentityAnchors[index];
            var newIdentityAnchor = newIdentityAnchors[index];
            if (oldExactAnchor is null ||
                newExactAnchor is null ||
                oldIdentityAnchor is null ||
                newIdentityAnchor is null)
            {
                error = $"point {index} lacks complete source identity evidence";
                return false;
            }

            if (string.Equals(oldExactAnchor, newExactAnchor, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(oldIdentityAnchor, newIdentityAnchor, StringComparison.Ordinal))
            {
                error = $"point {index} does not retain a one-to-one source identity";
                return false;
            }

            structurallyMatchedIndexes.Add(index);
        }

        foreach (var group in structurallyMatchedIndexes.GroupBy(index => oldIdentityAnchors[index], StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                error = $"points {string.Join(", ", group)} share a non-unique changed source identity";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static string? CreateSourcePointIdentityAnchor(PortableSequencePoint point, SourceText text)
    {
        var source = ReadSourcePointText(point, text);
        if (source is null)
        {
            return null;
        }

        var tokens = SyntaxFactory.ParseTokens(source)
            .Where(static token => !token.IsKind(SyntaxKind.EndOfFileToken))
            .Select(static token => token.IsKind(SyntaxKind.IdentifierToken)
                ? $"identifier:{token.ValueText}"
                : token.Kind().ToString())
            .ToArray();
        return tokens.Length == 0 ? null : string.Join('|', tokens);
    }

    private static string? CreateSourcePointAnchor(PortableSequencePoint point, SourceText text)
    {
        var source = ReadSourcePointText(point, text);
        return source is null
            ? null
            : string.Concat(source.Where(static character => !char.IsWhiteSpace(character)));
    }

    private static string? ReadSourcePointText(PortableSequencePoint point, SourceText text)
    {
        if (point.StartLine < 0 ||
            point.StartLine >= text.Lines.Count ||
            point.EndLine < point.StartLine ||
            point.EndLine >= text.Lines.Count)
        {
            return null;
        }

        var startLine = text.Lines[point.StartLine];
        var endLine = text.Lines[point.EndLine];
        var start = Math.Clamp(startLine.Start + Math.Max(point.StartColumn - 1, 0), startLine.Start, startLine.End);
        var end = Math.Clamp(endLine.Start + Math.Max(point.EndColumn - 1, 0), endLine.Start, endLine.End);
        if (end <= start)
        {
            return null;
        }

        return text.ToString(TextSpan.FromBounds(start, end));
    }

    private static ImmutableArray<string> FindMissingPaths(
        IEnumerable<string> requiredPaths,
        IEnumerable<string> observedPaths)
    {
        var observed = observedPaths.ToHashSet(PathComparer);
        return requiredPaths
            .Where(path => !observed.Contains(path))
            .Distinct(PathComparer)
            .ToImmutableArray();
    }

    private static ImmutableArray<PortableSequencePoint> ResolveChangedDocumentPoints(
        ImmutableArray<PortableSequencePoint> points,
        HashSet<string> changedPaths,
        IReadOnlyDictionary<string, ImmutableArray<byte>> documentChecksums)
    {
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

    private static ImmutableArray<string> ResolveChangedDocumentPaths(
        ImmutableArray<PortablePdbDocument> documents,
        IReadOnlySet<string> changedPaths,
        IReadOnlyDictionary<string, ImmutableArray<byte>> documentChecksums) =>
        documents
            .Select(document => ResolveChangedDocumentPath(
                document.FilePath,
                document.Checksum,
                changedPaths,
                documentChecksums))
            .Where(static path => path is not null)
            .Select(static path => path!)
            .Distinct(PathComparer)
            .ToImmutableArray();

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

    private static bool PathsReferToSameFile(string first, string second) =>
        PathComparer.Equals(
            ResolvePathThroughExistingLinks(first),
            ResolvePathThroughExistingLinks(second));

    private static string ResolvePathThroughExistingLinks(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"Path does not have a filesystem root: {path}");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate)
                    ? new FileInfo(candidate)
                    : null;
            if (info is null)
            {
                current = candidate;
                continue;
            }

            current = Path.GetFullPath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName);
        }

        return current;
    }

    private static async Task<Dictionary<string, DocumentSyntaxSnapshot>> ReadSyntaxDocumentsAsync(
        Project project,
        HashSet<string> changedPaths,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, DocumentSyntaxSnapshot>(PathComparer);
        var sourceGeneratedDocuments = await project.GetSourceGeneratedDocumentsAsync(cancellationToken);
        foreach (var document in project.Documents.Concat<Microsoft.CodeAnalysis.Document>(sourceGeneratedDocuments))
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

    private static PortablePdbData ReadPortablePdbData(
        ImmutableArray<byte> pdbImage)
    {
        using var provider = MetadataReaderProvider.FromPortablePdbImage(pdbImage);
        var reader = provider.GetMetadataReader();
        var documents = reader.Documents
            .Select(handle =>
            {
                var document = reader.GetDocument(handle);
                return new PortablePdbDocument(
                    reader.GetString(document.Name),
                    reader.GetBlobBytes(document.Hash).ToImmutableArray());
            })
            .ToImmutableArray();
        var points = ImmutableArray.CreateBuilder<PortableSequencePoint>();
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
                points.Add(new(
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

        return new(documents, points.ToImmutable());
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
                var signature = method.DecodeSignature(MethodIdentitySignatureTypeProvider.Instance, genericContext: null);
                var parameterTypes = string.Join(",", signature.ParameterTypes);
                result.Add(
                    token,
                    $"{typeName}::{reader.GetString(method.Name)}`{signature.GenericParameterCount}" +
                    $"({parameterTypes})->{signature.ReturnType}" +
                    $":required={signature.RequiredParameterCount}:header={signature.Header.RawValue}");
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

    private sealed class MethodIdentitySignatureTypeProvider : ISignatureTypeProvider<string, object?>
    {
        public static MethodIdentitySignatureTypeProvider Instance { get; } = new();

        public string GetArrayType(string elementType, ArrayShape shape) =>
            $"{elementType}[rank={shape.Rank};sizes={string.Join(",", shape.Sizes)};lower={string.Join(",", shape.LowerBounds)}]";

        public string GetByReferenceType(string elementType) => $"{elementType}&";

        public string GetFunctionPointerType(MethodSignature<string> signature) =>
            $"fnptr[header={signature.Header.RawValue};generic={signature.GenericParameterCount};required={signature.RequiredParameterCount}]" +
            $"({string.Join(",", signature.ParameterTypes)})->{signature.ReturnType}";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            $"{genericType}<{string.Join(",", typeArguments)}>";

        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";

        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
            $"{unmodifiedType} mod{(isRequired ? "req" : "opt")}({modifier})";

        public string GetPinnedType(string elementType) => $"{elementType} pinned";

        public string GetPointerType(string elementType) => $"{elementType}*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSZArrayType(string elementType) => $"{elementType}[]";

        public string GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind) =>
            $"def:{GetQualifiedTypeName(reader, handle)}";

        public string GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) =>
            $"ref:{GetQualifiedTypeReferenceName(reader, handle)}";

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        private static string GetQualifiedTypeReferenceName(MetadataReader reader, TypeReferenceHandle handle)
        {
            var type = reader.GetTypeReference(handle);
            var name = reader.GetString(type.Name);
            if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                return $"{GetQualifiedTypeReferenceName(reader, (TypeReferenceHandle)type.ResolutionScope)}+{name}";
            }

            var namespaceName = reader.GetString(type.Namespace);
            var qualifiedName = string.IsNullOrEmpty(namespaceName) ? name : $"{namespaceName}.{name}";
            return $"{GetResolutionScopeIdentity(reader, type.ResolutionScope)}:{qualifiedName}";
        }

        private static string GetResolutionScopeIdentity(MetadataReader reader, EntityHandle scope) => scope.Kind switch
        {
            HandleKind.AssemblyReference => GetAssemblyReferenceIdentity(reader, (AssemblyReferenceHandle)scope),
            HandleKind.ModuleReference => $"module:{reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)scope).Name)}",
            HandleKind.ModuleDefinition => "module:self",
            _ => scope.Kind.ToString()
        };

        private static string GetAssemblyReferenceIdentity(MetadataReader reader, AssemblyReferenceHandle handle)
        {
            var assembly = reader.GetAssemblyReference(handle);
            return $"assembly:{reader.GetString(assembly.Name)},{assembly.Version},{Convert.ToHexString(reader.GetBlobBytes(assembly.PublicKeyOrToken))}";
        }
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

    private sealed record PortablePdbReference(string Path, Guid Guid, uint Stamp);

    private sealed record PortablePdbCapture(
        string Path,
        ImmutableArray<byte> Image,
        Guid Guid,
        uint Stamp);

    private sealed record PortablePdbDocument(
        string FilePath,
        ImmutableArray<byte> Checksum);

    private sealed record PortablePdbData(
        ImmutableArray<PortablePdbDocument> Documents,
        ImmutableArray<PortableSequencePoint> SequencePoints);

    private sealed record DocumentSyntaxSnapshot(
        string FilePath,
        SyntaxNode Root,
        SourceText Text,
        ImmutableArray<byte> Checksum);

    private sealed record AdditionalDocumentSnapshot(
        string FilePath,
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

    private sealed record RuntimeDocumentChangeAnalysisResult(
        bool Success,
        ImmutableArray<string> Files,
        ImmutableArray<string> AdditionalFiles,
        ImmutableArray<string> GeneratedFiles,
        string? Error)
    {
        public static RuntimeDocumentChangeAnalysisResult Ok(
            ImmutableArray<string> files,
            ImmutableArray<string> additionalFiles,
            ImmutableArray<string> generatedFiles) =>
            new(true, files, additionalFiles, generatedFiles, null);

        public static RuntimeDocumentChangeAnalysisResult Fail(string error) =>
            new(false, [], [], [], error);
    }

    private sealed record AdditionalDocumentValidationResult(bool Success, string? Error)
    {
        public static AdditionalDocumentValidationResult Ok() => new(true, null);

        public static AdditionalDocumentValidationResult Fail(string error) => new(false, error);
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
