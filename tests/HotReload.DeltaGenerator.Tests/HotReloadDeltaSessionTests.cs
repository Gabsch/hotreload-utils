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
        Assert.True(session.HasPendingUpdate);

        session.DiscardUpdate();
        var regenerated = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.Source(2))
        ], cancellationToken);
        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, regenerated.Status);
        Assert.Equal(first.ModuleId, regenerated.ModuleId);
        session.CommitUpdate();

        var second = await session.PrepareUpdateAsync([
            new(fixture.SourcePath, ProjectFixture.Source(3))
        ], cancellationToken);
        Assert.Equal(HotReloadDeltaUpdateStatus.Ready, second.Status);
        session.DiscardUpdate();
    }

    [Fact]
    public async Task Session_ClassifiesLineMovingEditAsRestartRequired()
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
            new(fixture.SourcePath, ProjectFixture.Source(2) + Environment.NewLine)
        ], cancellationToken);

        Assert.Equal(HotReloadDeltaUpdateStatus.RestartRequired, update.Status);
        Assert.False(update.LineUpdatesComplete);
        Assert.False(session.HasPendingUpdate);
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

        public static async Task<ProjectFixture> CreateAsync(CancellationToken cancellationToken)
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
            await File.WriteAllTextAsync(fixture.SourcePath, Source(1), cancellationToken);
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
