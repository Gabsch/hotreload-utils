// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.HotReload.Utils.Generator;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace HotReload.DeltaGenerator.Tests;

public sealed class HotReloadDeltaSessionTests
{
    [Fact]
    public async Task Session_ProducesDeltaForLineStableRazorComponentEdit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await RazorProjectFixture.CreateAsync(cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.ComponentPath, RazorProjectFixture.UpdatedSource)
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, update.Status);
        Assert.NotEmpty(update.MetadataDelta);
        Assert.NotEmpty(update.IlDelta);
        Assert.NotEmpty(update.PdbDelta);
        Assert.NotEmpty(update.UpdatedTypes);
        Assert.NotEmpty(update.UpdatedMethods);
        var changedDocument = Assert.Single(update.ChangedDocuments);
        Assert.Equal(fixture.ComponentPath, changedDocument.FilePath);
        Assert.NotEqual(changedDocument.BaselineSha256, changedDocument.UpdatedSha256);
        Assert.True(update.LineUpdatesComplete);
        Assert.True(session.HasPendingUpdate);
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_RejectsLineMovingRazorUpdateWithoutSyntaxIdentityEvidence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await RazorProjectFixture.CreateAsync(cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);
        var updatedSource = RazorProjectFixture.UpdatedSource.Replace(
            "@code {",
            "\n@code {",
            StringComparison.Ordinal);

        var update = await session.PrepareUpdateAsync([
            new(fixture.ComponentPath, updatedSource)
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.False(update.LineUpdatesComplete);
        Assert.Empty(update.MetadataDelta);
        Assert.False(session.HasPendingUpdate);
        Assert.Contains(update.Warnings, warning => warning.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Session_DoesNotTreatRazorMarkupBeginningWithLineAsCSharpDirective()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var baseline = "#line documentation\n" + RazorProjectFixture.BaselineSource;
        using var fixture = await RazorProjectFixture.CreateAsync(cancellationToken, baseline);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.ComponentPath, "#line documentation\n" + RazorProjectFixture.UpdatedSource)
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, update.Status);
        Assert.True(session.HasPendingUpdate);
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_PreparesDiscardsRegeneratesAndCommitsLineStableUpdates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var first = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.Source(2))
        ], cancellationToken);
        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, first.Status);
        Assert.NotEmpty(first.MetadataDelta);
        Assert.NotEmpty(first.IlDelta);
        Assert.NotEmpty(first.PdbDelta);
        Assert.NotEmpty(first.UpdatedMethods);
        var changedDocument = Assert.Single(first.ChangedDocuments);
        Assert.Equal(fixture.SourcePath, changedDocument.FilePath);
        Assert.NotEqual(changedDocument.BaselineSha256, changedDocument.UpdatedSha256);
        Assert.True(session.HasPendingUpdate);

        session.DiscardUpdate();
        var regenerated = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.Source(2))
        ], cancellationToken);
        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, regenerated.Status);
        Assert.Equal(first.ModuleId, regenerated.ModuleId);
        Assert.Equal(first.UpdatedMethods, regenerated.UpdatedMethods);
        Assert.Equal(first.ChangedDocuments, regenerated.ChangedDocuments);
        session.CommitUpdate();

        var second = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.Source(3))
        ], cancellationToken);
        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, second.Status);
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_AllowsTrailingCommentWithoutSequencePointMovement()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.Source(2) + "\n// trailing comment\n")
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, update.Status);
        Assert.Empty(update.LineUpdates);
        Assert.True(session.HasPendingUpdate);
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_RejectsDuplicateDocumentChangesWithoutStagingAnUpdate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.SourceWithLineShift(2)),
            new(Path.Combine(fixture.Root, ".", Path.GetFileName(fixture.SourcePath)), ProjectFixture.Source(2))
        ], cancellationToken));

        Assert.False(session.HasPendingUpdate);
        var recovered = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.Source(2))
        ], cancellationToken);
        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, recovered.Status);
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_EmitsExactSequencePointUpdatesForLineMovingEdit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var first = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.Source(2))
        ], cancellationToken);
        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, first.Status);
        session.CommitUpdate();

        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.SourceWithLineShift(3))
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, update.Status);
        Assert.NotEmpty(update.PdbDelta);
        Assert.True(update.LineUpdatesComplete);
        var lineUpdate = Assert.Single(update.LineUpdates);
        Assert.Equal(fixture.SourcePath, lineUpdate.FilePath);
        Assert.Equal(lineUpdate.OldLine + 1, lineUpdate.NewLine);
        Assert.Contains(update.Warnings, warning => warning.Contains("Exact sequence-point", StringComparison.Ordinal));
        Assert.True(session.HasPendingUpdate);
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_MapsOnlyDocumentsThatActuallyMoveLines()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(
            ProjectFixture.Source(1),
            cancellationToken,
            additionalSource: ProjectFixture.SecondaryActiveSource(2));
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.SourceWithLineShift(2)),
            new(fixture.SecondaryPath, ProjectFixture.SecondaryActiveSourceWithoutSeed(3))
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, update.Status);
        Assert.NotEmpty(update.LineUpdates);
        Assert.All(update.LineUpdates, lineUpdate => Assert.Equal(fixture.SourcePath, lineUpdate.FilePath));
        Assert.Contains(fixture.SecondaryPath, update.ChangedFiles);
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_DetectsLineMovementWhenTotalLineCountIsUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var baseline = ProjectFixture.BalancedLineShiftBaseline(2);
        var updated = ProjectFixture.BalancedLineShiftUpdated(3);
        using var fixture = await ProjectFixture.CreateAsync(baseline, cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, updated)
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, update.Status);
        Assert.Equal(baseline.Split('\n').Length, updated.Split('\n').Length);
        Assert.Contains(update.LineUpdates, lineUpdate =>
            lineUpdate.OldLine == FindLine(baseline, "public static int Unchanged()") &&
            lineUpdate.NewLine == FindLine(updated, "public static int Unchanged()"));
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_EmitsExactSequencePointUpdatesForUpdatedMethodLineMove()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(ProjectFixture.ActiveSource(2), cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.ActiveSourceWithCommentShift(3))
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, update.Status);
        Assert.True(update.LineUpdatesComplete);
        Assert.Contains(update.Warnings, warning => warning.Contains("including updated methods", StringComparison.Ordinal));
        Assert.Contains(update.LineUpdates, lineUpdate =>
            lineUpdate.OldLine == FindLine(ProjectFixture.ActiveSource(2), "var result = input * 2;") &&
            lineUpdate.NewLine == FindLine(ProjectFixture.ActiveSourceWithCommentShift(3), "var result = input * 3;"));
        Assert.True(session.HasPendingUpdate);
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_MapsExistingPointsWhenUpdatedMethodAddsExecutablePoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(ProjectFixture.ActiveSource(2), cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var updatedSource = ProjectFixture.ActiveSourceWithExecutableInsertion(2);
        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, updatedSource)
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, update.Status);
        Assert.Contains(update.LineUpdates, lineUpdate =>
            lineUpdate.OldLine == FindLine(ProjectFixture.ActiveSource(2), "var result = input * 2;") &&
            lineUpdate.NewLine == FindLine(updatedSource, "var result = input * 2;"));
        session.DiscardUpdate();
    }

    [Theory]
    [InlineData("multiline")]
    [InlineData("deleted-point")]
    public async Task Session_RejectsIncompleteUpdatedMethodMappingBeforeCommit(string editShape)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(ProjectFixture.ActiveSource(2), cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);
        var updatedSource = editShape == "multiline"
            ? ProjectFixture.ActiveSourceWithMultilineExpression(3)
            : ProjectFixture.ActiveSourceWithoutSeed(3);

        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, updatedSource)
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.False(update.LineUpdatesComplete);
        Assert.Empty(update.MetadataDelta);
        Assert.Empty(update.LineUpdates);
        Assert.False(session.HasPendingUpdate);
        Assert.Contains(update.Warnings, warning => warning.Contains("updated method", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Session_RejectsAmbiguousOrdinalUpdatedMethodMapping()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(
            ProjectFixture.AmbiguousMappingSource(1, 2),
            cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var updatedSource = ProjectFixture.AmbiguousMappingSourceWithReorderedChanges(3, 4);
        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, updatedSource)
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.Equal(
            ProjectFixture.AmbiguousMappingSource(1, 2).Split('\n').Length,
            updatedSource.Split('\n').Length);
        Assert.False(update.LineUpdatesComplete);
        Assert.Empty(update.MetadataDelta);
        Assert.False(session.HasPendingUpdate);
        Assert.Contains(update.Warnings, warning => warning.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_AllowsHarmlessLineTextInCommentsAndStrings(bool useStringLiteral)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(
            ProjectFixture.SourceWithHarmlessLineText(1, useStringLiteral),
            cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.SourceWithHarmlessLineText(2, useStringLiteral))
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, update.Status);
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_RejectsActualLineDirective()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, "#line 100\n" + ProjectFixture.Source(2))
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.False(session.HasPendingUpdate);
    }

    [Fact]
    public async Task Session_RejectsMethodTableChangesBeforeCommittingFullPdb()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline", "AddMethodToExistingType"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.SourceWithAddedMethod(2))
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.False(update.LineUpdatesComplete);
        Assert.Empty(update.MetadataDelta);
        Assert.False(session.HasPendingUpdate);
        Assert.Contains(update.Warnings, warning => warning.Contains("method-definition table", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Session_RejectsNestedTypeMethodTokenSwaps()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(
            ProjectFixture.NestedTypeSource(1, 2, reverseOuterTypes: false),
            cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.NestedTypeSource(3, 4, reverseOuterTypes: true))
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.False(session.HasPendingUpdate);
        Assert.Contains(update.Warnings, warning => warning.Contains("Method token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Session_RejectsCoordinatedTypeAndMethodTokenSwaps()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(
            ProjectFixture.ReorderedSignatureTypesSource(1, 2, reverse: false),
            cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.ReorderedSignatureTypesSource(3, 4, reverse: true))
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.False(session.HasPendingUpdate);
        Assert.Contains(update.Warnings, warning => warning.Contains("Method token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Session_DetectsReorderedChangedFieldInitializersAsLineMovement()
    {
        var baseline = ProjectFixture.ReorderedFieldInitializersSource(1, 2, reverse: false);
        var updated = ProjectFixture.ReorderedFieldInitializersSource(3, 4, reverse: true);

        Assert.Equal(baseline.Split('\n').Length, updated.Split('\n').Length);
        Assert.True(DetectLineMovement(baseline, updated));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Session_DetectsReorderedChangedPropertiesAsLineMovement(bool expressionBodied)
    {
        var baseline = ProjectFixture.ReorderedPropertiesSource(1, 2, reverse: false, expressionBodied);
        var updated = ProjectFixture.ReorderedPropertiesSource(3, 4, reverse: true, expressionBodied);

        Assert.Equal(baseline.Split('\n').Length, updated.Split('\n').Length);
        Assert.True(DetectLineMovement(baseline, updated));
    }

    [Fact]
    public async Task Session_DetectsReorderedChangedExpressionStatements()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var baseline = ProjectFixture.ReorderedExpressionStatementsSource(1, 2, reverseCalls: false);
        var updated = ProjectFixture.ReorderedExpressionStatementsSource(3, 4, reverseCalls: true);
        using var fixture = await ProjectFixture.CreateAsync(baseline, cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, updated)
        ], cancellationToken);

        Assert.Equal(baseline.Split('\n').Length, updated.Split('\n').Length);
        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.False(update.LineUpdatesComplete);
        Assert.False(session.HasPendingUpdate);
    }

    [Fact]
    public async Task Session_UsesPortablePdbPathEmbeddedInBaselineAssembly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pdbPath = Path.Combine(
            Path.GetTempPath(),
            "hotreload-delta-generator-pdbs",
            Guid.NewGuid().ToString("N"),
            "configured.pdb");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pdbPath)!);
            using var fixture = await ProjectFixture.CreateAsync(
                ProjectFixture.Source(1),
                cancellationToken,
                pdbFile: pdbPath);
            using var session = await HotReloadDeltaSession.StartAsync(
                fixture.ProjectPath,
                "Debug",
                "net10.0",
                properties: null,
                runtimeCapabilities: ["Baseline"],
                cancellationToken);

            Assert.Equal(Path.GetFullPath(pdbPath), session.Info.PdbPath);
        }
        finally
        {
            var pdbDirectory = Path.GetDirectoryName(pdbPath)!;
            if (Directory.Exists(pdbDirectory))
            {
                Directory.Delete(pdbDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Session_ReleasesWorkspaceWhenBaselinePdbIsMissing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(
            ProjectFixture.Source(1),
            cancellationToken,
            debugType: "none");

        await Assert.ThrowsAsync<InvalidOperationException>(() => HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken));

        Directory.Delete(fixture.Root, recursive: true);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task Session_CapturesModuleIdWithTheBaselineImage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);
        Guid expectedModuleId;
        using (var stream = File.OpenRead(session.Info.OutputAssemblyPath))
        using (var peReader = new PEReader(stream))
        {
            var metadata = peReader.GetMetadataReader();
            expectedModuleId = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        }

        File.Delete(session.Info.OutputAssemblyPath);

        Assert.NotEqual(Guid.Empty, session.Info.ModuleId);
        Assert.Equal(expectedModuleId, session.Info.ModuleId);
    }

    [Fact]
    public async Task Session_CorrelatesPathMappedPortablePdbDocumentsByChecksum()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var baseline = ProjectFixture.BalancedLineShiftBaseline(2);
        using var fixture = await ProjectFixture.CreateAsync(
            baseline,
            cancellationToken,
            buildPathMapTarget: "/_/source");
        var pdbPath = Path.Combine(fixture.Root, "obj", "Debug", "net10.0", "DeltaFixture.pdb");
        using var pdbStream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        var reader = provider.GetMetadataReader();
        var document = reader.Documents
            .Select(reader.GetDocument)
            .Single(candidate => reader.GetString(candidate.Name).EndsWith("Calculator.cs", StringComparison.Ordinal));
        var mappedPath = reader.GetString(document.Name);
        var checksum = reader.GetBlobBytes(document.Hash).ToImmutableArray();
        Assert.NotEqual(Path.GetFullPath(fixture.SourcePath), Path.GetFullPath(mappedPath));
        Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(fixture.SourcePath, cancellationToken)), checksum);

        var resolver = typeof(HotReloadDeltaSession).GetMethod(
            "ResolveChangedDocumentPath",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var resolvedPath = (string?)resolver.Invoke(null, [
            mappedPath,
            checksum,
            new HashSet<string>([fixture.SourcePath], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ImmutableArray<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                [fixture.SourcePath] = checksum
            }
        ]);

        Assert.Equal(fixture.SourcePath, resolvedPath);
    }

    [Fact]
    public async Task Worker_DiscardsPreparedUpdateWhenArtifactWritingFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(cancellationToken);
        using var worker = StartWorker();
        var stderr = worker.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            var started = await SendWorkerRequestAsync(
                worker,
                "start",
                "startSession",
                new
                {
                    projectPath = fixture.ProjectPath,
                    workspaceRoot = fixture.Root,
                    configuration = "Debug",
                    targetFramework = "net10.0",
                    msBuildProperties = (object?)null,
                    runtimeCapabilities = new[] { "Baseline" }
                },
                cancellationToken);
            Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());
            var sessionId = started.GetProperty("result").GetProperty("sessionId").GetString()!;
            var blockedArtifactDirectory = Path.Combine(fixture.Root, "artifact-blocker");
            await File.WriteAllTextAsync(blockedArtifactDirectory, "not a directory", cancellationToken);

            var failed = await SendWorkerRequestAsync(
                worker,
                "prepare-failure",
                "prepareUpdate",
                new
                {
                    sessionId,
                    changedDocuments = new[]
                    {
                        new { filePath = fixture.SourcePath, text = ProjectFixture.Source(2) }
                    },
                    artifactDirectory = blockedArtifactDirectory
                },
                cancellationToken);
            Assert.False(failed.GetProperty("success").GetBoolean());
            Assert.Equal(
                "hot_reload_worker_error",
                failed.GetProperty("error").GetProperty("code").GetString());

            var recovered = await SendWorkerRequestAsync(
                worker,
                "prepare-recovered",
                "prepareUpdate",
                new
                {
                    sessionId,
                    changedDocuments = new[]
                    {
                        new { filePath = fixture.SourcePath, text = ProjectFixture.Source(2) }
                    },
                    artifactDirectory = Path.Combine(fixture.Root, "artifacts")
                },
                cancellationToken);
            Assert.True(recovered.GetProperty("success").GetBoolean(), recovered.ToString());
            var result = recovered.GetProperty("result");
            Assert.Equal("ready", result.GetProperty("status").GetString());
            var updateId = result.GetProperty("updateId").GetString()!;

            var discarded = await SendWorkerRequestAsync(
                worker,
                "discard",
                "discardUpdate",
                new { sessionId, updateId },
                cancellationToken);
            Assert.True(discarded.GetProperty("success").GetBoolean(), discarded.ToString());

            var ended = await SendWorkerRequestAsync(
                worker,
                "end",
                "endSession",
                new { sessionId },
                cancellationToken);
            Assert.True(ended.GetProperty("success").GetBoolean(), ended.ToString());

            worker.StandardInput.Close();
            await worker.WaitForExitAsync(cancellationToken);
            Assert.True(worker.ExitCode == 0, await stderr);
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Worker_RejectsDuplicateDocumentsAndKeepsSessionUsable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(cancellationToken);
        using var worker = StartWorker();
        var stderr = worker.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            var started = await SendWorkerRequestAsync(
                worker,
                "start",
                "startSession",
                new
                {
                    projectPath = fixture.ProjectPath,
                    workspaceRoot = fixture.Root,
                    configuration = "Debug",
                    targetFramework = "net10.0",
                    msBuildProperties = (object?)null,
                    runtimeCapabilities = new[] { "Baseline" }
                },
                cancellationToken);
            Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());
            var sessionId = started.GetProperty("result").GetProperty("sessionId").GetString()!;

            var rejected = await SendWorkerRequestAsync(
                worker,
                "duplicate",
                "prepareUpdate",
                new
                {
                    sessionId,
                    changedDocuments = new[]
                    {
                        new { filePath = fixture.SourcePath, text = ProjectFixture.SourceWithLineShift(2) },
                        new
                        {
                            filePath = Path.Combine(fixture.Root, ".", Path.GetFileName(fixture.SourcePath)),
                            text = ProjectFixture.Source(2)
                        }
                    },
                    artifactDirectory = Path.Combine(fixture.Root, "artifacts")
                },
                cancellationToken);
            Assert.False(rejected.GetProperty("success").GetBoolean());
            Assert.Equal(
                "hot_reload_invalid_request",
                rejected.GetProperty("error").GetProperty("code").GetString());

            var recovered = await SendWorkerRequestAsync(
                worker,
                "prepare",
                "prepareUpdate",
                new
                {
                    sessionId,
                    changedDocuments = new[]
                    {
                        new { filePath = fixture.SourcePath, text = ProjectFixture.Source(2) }
                    },
                    artifactDirectory = Path.Combine(fixture.Root, "artifacts")
                },
                cancellationToken);
            Assert.True(recovered.GetProperty("success").GetBoolean(), recovered.ToString());
            var updateId = recovered.GetProperty("result").GetProperty("updateId").GetString()!;

            var discarded = await SendWorkerRequestAsync(
                worker,
                "discard",
                "discardUpdate",
                new { sessionId, updateId },
                cancellationToken);
            Assert.True(discarded.GetProperty("success").GetBoolean(), discarded.ToString());

            var ended = await SendWorkerRequestAsync(
                worker,
                "end",
                "endSession",
                new { sessionId },
                cancellationToken);
            Assert.True(ended.GetProperty("success").GetBoolean(), ended.ToString());

            worker.StandardInput.Close();
            await worker.WaitForExitAsync(cancellationToken);
            Assert.True(worker.ExitCode == 0, await stderr);
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Worker_RejectsArtifactDirectoryThatEscapesWorkspaceThroughSymbolicLink()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(cancellationToken);
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "hotreload-delta-generator-tests-outside",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        var linkPath = Path.Combine(fixture.Root, "artifact-link");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, outsideRoot);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Skip($"Symbolic links are unavailable in this test environment: {exception.Message}");
                return;
            }

            using var worker = StartWorker();
            var stderr = worker.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                var started = await SendWorkerRequestAsync(
                    worker,
                    "start",
                    "startSession",
                    new
                    {
                        projectPath = fixture.ProjectPath,
                        workspaceRoot = fixture.Root,
                        configuration = "Debug",
                        targetFramework = "net10.0",
                        msBuildProperties = (object?)null,
                        runtimeCapabilities = new[] { "Baseline" }
                    },
                    cancellationToken);
                Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());
                var sessionId = started.GetProperty("result").GetProperty("sessionId").GetString()!;

                var rejected = await SendWorkerRequestAsync(
                    worker,
                    "prepare",
                    "prepareUpdate",
                    new
                    {
                        sessionId,
                        changedDocuments = new[]
                        {
                            new { filePath = fixture.SourcePath, text = ProjectFixture.Source(2) }
                        },
                        artifactDirectory = Path.Combine(linkPath, "artifacts")
                    },
                    cancellationToken);
                Assert.False(rejected.GetProperty("success").GetBoolean());
                Assert.Equal(
                    "hot_reload_path_outside_workspace",
                    rejected.GetProperty("error").GetProperty("code").GetString());
                Assert.Empty(Directory.EnumerateFileSystemEntries(outsideRoot));

                var ended = await SendWorkerRequestAsync(
                    worker,
                    "end",
                    "endSession",
                    new { sessionId },
                    cancellationToken);
                Assert.True(ended.GetProperty("success").GetBoolean(), ended.ToString());

                worker.StandardInput.Close();
                await worker.WaitForExitAsync(cancellationToken);
                Assert.True(worker.ExitCode == 0, await stderr);
            }
            finally
            {
                if (!worker.HasExited)
                {
                    worker.Kill(entireProcessTree: true);
                    await worker.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task Worker_RejectsProjectThatEscapesWorkspaceThroughSymbolicLink()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspaceFixture = await ProjectFixture.CreateAsync(cancellationToken);
        using var outsideFixture = await ProjectFixture.CreateAsync(cancellationToken);
        var linkPath = Path.Combine(workspaceFixture.Root, "linked-project.csproj");
        try
        {
            File.CreateSymbolicLink(linkPath, outsideFixture.ProjectPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic links are unavailable in this test environment: {exception.Message}");
            return;
        }

        using var worker = StartWorker();
        var stderr = worker.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            var rejected = await SendWorkerRequestAsync(
                worker,
                "start",
                "startSession",
                new
                {
                    projectPath = linkPath,
                    workspaceRoot = workspaceFixture.Root,
                    configuration = "Debug",
                    targetFramework = "net10.0",
                    msBuildProperties = (object?)null,
                    runtimeCapabilities = new[] { "Baseline" }
                },
                cancellationToken);
            Assert.False(rejected.GetProperty("success").GetBoolean());
            Assert.Equal(
                "hot_reload_baseline_invalid",
                rejected.GetProperty("error").GetProperty("code").GetString());

            worker.StandardInput.Close();
            await worker.WaitForExitAsync(cancellationToken);
            Assert.True(worker.ExitCode == 0, await stderr);
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Worker_AcceptsFilesystemRootAsWorkspaceRoot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(cancellationToken);
        using var worker = StartWorker();
        var stderr = worker.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            var started = await SendWorkerRequestAsync(
                worker,
                "start",
                "startSession",
                new
                {
                    projectPath = fixture.ProjectPath,
                    workspaceRoot = Path.GetPathRoot(fixture.Root)!,
                    configuration = "Debug",
                    targetFramework = "net10.0",
                    msBuildProperties = (object?)null,
                    runtimeCapabilities = new[] { "Baseline" }
                },
                cancellationToken);
            Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());
            var sessionId = started.GetProperty("result").GetProperty("sessionId").GetString()!;

            var prepared = await SendWorkerRequestAsync(
                worker,
                "prepare",
                "prepareUpdate",
                new
                {
                    sessionId,
                    changedDocuments = new[]
                    {
                        new { filePath = fixture.SourcePath, text = ProjectFixture.Source(2) }
                    },
                    artifactDirectory = Path.Combine(fixture.Root, "artifacts")
                },
                cancellationToken);
            Assert.True(prepared.GetProperty("success").GetBoolean(), prepared.ToString());
            var updateId = prepared.GetProperty("result").GetProperty("updateId").GetString()!;

            var discarded = await SendWorkerRequestAsync(
                worker,
                "discard",
                "discardUpdate",
                new { sessionId, updateId },
                cancellationToken);
            Assert.True(discarded.GetProperty("success").GetBoolean(), discarded.ToString());

            var ended = await SendWorkerRequestAsync(
                worker,
                "end",
                "endSession",
                new { sessionId },
                cancellationToken);
            Assert.True(ended.GetProperty("success").GetBoolean(), ended.ToString());

            worker.StandardInput.Close();
            await worker.WaitForExitAsync(cancellationToken);
            Assert.True(worker.ExitCode == 0, await stderr);
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static Process StartWorker()
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = typeof(HotReloadDeltaSessionTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?
            .Configuration ?? "Debug";
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repositoryRoot
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(
            repositoryRoot,
            "src",
            "HotReload.DeltaWorker",
            "HotReload.DeltaWorker.csproj"));
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("--framework");
        startInfo.ArgumentList.Add("net10.0");
        return Process.Start(startInfo)!;
    }

    private static async Task<JsonElement> SendWorkerRequestAsync(
        Process worker,
        string id,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Serialize(new
        {
            protocolVersion = 2,
            id,
            method,
            parameters
        });
        await worker.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
        await worker.StandardInput.FlushAsync(cancellationToken);
        var response = await worker.StandardOutput.ReadLineAsync(cancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(response));
        using var document = JsonDocument.Parse(response);
        return document.RootElement.Clone();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HotReloadUtils.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the hotreload-utils repository root.");
    }

    private static int FindLine(string source, string text) =>
        Array.FindIndex(source.Split('\n'), line => line.Contains(text, StringComparison.Ordinal));

    private static bool DetectLineMovement(string baseline, string updated)
    {
        var detector = typeof(HotReloadDeltaSession).GetMethod(
            "HasLineMovingChanges",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)detector.Invoke(null, [
            SourceText.From(baseline),
            SourceText.From(updated)
        ])!;
    }

    private sealed class RazorProjectFixture : IDisposable
    {
        private RazorProjectFixture(string root)
        {
            Root = root;
            ProjectPath = Path.Combine(root, "RazorDeltaFixture.csproj");
            ComponentPath = Path.Combine(root, "Counter.razor");
        }

        public string Root { get; }

        public string ProjectPath { get; }

        public string ComponentPath { get; }

        public const string BaselineSource = """
            @namespace RazorDeltaFixture
            <h1>Counter step: 2</h1>
            <p role="status">Value: @value</p>
            <button @onclick="Increment">Increment</button>
            @code {
                private int value;
                private void Increment() => value += 2;
            }
            """;

        public const string UpdatedSource = """
            @namespace RazorDeltaFixture
            <h1>Counter step: 3</h1>
            <p role="status">Updated value: @value</p>
            <button @onclick="Increment">Add three</button>
            @code {
                private int value;
                private void Increment() => value += 3;
            }
            """;

        public static async Task<RazorProjectFixture> CreateAsync(
            CancellationToken cancellationToken,
            string? initialSource = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "hotreload-razor-delta-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new RazorProjectFixture(root);
            await File.WriteAllTextAsync(fixture.ProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk.Razor">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <DebugType>portable</DebugType>
                    <Optimize>false</Optimize>
                    <RazorLangVersion>10.0</RazorLangVersion>
                  </PropertyGroup>
                  <ItemGroup>
                    <FrameworkReference Include="Microsoft.AspNetCore.App" />
                  </ItemGroup>
                </Project>
                """, cancellationToken);
            await File.WriteAllTextAsync(fixture.ComponentPath, initialSource ?? BaselineSource, cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root
            };
            startInfo.ArgumentList.Add("build");
            startInfo.ArgumentList.Add(fixture.ProjectPath);
            startInfo.ArgumentList.Add("--nologo");
            using var process = Process.Start(startInfo)!;
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                fixture.Dispose();
                throw new InvalidOperationException($"Razor fixture build failed: {output}{error}");
            }

            return fixture;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    private sealed class ProjectFixture : IDisposable
    {
        private ProjectFixture(string root)
        {
            Root = root;
            ProjectPath = Path.Combine(root, "DeltaFixture.csproj");
            SourcePath = Path.Combine(root, "Calculator.cs");
            SecondaryPath = Path.Combine(root, "Secondary.cs");
        }

        public string Root { get; }

        public string ProjectPath { get; }

        public string SourcePath { get; }

        public string SecondaryPath { get; }

        public static Task<ProjectFixture> CreateAsync(CancellationToken cancellationToken) =>
            CreateAsync(Source(1), cancellationToken);

        public static async Task<ProjectFixture> CreateAsync(
            string initialSource,
            CancellationToken cancellationToken,
            string? pdbFile = null,
            string? buildPathMapTarget = null,
            string debugType = "portable",
            string? additionalSource = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "hotreload-delta-generator-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new ProjectFixture(root);
            var pdbProperty = pdbFile is null
                ? string.Empty
                : $"<PdbFile>{System.Security.SecurityElement.Escape(Path.GetFullPath(pdbFile))}</PdbFile>";
            await File.WriteAllTextAsync(fixture.ProjectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <DebugType>{{debugType}}</DebugType>
                    <Optimize>false</Optimize>
                    {{pdbProperty}}
                  </PropertyGroup>
                </Project>
                """, cancellationToken);
            await File.WriteAllTextAsync(fixture.SourcePath, initialSource, cancellationToken);
            if (additionalSource is not null)
            {
                await File.WriteAllTextAsync(fixture.SecondaryPath, additionalSource, cancellationToken);
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root
            };
            startInfo.ArgumentList.Add("build");
            startInfo.ArgumentList.Add(fixture.ProjectPath);
            startInfo.ArgumentList.Add("--nologo");
            if (buildPathMapTarget is not null)
            {
                startInfo.ArgumentList.Add($"-p:PathMap={fixture.Root}={buildPathMapTarget}");
            }
            using var process = Process.Start(startInfo)!;
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                fixture.Dispose();
                throw new InvalidOperationException($"Fixture build failed: {output}{error}");
            }

            return fixture;
        }

        public static string Source(int value) => $$"""
            namespace DeltaFixture;

            public static class Calculator
            {
                public static int Value() => {{value}};

                public static int Unchanged() => 10;
            }
            """;

        public static string SourceWithLineShift(int value) =>
            Source(value).Replace(
                "    public static int Unchanged()",
                "    // inserted line\n    public static int Unchanged()",
                StringComparison.Ordinal);

        public static string SourceWithHarmlessLineText(int value, bool useStringLiteral) =>
            Source(value).Replace(
                "    public static int Unchanged() => 10;",
                useStringLiteral
                    ? "    public static string Message => \"See #line documentation\";\n    public static int Unchanged() => 10;"
                    : "    // See #line documentation\n    public static int Unchanged() => 10;",
                StringComparison.Ordinal);

        public static string SourceWithAddedMethod(int value) =>
            Source(value).Replace(
                "    public static int Value()",
                "    public static int Added() => 20;\n\n    public static int Value()",
                StringComparison.Ordinal);

        public static string BalancedLineShiftBaseline(int value) =>
            Source(value).Replace(
                "    public static int Unchanged() => 10;",
                "    public static int Unchanged() => 10;\n    // removable line",
                StringComparison.Ordinal);

        public static string BalancedLineShiftUpdated(int value) =>
            Source(value).Replace(
                "    public static int Unchanged()",
                "    // inserted line\n    public static int Unchanged()",
                StringComparison.Ordinal);

        public static string ActiveSource(int value) => $$"""
            namespace DeltaFixture;

            public static class Calculator
            {
                public static int Run(int input)
                {
                    var seed = input + 1;
                    var result = input * {{value}};
                    return result;
                }
            }
            """;

        public static string ActiveSourceWithCommentShift(int value) =>
            ActiveSource(value).Replace(
                "        var result",
                "        // inserted line\n        var result",
                StringComparison.Ordinal);

        public static string ActiveSourceWithExecutableInsertion(int value) =>
            ActiveSource(value).Replace(
                "        var result",
                "        var extra = seed - seed;\n        var result",
                StringComparison.Ordinal);

        public static string ActiveSourceWithMultilineExpression(int value) =>
            ActiveSource(value).Replace(
                $"var result = input * {value};",
                $"var result =\n            input * {value};",
                StringComparison.Ordinal);

        public static string ActiveSourceWithoutSeed(int value)
        {
            var source = ActiveSource(value);
            const string line = "        var seed = input + 1;";
            return source
                .Replace(line + "\r\n", string.Empty, StringComparison.Ordinal)
                .Replace(line + "\n", string.Empty, StringComparison.Ordinal);
        }

        public static string AmbiguousMappingSource(int first, int second) => $$"""
            namespace DeltaFixture;

            public static class Calculator
            {
                public static int Run(int input)
                {
                    var first = input + {{first}};
                    var second = input + {{second}};
                    return first + second;
                }
            }
            """;

        public static string AmbiguousMappingSourceWithReorderedChanges(int first, int second) => $$"""
            namespace DeltaFixture;

            public static class Calculator
            {
                public static int Run(int input)
                {
                    var second = input + {{second}};
                    var first = input + {{first}};
                    return first + second;
                }
            }
            """;

        public static string NestedTypeSource(int first, int second, bool reverseOuterTypes)
        {
            var a = $$"""
                public static class A
                {
                    public static class Inner
                    {
                        public static int M() => {{first}};
                    }
                }
                """;
            var b = $$"""
                public static class B
                {
                    public static class Inner
                    {
                        public static int M() => {{second}};
                    }
                }
                """;
            return "namespace DeltaFixture;\n\n" + (reverseOuterTypes ? b + "\n\n" + a : a + "\n\n" + b);
        }

        public static string ReorderedSignatureTypesSource(int first, int second, bool reverse)
        {
            var types = reverse
                ? "public sealed class B { }\npublic sealed class A { }"
                : "public sealed class A { }\npublic sealed class B { }";
            var methods = reverse
                ? $"    public static int M(B value) => {second};\n    public static int M(A value) => {first};"
                : $"    public static int M(A value) => {first};\n    public static int M(B value) => {second};";
            return $$"""
                namespace DeltaFixture;

                {{types}}

                public static class Calculator
                {
                {{methods}}
                }
                """;
        }

        public static string ReorderedFieldInitializersSource(int first, int second, bool reverse)
        {
            var fields = reverse
                ? $"    public static int B = G({second});\n    public static int A = F({first});"
                : $"    public static int A = F({first});\n    public static int B = G({second});";
            return $$"""
                namespace DeltaFixture;

                public static class Calculator
                {
                {{fields}}

                    private static int F(int value) => value;
                    private static int G(int value) => value;
                }
                """;
        }

        public static string ReorderedPropertiesSource(
            int first,
            int second,
            bool reverse,
            bool expressionBodied)
        {
            var firstProperty = expressionBodied
                ? $"    public static int A => F({first});"
                : $"    public static int A {{ get; }} = F({first});";
            var secondProperty = expressionBodied
                ? $"    public static int B => G({second});"
                : $"    public static int B {{ get; }} = G({second});";
            var properties = reverse
                ? secondProperty + "\n" + firstProperty
                : firstProperty + "\n" + secondProperty;
            return $$"""
                namespace DeltaFixture;

                public static class Calculator
                {
                {{properties}}

                    private static int F(int value) => value;
                    private static int G(int value) => value;
                }
                """;
        }

        public static string SecondaryActiveSource(int value) => $$"""
            namespace DeltaFixture;

            public static class Secondary
            {
                public static int Run(int input)
                {
                    var seed = input + 1;
                    return input * {{value}};
                }
            }
            """;

        public static string SecondaryActiveSourceWithoutSeed(int value) =>
            SecondaryActiveSource(value).Replace(
                "        var seed = input + 1;",
                "        // seed removed",
                StringComparison.Ordinal);

        public static string ReorderedExpressionStatementsSource(int first, int second, bool reverseCalls)
        {
            var calls = reverseCalls
                ? $"        LogB({second});\n        LogA({first});"
                : $"        LogA({first});\n        LogB({second});";
            return $$"""
                namespace DeltaFixture;

                public static class Calculator
                {
                    public static void Run()
                    {
                {{calls}}
                    }

                    private static void LogA(int value) { }
                    private static void LogB(int value) { }
                }
                """;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for files retained by MSBuild.
            }
        }
    }
}
