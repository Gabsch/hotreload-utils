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

        Assert.True(
            update.Status == HotReloadDeltaUpdateStatus.Ready,
            string.Join(Environment.NewLine, update.Warnings));
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
    public async Task Session_RejectsEqualLengthRazorMoveWhenChangedTextHasNoStableIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await RazorProjectFixture.CreateAsync(
            cancellationToken,
            RazorProjectFixture.MovedExpressionBaseline);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.ComponentPath, RazorProjectFixture.MovedExpressionUpdated)
        ], cancellationToken);

        Assert.Equal(
            RazorProjectFixture.MovedExpressionBaseline.Split('\n').Length,
            RazorProjectFixture.MovedExpressionUpdated.Split('\n').Length);
        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.False(update.LineUpdatesComplete);
        Assert.Empty(update.MetadataDelta);
        Assert.False(session.HasPendingUpdate);
        Assert.Contains(update.Warnings, warning =>
            warning.Contains("sequence-point positions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Session_RejectsRazorExecutableRegionPermutationWithSameLineMultiset()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await RazorProjectFixture.CreateAsync(
            cancellationToken,
            RazorProjectFixture.SwappedRegionsBaseline);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.ComponentPath, RazorProjectFixture.SwappedRegionsUpdated)
        ], cancellationToken);

        Assert.Equal(
            RazorProjectFixture.SwappedRegionsBaseline.Split('\n').Length,
            RazorProjectFixture.SwappedRegionsUpdated.Split('\n').Length);
        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.False(update.LineUpdatesComplete);
        Assert.Empty(update.MetadataDelta);
        Assert.False(session.HasPendingUpdate);
        Assert.Contains(update.Warnings, warning =>
            warning.Contains("identities", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Session_RejectsEditedRazorExecutableRegionPermutationWithSameLineMultiset()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await RazorProjectFixture.CreateAsync(
            cancellationToken,
            RazorProjectFixture.SwappedRegionsBaseline);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.ComponentPath, RazorProjectFixture.SwappedAndEditedRegionsUpdated)
        ], cancellationToken);

        Assert.Equal(
            RazorProjectFixture.SwappedRegionsBaseline.Split('\n').Length,
            RazorProjectFixture.SwappedAndEditedRegionsUpdated.Split('\n').Length);
        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.False(update.LineUpdatesComplete);
        Assert.Empty(update.MetadataDelta);
        Assert.False(session.HasPendingUpdate);
        Assert.Contains(update.Warnings, warning =>
            warning.Contains("identities", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Session_RejectsRazorPermutationWithDuplicateStructuralAnchors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await RazorProjectFixture.CreateAsync(
            cancellationToken,
            RazorProjectFixture.DuplicateStructuralAnchorsBaseline);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.ComponentPath, RazorProjectFixture.DuplicateStructuralAnchorsSwapped)
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.False(update.LineUpdatesComplete);
        Assert.Empty(update.MetadataDelta);
        Assert.False(session.HasPendingUpdate);
        Assert.Contains(update.Warnings, warning =>
            warning.Contains("non-unique", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Session_RejectsRazorPermutationThatDiffersOnlyByLiteralWhitespace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await RazorProjectFixture.CreateAsync(
            cancellationToken,
            RazorProjectFixture.LiteralWhitespaceBaseline);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.ComponentPath, RazorProjectFixture.LiteralWhitespaceSwapped)
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.False(update.LineUpdatesComplete);
        Assert.Empty(update.MetadataDelta);
        Assert.False(session.HasPendingUpdate);
        Assert.Contains(update.Warnings, warning =>
            warning.Contains("identities", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Session_PreservesSameLineRazorSequencePointMultiplicity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await RazorProjectFixture.CreateAsync(
            cancellationToken,
            RazorProjectFixture.SameLinePointsBaseline);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.ComponentPath, RazorProjectFixture.SameLinePointsUpdated)
        ], cancellationToken);

        Assert.True(
            update.Status == HotReloadDeltaUpdateStatus.Ready,
            string.Join(Environment.NewLine, update.Warnings));
        Assert.True(update.LineUpdatesComplete);
        Assert.True(session.HasPendingUpdate);
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_AccountsForChangedSourceGeneratedDocumentsBeforeDeltaClassification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await SourceGeneratorProjectFixture.CreateAsync(cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var baselineSolution = ReadSessionSolution(session, "pdbSolution");
        var baselineProject = baselineSolution.Projects.Single(project =>
            string.Equals(project.FilePath, fixture.ProjectPath, StringComparison.OrdinalIgnoreCase));
        var inputDocument = baselineProject.Documents.Single(document =>
            string.Equals(document.FilePath, fixture.SourcePath, StringComparison.OrdinalIgnoreCase));
        var updatedSolution = baselineSolution.WithDocumentText(
            inputDocument.Id,
            SourceText.From(SourceGeneratorProjectFixture.InputSource(generatedLines: 1, value: 1)));
        var analyze = typeof(HotReloadDeltaSession).GetMethod(
            "AnalyzeRuntimeDocumentChangesAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var analysisTask = (Task)analyze.Invoke(null, [
            baselineProject,
            updatedSolution.GetProject(baselineProject.Id)!,
            cancellationToken
        ])!;
        await analysisTask;
        var analysis = analysisTask.GetType().GetProperty("Result")!.GetValue(analysisTask)!;
        Assert.True((bool)analysis.GetType().GetProperty("Success")!.GetValue(analysis)!);
        var generatedFiles = (ImmutableArray<string>)analysis.GetType()
            .GetProperty("GeneratedFiles")!
            .GetValue(analysis)!;
        Assert.Contains(generatedFiles, path =>
            path.EndsWith("GeneratedCalculator.g.cs", StringComparison.OrdinalIgnoreCase));

        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, SourceGeneratorProjectFixture.InputSource(generatedLines: 1, value: 1))
        ], cancellationToken);
        Assert.True(
            update.Status is HotReloadDeltaUpdateStatus.Ready or HotReloadDeltaUpdateStatus.RestartRequired,
            $"Unexpected status {update.Status}: {string.Join(Environment.NewLine, update.Warnings)}");
        if (update.Status == HotReloadDeltaUpdateStatus.Ready)
        {
            Assert.True(update.LineUpdatesComplete);
            Assert.Contains(update.LineUpdates, lineUpdate =>
                lineUpdate.FilePath.EndsWith("GeneratedCalculator.g.cs", StringComparison.OrdinalIgnoreCase) &&
                lineUpdate.OldLine != lineUpdate.NewLine);
            session.DiscardUpdate();
        }
        else
        {
            Assert.Empty(update.MetadataDelta);
            Assert.False(session.HasPendingUpdate);
        }
    }

    [Fact]
    public async Task Session_RejectsLineMappedChangedSourceGeneratedDocumentsBeforeDeltaClassification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await SourceGeneratorProjectFixture.CreateAsync(cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var baselineSolution = ReadSessionSolution(session, "pdbSolution");
        var baselineProject = baselineSolution.Projects.Single(project =>
            string.Equals(project.FilePath, fixture.ProjectPath, StringComparison.OrdinalIgnoreCase));
        var inputDocument = baselineProject.Documents.Single(document =>
            string.Equals(document.FilePath, fixture.SourcePath, StringComparison.OrdinalIgnoreCase));
        var updatedSolution = baselineSolution.WithDocumentText(
            inputDocument.Id,
            SourceText.From(SourceGeneratorProjectFixture.InputSource(
                generatedLines: 0,
                value: 1,
                generatedLineDirective: 2)));
        var analyze = typeof(HotReloadDeltaSession).GetMethod(
            "AnalyzeRuntimeDocumentChangesAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var analysisTask = (Task)analyze.Invoke(null, [
            baselineProject,
            updatedSolution.GetProject(baselineProject.Id)!,
            cancellationToken
        ])!;
        await analysisTask;
        var analysis = analysisTask.GetType().GetProperty("Result")!.GetValue(analysisTask)!;

        Assert.False((bool)analysis.GetType().GetProperty("Success")!.GetValue(analysis)!);
        var error = (string?)analysis.GetType().GetProperty("Error")!.GetValue(analysis);
        Assert.Contains("#line", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Session_ResolvesRelativeGeneratedLineMappingAgainstProjectDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await SourceGeneratorProjectFixture.CreateAsync(cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var baselineSolution = ReadSessionSolution(session, "pdbSolution");
        var baselineProject = baselineSolution.Projects.Single(project =>
            string.Equals(project.FilePath, fixture.ProjectPath, StringComparison.OrdinalIgnoreCase));
        var inputDocument = baselineProject.Documents.Single(document =>
            string.Equals(document.FilePath, fixture.SourcePath, StringComparison.OrdinalIgnoreCase));
        var additionalDocument = baselineProject.AdditionalDocuments.Single(document =>
            string.Equals(document.FilePath, fixture.AdditionalPath, StringComparison.OrdinalIgnoreCase));
        var updatedSolution = baselineSolution
            .WithDocumentText(
                inputDocument.Id,
                SourceText.From(SourceGeneratorProjectFixture.InputSource(
                    generatedLines: 0,
                    value: 1,
                    generatedLineDirective: 1)))
            .WithAdditionalDocumentText(additionalDocument.Id, SourceText.From("updated"));

        var analysis = await AnalyzeRuntimeDocumentChangesAsync(
            baselineProject,
            updatedSolution.GetProject(baselineProject.Id)!,
            cancellationToken);

        Assert.True((bool)analysis.GetType().GetProperty("Success")!.GetValue(analysis)!);
    }

    [Fact]
    public async Task Session_RejectsEnhancedLineSpanMappedGeneratedDocument()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await SourceGeneratorProjectFixture.CreateAsync(cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var baselineSolution = ReadSessionSolution(session, "pdbSolution");
        var baselineProject = baselineSolution.Projects.Single(project =>
            string.Equals(project.FilePath, fixture.ProjectPath, StringComparison.OrdinalIgnoreCase));
        var inputDocument = baselineProject.Documents.Single(document =>
            string.Equals(document.FilePath, fixture.SourcePath, StringComparison.OrdinalIgnoreCase));
        var updatedSolution = baselineSolution.WithDocumentText(
            inputDocument.Id,
            SourceText.From(SourceGeneratorProjectFixture.InputSource(
                generatedLines: 0,
                value: 1,
                generatedLineSpanDirective: 1)));

        var analysis = await AnalyzeRuntimeDocumentChangesAsync(
            baselineProject,
            updatedSolution.GetProject(baselineProject.Id)!,
            cancellationToken);

        Assert.False((bool)analysis.GetType().GetProperty("Success")!.GetValue(analysis)!);
        var error = (string?)analysis.GetType().GetProperty("Error")!.GetValue(analysis);
        Assert.Contains("#line", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Session_DoesNotCreateRelocationDebtForUnchangedSourceGeneratedDocument()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await SourceGeneratorProjectFixture.CreateAsync(cancellationToken);
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var update = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, SourceGeneratorProjectFixture.InputSource(generatedLines: 0, value: 2))
        ], cancellationToken);

        Assert.True(
            update.Status == HotReloadDeltaUpdateStatus.Ready,
            string.Join(Environment.NewLine, update.Warnings));
        Assert.True(update.LineUpdatesComplete);
        Assert.DoesNotContain(update.LineUpdates, lineUpdate =>
            lineUpdate.FilePath.EndsWith("GeneratedCalculator.g.cs", StringComparison.OrdinalIgnoreCase));
        session.DiscardUpdate();
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
    public async Task Session_AdvancesSourceSnapshotWhenRoslynProducesNoDelta()
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
        var commentOnlySource = ProjectFixture.Source(1) + "\n// comment-only update\n";

        var noDelta = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, commentOnlySource)
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.NoChanges, noDelta.Status);
        Assert.False(session.HasPendingUpdate);

        var next = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.Source(2) + "\n// comment-only update\n")
        ], cancellationToken);
        var changedDocument = Assert.Single(next.ChangedDocuments);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(commentOnlySource))).ToLowerInvariant(),
            changedDocument.BaselineSha256);
        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, next.Status);
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_PreservesPortablePdbSourceSnapshotAcrossNoDeltaUpdate()
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
        var baselineSource = ProjectFixture.Source(1);
        var commentOnlySource = baselineSource + "\n// comment-only update\n";

        var noDelta = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, commentOnlySource)
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.NoChanges, noDelta.Status);
        Assert.False(session.HasPendingUpdate);
        Assert.Equal(commentOnlySource, await ReadSessionDocumentAsync(session, "solution", fixture.SourcePath));
        Assert.Equal(baselineSource, await ReadSessionDocumentAsync(session, "pdbSolution", fixture.SourcePath));

        var nextSource = ProjectFixture.SourceWithLineShift(2) + "\n// comment-only update\n";
        var next = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, nextSource)
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, next.Status);
        Assert.Contains(next.LineUpdates, lineUpdate =>
            lineUpdate.OldLine == FindLine(ProjectFixture.Source(1), "public static int Unchanged()") &&
            lineUpdate.NewLine == FindLine(nextSource, "public static int Unchanged()"));
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_TreatsDocumentWithNoSequencePointsAsVacuouslyMapped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(
            ProjectFixture.Source(1),
            cancellationToken,
            additionalSource: ProjectFixture.InterfaceOnlySource(updated: false));
        using var session = await HotReloadDeltaSession.StartAsync(
            fixture.ProjectPath,
            "Debug",
            "net10.0",
            properties: null,
            runtimeCapabilities: ["Baseline"],
            cancellationToken);

        var noDelta = await session.PrepareUpdateAsync([
            new(fixture.SecondaryPath, ProjectFixture.InterfaceOnlySource(updated: true))
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.NoChanges, noDelta.Status);
        Assert.False(session.HasPendingUpdate);

        var emitting = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.Source(2))
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, emitting.Status);
        Assert.True(emitting.LineUpdatesComplete);
        Assert.Empty(emitting.LineUpdates);
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_CollectsRuntimeDivergenceOutsideCurrentChangedFile()
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
        var pdbSolution = ReadSessionSolution(session, "pdbSolution");
        var updatedSolution = ReadSessionSolution(session, "solution");
        var sourceDocument = updatedSolution.Projects
            .SelectMany(static project => project.Documents)
            .Single(document => document.FilePath == fixture.SourcePath);
        var secondaryDocument = updatedSolution.Projects
            .SelectMany(static project => project.Documents)
            .Single(document => document.FilePath == fixture.SecondaryPath);
        updatedSolution = updatedSolution
            .WithDocumentText(sourceDocument.Id, SourceText.From(ProjectFixture.SourceWithLineShift(1)))
            .WithDocumentText(secondaryDocument.Id, SourceText.From(ProjectFixture.SecondaryActiveSource(3)));
        var analyzer = typeof(HotReloadDeltaSession).GetMethod(
            "AnalyzeRuntimeDocumentChangesAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task)analyzer.Invoke(null, [
            pdbSolution.GetProject(sourceDocument.Project.Id)!,
            updatedSolution.GetProject(sourceDocument.Project.Id)!,
            cancellationToken
        ])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var files = (ImmutableArray<string>)result.GetType().GetProperty("Files")!.GetValue(result)!;

        Assert.Contains(fixture.SourcePath, files);
        Assert.Contains(fixture.SecondaryPath, files);
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
            new(fixture.SecondaryPath, ProjectFixture.SecondaryActiveSource(3))
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, update.Status);
        Assert.NotEmpty(update.LineUpdates);
        Assert.All(update.LineUpdates, lineUpdate => Assert.Equal(fixture.SourcePath, lineUpdate.FilePath));
        Assert.Contains(fixture.SecondaryPath, update.ChangedFiles);
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_RequiresRestartWhenUpdatedMethodSequencePointIsDeleted()
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
            new(fixture.SecondaryPath, ProjectFixture.SecondaryActiveSourceWithoutSeed(3))
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.False(update.LineUpdatesComplete);
        Assert.Contains(update.Warnings, warning =>
            warning.Contains("ambiguous sequence-point mapping", StringComparison.Ordinal));
        Assert.False(session.HasPendingUpdate);
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
    public async Task Session_MapsMovedChangedStatementWithoutStableTextIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var baseline = ProjectFixture.MovedChangedStatementSource(updated: false);
        var updated = ProjectFixture.MovedChangedStatementSource(updated: true);
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
        Assert.Contains(update.LineUpdates, lineUpdate =>
            lineUpdate.OldLine == FindLine(baseline, "F();") &&
            lineUpdate.NewLine == FindLine(updated, "G();"));
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_IgnoresNonExecutableLinePermutationDuringLineStableUpdate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var baseline = ProjectFixture.BlankPermutationSource(1, updated: false);
        var updated = ProjectFixture.BlankPermutationSource(2, updated: true);
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
        Assert.Empty(update.LineUpdates);
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
    public async Task Session_DistinguishesFunctionPointerCallingConventionsInTokenIdentities()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(
            ProjectFixture.FunctionPointerOverloadSource(1, 2, reverse: false),
            cancellationToken);
        var assemblyPath = Path.Combine(fixture.Root, "bin", "Debug", "net10.0", "DeltaFixture.dll");
        var reader = typeof(HotReloadDeltaSession).GetMethod(
            "ReadMethodTokenIdentities",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var identities = (Dictionary<int, string>)reader.Invoke(null, [
            (await File.ReadAllBytesAsync(assemblyPath, cancellationToken)).ToImmutableArray()
        ])!;
        var overloads = identities.Values
            .Where(identity => identity.Contains("::M`0", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, overloads.Length);
        Assert.All(overloads, identity => Assert.Contains("fnptr[header=", identity, StringComparison.Ordinal));
        Assert.NotEqual(overloads[0], overloads[1]);
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
    public async Task Session_PortablePdbCaptureRetainsTheValidatedBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(ProjectFixture.Source(1), cancellationToken);
        using var otherFixture = await RazorProjectFixture.CreateAsync(cancellationToken);
        var assemblyPath = Path.Combine(fixture.Root, "bin", "Debug", "net10.0", "DeltaFixture.dll");
        var pdbPath = ReadCodeViewPath(assemblyPath);
        var originalBytes = await File.ReadAllBytesAsync(pdbPath, cancellationToken);
        var captureMethod = typeof(HotReloadDeltaSession).GetMethod(
            "CapturePortablePdb",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var assemblyBytes = (await File.ReadAllBytesAsync(assemblyPath, cancellationToken)).ToImmutableArray();
        var capture = captureMethod.Invoke(null, [assemblyPath, assemblyBytes])!;

        var otherPdbPath = Path.Combine(otherFixture.Root, "bin", "Debug", "net10.0", "RazorDeltaFixture.pdb");
        File.Copy(otherPdbPath, pdbPath, overwrite: true);

        var capturedImage = (ImmutableArray<byte>)capture.GetType().GetProperty("Image")!.GetValue(capture)!;
        Assert.Equal(originalBytes, capturedImage.ToArray());
        Assert.NotEqual(await File.ReadAllBytesAsync(pdbPath, cancellationToken), capturedImage.ToArray());
    }

    [Fact]
    public async Task Session_PortablePdbLookupRejectsFileThatDoesNotMatchBaselineAssembly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(ProjectFixture.Source(1), cancellationToken);
        using var otherFixture = await RazorProjectFixture.CreateAsync(cancellationToken);
        var assemblyPath = Path.Combine(fixture.Root, "bin", "Debug", "net10.0", "DeltaFixture.dll");
        var pdbPath = ReadCodeViewPath(assemblyPath);
        var siblingPdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        var otherPdbPath = Path.Combine(otherFixture.Root, "bin", "Debug", "net10.0", "RazorDeltaFixture.pdb");
        File.Copy(otherPdbPath, pdbPath, overwrite: true);
        if (!string.Equals(pdbPath, siblingPdbPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(otherPdbPath, siblingPdbPath, overwrite: true);
        }

        var finder = typeof(HotReloadDeltaSession).GetMethod(
            "CapturePortablePdb",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var assemblyBytes = (await File.ReadAllBytesAsync(assemblyPath, cancellationToken)).ToImmutableArray();
        var exception = Assert.Throws<TargetInvocationException>(() => finder.Invoke(null, [
            assemblyPath,
            assemblyBytes
        ]));

        var mismatch = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("matching external portable PDB", mismatch.Message, StringComparison.Ordinal);
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
    public async Task Session_ReleasesWorkspaceWhenBaselineAssemblyIsMissing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(cancellationToken);
        var assemblyPath = Path.Combine(fixture.Root, "bin", "Debug", "net10.0", "DeltaFixture.dll");
        File.Delete(assemblyPath);

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
    public void Session_RequiresCoverageForEveryLineMovingDocument()
    {
        var finder = typeof(HotReloadDeltaSession).GetMethod(
            "FindMissingPaths",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var missing = (ImmutableArray<string>)finder.Invoke(null, [
            new[] { "first.cs", "second.cs", "third.cs" },
            new[] { "third.cs" }
        ])!;

        Assert.Equal(["first.cs", "second.cs"], missing);
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
    public async Task Worker_RejectsAllowedLookingSymlinkToUnsupportedDocumentExtension()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(cancellationToken);
        var targetPath = Path.Combine(fixture.Root, "settings.txt");
        var linkPath = Path.Combine(fixture.Root, "settings.cs");
        await File.WriteAllTextAsync(targetPath, "baseline", cancellationToken);
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
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
                        new { filePath = linkPath, text = "changed" }
                    },
                    artifactDirectory = Path.Combine(fixture.Root, "artifacts")
                },
                cancellationToken);
            Assert.False(rejected.GetProperty("success").GetBoolean());
            Assert.Equal(
                "hot_reload_document_invalid",
                rejected.GetProperty("error").GetProperty("code").GetString());

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_RetainsResolvedWorkspaceRootForLaterRequests(bool useAliasPaths)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await ProjectFixture.CreateAsync(cancellationToken);
        var aliasParent = Path.Combine(
            Path.GetTempPath(),
            "hotreload-delta-generator-tests-alias",
            Guid.NewGuid().ToString("N"));
        var aliasRoot = Path.Combine(aliasParent, "workspace");
        Directory.CreateDirectory(aliasParent);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(aliasRoot, fixture.Root);
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
                var projectPath = useAliasPaths
                    ? Path.Combine(aliasRoot, Path.GetFileName(fixture.ProjectPath))
                    : fixture.ProjectPath;
                var sourcePath = useAliasPaths
                    ? Path.Combine(aliasRoot, Path.GetFileName(fixture.SourcePath))
                    : fixture.SourcePath;
                var artifactDirectory = useAliasPaths
                    ? Path.Combine(aliasRoot, "artifacts")
                    : Path.Combine(fixture.Root, "artifacts");
                var started = await SendWorkerRequestAsync(
                    worker,
                    "start",
                    "startSession",
                    new
                    {
                        projectPath,
                        workspaceRoot = aliasRoot,
                        configuration = "Debug",
                        targetFramework = "net10.0",
                        msBuildProperties = (object?)null,
                        runtimeCapabilities = new[] { "Baseline" }
                    },
                    cancellationToken);
                Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());
                Assert.Equal(
                    Path.GetFullPath(fixture.Root),
                    started.GetProperty("result").GetProperty("workspaceRoot").GetString());
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
                            new
                            {
                                filePath = sourcePath,
                                text = ProjectFixture.Source(2)
                            }
                        },
                        artifactDirectory
                    },
                    cancellationToken);
                Assert.True(prepared.GetProperty("success").GetBoolean(), prepared.ToString());
                var artifacts = prepared.GetProperty("result").GetProperty("artifacts");
                Assert.True(artifacts.GetArrayLength() > 0, prepared.ToString());
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
        finally
        {
            try
            {
                if (Directory.Exists(aliasRoot))
                {
                    Directory.Delete(aliasRoot);
                }
                Directory.Delete(aliasParent);
            }
            catch
            {
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

    private static string ReadCodeViewPath(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var reader = new PEReader(stream);
        var entry = reader.ReadDebugDirectory().Single(candidate => candidate.Type == DebugDirectoryEntryType.CodeView);
        var path = reader.ReadCodeViewDebugDirectoryData(entry).Path;
        return Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(Path.GetDirectoryName(assemblyPath)!, path));
    }

    private static async Task<string> ReadSessionDocumentAsync(
        HotReloadDeltaSession session,
        string fieldName,
        string filePath)
    {
        var field = typeof(HotReloadDeltaSession).GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var solution = (Microsoft.CodeAnalysis.Solution)field.GetValue(session)!;
        var document = solution.Projects
            .SelectMany(static project => project.Documents)
            .Single(candidate => string.Equals(candidate.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        return (await document.GetTextAsync(TestContext.Current.CancellationToken)).ToString();
    }

    private static Microsoft.CodeAnalysis.Solution ReadSessionSolution(
        HotReloadDeltaSession session,
        string fieldName)
    {
        var field = typeof(HotReloadDeltaSession).GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Microsoft.CodeAnalysis.Solution)field.GetValue(session)!;
    }

    private static async Task<object> AnalyzeRuntimeDocumentChangesAsync(
        Microsoft.CodeAnalysis.Project baselineProject,
        Microsoft.CodeAnalysis.Project updatedProject,
        CancellationToken cancellationToken)
    {
        var analyze = typeof(HotReloadDeltaSession).GetMethod(
            "AnalyzeRuntimeDocumentChangesAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var analysisTask = (Task)analyze.Invoke(null, [
            baselineProject,
            updatedProject,
            cancellationToken
        ])!;
        await analysisTask;
        return analysisTask.GetType().GetProperty("Result")!.GetValue(analysisTask)!;
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

        public const string MovedExpressionBaseline = """
            @namespace RazorDeltaFixture
            @* old *@
            <p>@A(1)</p>
            @code {
                private int A(int value) => value;
                private int B(int value) => value;
            }
            """;

        public const string MovedExpressionUpdated = """
            @namespace RazorDeltaFixture
            <p>@B(2)</p>
            @* new *@
            @code {
                private int A(int value) => value;
                private int B(int value) => value;
            }
            """;

        public const string SwappedRegionsBaseline = """
            @namespace RazorDeltaFixture
            <p>@A(1)</p>
            <p>@B(2)</p>
            @code {
                private int A(int value) => value;
                private int B(int value) => value;
            }
            """;

        public const string SwappedRegionsUpdated = """
            @namespace RazorDeltaFixture
            <p>@B(2)</p>
            <p>@A(1)</p>
            @code {
                private int A(int value) => value;
                private int B(int value) => value;
            }
            """;

        public const string SwappedAndEditedRegionsUpdated = """
            @namespace RazorDeltaFixture
            <p>@B(3)</p>
            <p>@A(4)</p>
            @code {
                private int A(int value) => value;
                private int B(int value) => value;
            }
            """;

        public const string DuplicateStructuralAnchorsBaseline = """
            @namespace RazorDeltaFixture
            <p>@A(1)</p>
            <p>@A(2)</p>
            @code {
                private int A(int value) => value;
            }
            """;

        public const string DuplicateStructuralAnchorsSwapped = """
            @namespace RazorDeltaFixture
            <p>@A(2)</p>
            <p>@A(1)</p>
            @code {
                private int A(int value) => value;
            }
            """;

        public const string LiteralWhitespaceBaseline = """
            @namespace RazorDeltaFixture
            <p>@A("a b")</p>
            <p>@A("ab")</p>
            @code {
                private string A(string value) => value;
            }
            """;

        public const string LiteralWhitespaceSwapped = """
            @namespace RazorDeltaFixture
            <p>@A("ab")</p>
            <p>@A("a b")</p>
            @code {
                private string A(string value) => value;
            }
            """;

        public const string SameLinePointsBaseline = """
            @namespace RazorDeltaFixture
            <p>@A(1) @B(2)</p>
            @code {
                private int A(int value) => value;
                private int B(int value) => value;
            }
            """;

        public const string SameLinePointsUpdated = """
            @namespace RazorDeltaFixture
            <p>@A(3) @B(4)</p>
            @code {
                private int A(int value) => value;
                private int B(int value) => value;
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

    private sealed class SourceGeneratorProjectFixture : IDisposable
    {
        private SourceGeneratorProjectFixture(string root)
        {
            Root = root;
            ProjectPath = Path.Combine(root, "GeneratedDeltaFixture.csproj");
            SourcePath = Path.Combine(root, "GeneratorInput.cs");
            AdditionalPath = Path.Combine(root, "input.cs");
            GeneratorDirectory = Path.Combine(root, "DrivingGenerator");
        }

        public string Root { get; }

        public string ProjectPath { get; }

        public string SourcePath { get; }

        public string AdditionalPath { get; }

        private string GeneratorDirectory { get; }

        public static string InputSource(
            int generatedLines,
            int value,
            int generatedLineDirective = 0,
            int generatedLineSpanDirective = 0) => $$"""
            namespace GeneratedDeltaFixture;

            public static class GeneratorInput
            {
                public const int GeneratedLines = {{generatedLines}};
                public const int GeneratedLineDirective = {{generatedLineDirective}};
                public const int GeneratedLineSpanDirective = {{generatedLineSpanDirective}};
                public static int Value() => {{value}};
            }
            """;

        public static async Task<SourceGeneratorProjectFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "hotreload-source-generator-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new SourceGeneratorProjectFixture(root);
            Directory.CreateDirectory(fixture.GeneratorDirectory);
            var generatorProjectPath = Path.Combine(fixture.GeneratorDirectory, "DrivingGenerator.csproj");
            var generatorSourcePath = Path.Combine(fixture.GeneratorDirectory, "DrivingGenerator.cs");
            await File.WriteAllTextAsync(generatorProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Microsoft.CodeAnalysis">
                      <HintPath>$(MSBuildSDKsPath)\..\Roslyn\bincore\Microsoft.CodeAnalysis.dll</HintPath>
                      <Private>false</Private>
                    </Reference>
                    <Reference Include="Microsoft.CodeAnalysis.CSharp">
                      <HintPath>$(MSBuildSDKsPath)\..\Roslyn\bincore\Microsoft.CodeAnalysis.CSharp.dll</HintPath>
                      <Private>false</Private>
                    </Reference>
                  </ItemGroup>
                </Project>
                """, cancellationToken);
            await File.WriteAllTextAsync(generatorSourcePath, """"
                using System.Linq;
                using System.Text;
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp.Syntax;
                using Microsoft.CodeAnalysis.Text;

                [Generator]
                public sealed class DrivingGenerator : ISourceGenerator
                {
                    public void Initialize(GeneratorInitializationContext context)
                    {
                    }

                    public void Execute(GeneratorExecutionContext context)
                    {
                        var generatedLines = context.Compilation.SyntaxTrees
                            .SelectMany(static tree => tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>())
                            .Where(static variable => variable.Identifier.ValueText == "GeneratedLines")
                            .Select(static variable => variable.Initializer?.Value)
                            .OfType<LiteralExpressionSyntax>()
                            .Select(static literal => (int)literal.Token.Value!)
                            .Single();
                        var generatedLineDirective = context.Compilation.SyntaxTrees
                            .SelectMany(static tree => tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>())
                            .Where(static variable => variable.Identifier.ValueText == "GeneratedLineDirective")
                            .Select(static variable => variable.Initializer?.Value)
                            .OfType<LiteralExpressionSyntax>()
                            .Select(static literal => (int)literal.Token.Value!)
                            .Single();
                        var generatedLineSpanDirective = context.Compilation.SyntaxTrees
                            .SelectMany(static tree => tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>())
                            .Where(static variable => variable.Identifier.ValueText == "GeneratedLineSpanDirective")
                            .Select(static variable => variable.Initializer?.Value)
                            .OfType<LiteralExpressionSyntax>()
                            .Select(static literal => (int)literal.Token.Value!)
                            .Single();
                        var padding = string.Concat(Enumerable.Repeat("        // generated padding\n", generatedLines));
                        var lineDirective = generatedLineSpanDirective != 0
                            ? "        #line (1,1)-(1,10) \"untracked.cs\"\n"
                            : generatedLineDirective == 0
                                ? string.Empty
                                : $"        #line {generatedLineDirective} \"input.cs\"\n";
                        var source = $$"""
                            namespace GeneratedDeltaFixture;

                            public static class GeneratedCalculator
                            {
                                public static int GetValue()
                                {
                            {{padding}}{{lineDirective}}        return 10;
                                }
                            }
                            """;
                        context.AddSource("GeneratedCalculator.g.cs", SourceText.From(source, Encoding.UTF8));
                    }
                }
                """", cancellationToken);
            await File.WriteAllTextAsync(fixture.ProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <DebugType>portable</DebugType>
                    <Optimize>false</Optimize>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Remove="DrivingGenerator\**\*.cs" />
                    <Compile Remove="input.cs" />
                    <AdditionalFiles Include="input.cs" />
                    <ProjectReference Include="DrivingGenerator\DrivingGenerator.csproj"
                                      OutputItemType="Analyzer"
                                      ReferenceOutputAssembly="false" />
                  </ItemGroup>
                </Project>
                """, cancellationToken);
            await File.WriteAllTextAsync(
                fixture.SourcePath,
                InputSource(generatedLines: 0, value: 1),
                cancellationToken);
            await File.WriteAllTextAsync(fixture.AdditionalPath, "baseline", cancellationToken);

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
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                fixture.Dispose();
                throw new InvalidOperationException($"Source-generator fixture build failed: {output}{error}");
            }

            return fixture;
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
                    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
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

        public static string MovedChangedStatementSource(bool updated) => updated
            ? """
                namespace DeltaFixture;

                public static class Calculator
                {
                    public static int Run()
                    {
                        G();
                        // new
                        return 1;
                    }

                    private static void F() { }
                    private static void G() { }
                }
                """
            : """
                namespace DeltaFixture;

                public static class Calculator
                {
                    public static int Run()
                    {
                        // old
                        F();
                        return 1;
                    }

                    private static void F() { }
                    private static void G() { }
                }
                """;

        public static string BlankPermutationSource(int value, bool updated) => updated
            ? $$"""
                namespace DeltaFixture;

                public static class Calculator
                {

                    // marker
                    public static int Value() => {{value}};
                }
                """
            : $$"""
                namespace DeltaFixture;

                public static class Calculator
                {
                    // marker

                    public static int Value() => {{value}};
                }
                """;

        public static string ExpressionLambdaSource(bool fieldLambda, bool shifted)
        {
            var declaration = fieldLambda
                ? "    private static readonly System.Func<int, int> Transform ="
                : "        System.Func<int, int> transform =";
            var invocation = fieldLambda ? "Transform(2)" : "transform(2)";
            var bodyIndent = fieldLambda ? "        " : "            ";
            var lambda = shifted
                ? $"{declaration}\n{bodyIndent}// inserted line\n{bodyIndent}value => value * 3;"
                : $"{declaration}\n{bodyIndent}value => value * 2;\n{bodyIndent}// removable line";
            return fieldLambda
                ? $$"""
                    namespace DeltaFixture;

                    public static class Calculator
                    {
                    {{lambda}}

                        public static int Run() => {{invocation}};
                    }
                    """
                : $$"""
                    namespace DeltaFixture;

                    public static class Calculator
                    {
                        public static int Run()
                        {
                    {{lambda}}
                            return {{invocation}};
                        }
                    }
                    """;
        }

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

        public static string ShiftedArrowExpressionSource(bool property, bool updated)
        {
            var member = (property, updated) switch
            {
                (true, false) => "    public static int Value => F(1);\n    // movable comment",
                (true, true) => "    public static int Value =>\n        F(2);",
                (false, false) => "    public static int Value() => F(1);\n    // movable comment",
                _ => "    public static int Value() =>\n        F(2);"
            };
            return $$"""
                namespace DeltaFixture;

                public static class Calculator
                {
                {{member}}
                    public static int Unchanged => 10;
                    private static int F(int value) => value;
                }
                """;
        }

        public static string FunctionPointerOverloadSource(int first, int second, bool reverse)
        {
            var managed = $"    public static int M(delegate* managed<void> callback) => {first};";
            var unmanaged = $"    public static int M(delegate* unmanaged<void> callback) => {second};";
            var methods = reverse ? unmanaged + "\n" + managed : managed + "\n" + unmanaged;
            return $$"""
                namespace DeltaFixture;

                public static unsafe class Calculator
                {
                {{methods}}
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

        public static string InterfaceOnlySource(bool updated) => updated
            ? """
                namespace DeltaFixture;

                // updated documentation
                public interface IMarker
                {
                }
                """
            : """
                namespace DeltaFixture;

                // baseline documentation
                public interface IMarker
                {
                }
                """;

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
