// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.HotReload.Utils.Generator;
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

        public static async Task<RazorProjectFixture> CreateAsync(CancellationToken cancellationToken)
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
            await File.WriteAllTextAsync(fixture.ComponentPath, BaselineSource, cancellationToken);

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
        }

        public string Root { get; }

        public string ProjectPath { get; }

        public string SourcePath { get; }

        public static Task<ProjectFixture> CreateAsync(CancellationToken cancellationToken) =>
            CreateAsync(Source(1), cancellationToken);

        public static async Task<ProjectFixture> CreateAsync(
            string initialSource,
            CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "hotreload-delta-generator-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new ProjectFixture(root);
            await File.WriteAllTextAsync(fixture.ProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <DebugType>portable</DebugType>
                    <Optimize>false</Optimize>
                  </PropertyGroup>
                </Project>
                """, cancellationToken);
            await File.WriteAllTextAsync(fixture.SourcePath, initialSource, cancellationToken);
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
