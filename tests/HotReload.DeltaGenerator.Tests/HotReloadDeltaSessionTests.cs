// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
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
