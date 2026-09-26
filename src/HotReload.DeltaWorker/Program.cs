// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.DotNet.HotReload.Utils.Generator;

const int ProtocolVersion = 2;
var protocolOutput = Console.Out;
Console.SetOut(Console.Error);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};
var sessions = new Dictionary<string, WorkerSession>(StringComparer.Ordinal);

while (await Console.In.ReadLineAsync() is { } line)
{
    WorkerResponse response;
    WorkerRequest? request = null;
    try
    {
        request = JsonSerializer.Deserialize<WorkerRequest>(line, jsonOptions)
            ?? throw new InvalidOperationException("Request payload is empty.");
        if (request.ProtocolVersion != ProtocolVersion)
        {
            throw new WorkerException(
                "hot_reload_protocol_mismatch",
                $"Hot Reload delta worker protocol v{ProtocolVersion} is required.");
        }

        var result = request.Method switch
        {
            "startSession" => await StartSessionAsync(request.Parameters, sessions, jsonOptions),
            "prepareUpdate" => await PrepareUpdateAsync(request.Parameters, sessions, jsonOptions),
            "commitUpdate" => CommitUpdate(request.Parameters, sessions, jsonOptions),
            "discardUpdate" => DiscardUpdate(request.Parameters, sessions, jsonOptions),
            "endSession" => EndSession(request.Parameters, sessions, jsonOptions),
            _ => throw new WorkerException(
                "hot_reload_method_unknown",
                $"Unknown worker method '{request.Method}'.")
        };
        response = new(ProtocolVersion, request.Id, Success: true, result, Error: null);
    }
    catch (WorkerException exception)
    {
        response = new(
            ProtocolVersion,
            request?.Id ?? string.Empty,
            Success: false,
            Result: null,
            new WorkerError(exception.Code, exception.Message));
    }
    catch (Exception exception)
    {
        response = new(
            ProtocolVersion,
            request?.Id ?? string.Empty,
            Success: false,
            Result: null,
            new WorkerError("hot_reload_worker_error", exception.Message));
    }

    await protocolOutput.WriteLineAsync(JsonSerializer.Serialize(response, jsonOptions));
    await protocolOutput.FlushAsync();
}

foreach (var session in sessions.Values)
{
    session.Session.Dispose();
}

static async Task<object> StartSessionAsync(
    JsonElement parameters,
    IDictionary<string, WorkerSession> sessions,
    JsonSerializerOptions options)
{
    var request = parameters.Deserialize<StartSessionRequest>(options)
        ?? throw new WorkerException("hot_reload_invalid_request", "startSession parameters are required.");
    var workspaceRoot = Path.GetFullPath(request.WorkspaceRoot);
    var projectPath = Path.GetFullPath(request.ProjectPath);
    if (!Directory.Exists(workspaceRoot) ||
        !IsUnderRoot(workspaceRoot, projectPath) ||
        !File.Exists(projectPath) ||
        !string.Equals(Path.GetExtension(projectPath), ".csproj", StringComparison.OrdinalIgnoreCase))
    {
        throw new WorkerException(
            "hot_reload_baseline_invalid",
            "projectPath must identify an existing .csproj under workspaceRoot.");
    }

    var session = await HotReloadDeltaSession.StartAsync(
        projectPath,
        request.Configuration,
        request.TargetFramework,
        request.MsBuildProperties,
        request.RuntimeCapabilities);
    var sessionId = "hr_" + Guid.NewGuid().ToString("N");
    var moduleId = ReadModuleId(session.Info.OutputAssemblyPath);
    var runtimeCapabilities = request.RuntimeCapabilities is { Count: > 0 }
        ? request.RuntimeCapabilities
        : ["Baseline"];
    var workerSession = new WorkerSession(
        sessionId,
        workspaceRoot,
        runtimeCapabilities,
        session,
        moduleId);
    sessions.Add(sessionId, workerSession);
    return new
    {
        sessionId,
        projectPath = session.Info.ProjectPath,
        workspaceRoot,
        configuration = session.Info.Configuration,
        targetFramework = session.Info.TargetFramework,
        outputAssemblyPath = session.Info.OutputAssemblyPath,
        pdbPath = session.Info.PdbPath,
        moduleName = session.Info.ModuleName,
        moduleId,
        runtimeCapabilities = workerSession.RuntimeCapabilities,
        startedAt = DateTimeOffset.UtcNow
    };
}

static async Task<object> PrepareUpdateAsync(
    JsonElement parameters,
    IDictionary<string, WorkerSession> sessions,
    JsonSerializerOptions options)
{
    var request = parameters.Deserialize<PrepareUpdateRequest>(options)
        ?? throw new WorkerException("hot_reload_invalid_request", "prepareUpdate parameters are required.");
    var session = GetSession(request.SessionId, sessions);
    if (session.PendingUpdateId is not null)
    {
        throw new WorkerException(
            "hot_reload_update_pending",
            "Commit or discard the current Hot Reload update first.");
    }

    var artifactDirectory = Path.GetFullPath(request.ArtifactDirectory);
    if (!IsUnderRoot(session.WorkspaceRoot, artifactDirectory))
    {
        throw new WorkerException(
            "hot_reload_path_outside_workspace",
            "artifactDirectory must be under workspaceRoot.");
    }

    var changes = request.ChangedDocuments.Select(document =>
    {
        var filePath = Path.GetFullPath(document.FilePath);
        var extension = Path.GetExtension(filePath);
        if (!IsUnderRoot(session.WorkspaceRoot, filePath) ||
            !string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".razor", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkerException(
                "hot_reload_document_invalid",
                $"Changed document is outside the workspace or is not C# or Razor: {document.FilePath}");
        }

        return new HotReloadDeltaDocumentChange(filePath, document.Text);
    }).ToArray();
    var prepared = await session.Session.PrepareUpdateAsync(changes);
    var updateId = "upd_" + Guid.NewGuid().ToString("N");
    var artifacts = new List<ArtifactData>();
    if (prepared.Status == HotReloadDeltaUpdateStatus.Ready)
    {
        Directory.CreateDirectory(artifactDirectory);
        artifacts.Add(WriteArtifact(artifactDirectory, updateId + ".dmeta", "metadata", prepared.MetadataDelta.AsSpan()));
        artifacts.Add(WriteArtifact(artifactDirectory, updateId + ".dil", "il", prepared.IlDelta.AsSpan()));
        artifacts.Add(WriteArtifact(artifactDirectory, updateId + ".dpdb", "pdb", prepared.PdbDelta.AsSpan()));
        artifacts.Add(WriteLineUpdatesArtifact(
            artifactDirectory,
            updateId + ".dlines",
            prepared.LineUpdates));
        session.PendingUpdateId = updateId;
    }

    return new
    {
        sessionId = session.SessionId,
        updateId,
        status = prepared.Status,
        moduleName = prepared.ModuleName,
        moduleId = prepared.ModuleId,
        changedFiles = prepared.ChangedFiles,
        artifacts,
        updatedTypes = prepared.UpdatedTypes,
        updatedMethods = prepared.UpdatedMethods,
        changedDocuments = prepared.ChangedDocuments,
        requiredCapabilities = prepared.RequiredCapabilities,
        diagnostics = prepared.Diagnostics,
        lineUpdatesComplete = prepared.LineUpdatesComplete,
        warnings = prepared.Warnings
    };
}

static ArtifactData WriteLineUpdatesArtifact(
    string artifactDirectory,
    string fileName,
    IReadOnlyList<HotReloadDeltaLineUpdate> updates)
{
    var path = Path.Combine(artifactDirectory, fileName);
    var groupedUpdates = updates
        .GroupBy(update => update.FilePath, StringComparer.Ordinal)
        .ToArray();
    using (var stream = File.Create(path))
    using (var writer = new BinaryWriter(stream))
    {
        writer.Write(groupedUpdates.Length);
        foreach (var group in groupedUpdates)
        {
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(group.Key);
            writer.Write(pathBytes.Length);
            writer.Write(pathBytes);
            var entries = group.ToArray();
            writer.Write(entries.Length);
            foreach (var update in entries)
            {
                writer.Write(update.NewLine);
                writer.Write(update.OldLine);
            }
        }
    }

    var content = File.ReadAllBytes(path);
    return new(
        "lineUpdates",
        path,
        content.Length,
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
}

static object CommitUpdate(
    JsonElement parameters,
    IDictionary<string, WorkerSession> sessions,
    JsonSerializerOptions options)
{
    var request = parameters.Deserialize<CompleteUpdateRequest>(options)
        ?? throw new WorkerException("hot_reload_invalid_request", "commitUpdate parameters are required.");
    var session = GetPendingSession(request, sessions);
    session.Session.CommitUpdate();
    session.PendingUpdateId = null;
    return new
    {
        sessionId = session.SessionId,
        updateId = request.UpdateId,
        committed = true,
        completedAt = DateTimeOffset.UtcNow
    };
}

static object DiscardUpdate(
    JsonElement parameters,
    IDictionary<string, WorkerSession> sessions,
    JsonSerializerOptions options)
{
    var request = parameters.Deserialize<CompleteUpdateRequest>(options)
        ?? throw new WorkerException("hot_reload_invalid_request", "discardUpdate parameters are required.");
    var session = GetPendingSession(request, sessions);
    session.Session.DiscardUpdate();
    session.PendingUpdateId = null;
    return new
    {
        sessionId = session.SessionId,
        updateId = request.UpdateId,
        committed = false,
        completedAt = DateTimeOffset.UtcNow
    };
}

static object EndSession(
    JsonElement parameters,
    IDictionary<string, WorkerSession> sessions,
    JsonSerializerOptions options)
{
    var request = parameters.Deserialize<EndSessionRequest>(options)
        ?? throw new WorkerException("hot_reload_invalid_request", "endSession parameters are required.");
    if (!sessions.Remove(request.SessionId, out var session))
    {
        throw new WorkerException("hot_reload_session_invalid", "The Hot Reload session does not exist.");
    }

    session.Session.Dispose();
    return true;
}

static WorkerSession GetSession(string sessionId, IDictionary<string, WorkerSession> sessions) =>
    sessions.TryGetValue(sessionId, out var session)
        ? session
        : throw new WorkerException("hot_reload_session_invalid", "The Hot Reload session does not exist.");

static WorkerSession GetPendingSession(
    CompleteUpdateRequest request,
    IDictionary<string, WorkerSession> sessions)
{
    var session = GetSession(request.SessionId, sessions);
    if (!string.Equals(session.PendingUpdateId, request.UpdateId, StringComparison.Ordinal))
    {
        throw new WorkerException(
            "hot_reload_update_invalid",
            "The update is not pending for this session.");
    }

    return session;
}

static ArtifactData WriteArtifact(
    string directory,
    string name,
    string kind,
    ReadOnlySpan<byte> content)
{
    var path = Path.Combine(directory, name);
    File.WriteAllBytes(path, content);
    return new(
        kind,
        path,
        content.Length,
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
}

static Guid ReadModuleId(string assemblyPath)
{
    using var stream = File.OpenRead(assemblyPath);
    using var peReader = new PEReader(stream);
    var metadata = peReader.GetMetadataReader();
    return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
}

static bool IsUnderRoot(string root, string path)
{
    var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
    return Path.GetFullPath(path).StartsWith(
        normalizedRoot,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

internal sealed record WorkerRequest(
    int ProtocolVersion,
    string Id,
    string Method,
    JsonElement Parameters);

internal sealed record WorkerResponse(
    int ProtocolVersion,
    string Id,
    bool Success,
    object? Result,
    WorkerError? Error);

internal sealed record WorkerError(string Code, string Message);

internal sealed record StartSessionRequest(
    string ProjectPath,
    string WorkspaceRoot,
    string Configuration,
    string? TargetFramework,
    Dictionary<string, string>? MsBuildProperties,
    List<string>? RuntimeCapabilities);

internal sealed record ChangedDocumentData(string FilePath, string Text);

internal sealed record PrepareUpdateRequest(
    string SessionId,
    List<ChangedDocumentData> ChangedDocuments,
    string ArtifactDirectory);

internal sealed record CompleteUpdateRequest(string SessionId, string UpdateId);

internal sealed record EndSessionRequest(string SessionId);

internal sealed record ArtifactData(string Kind, string Path, long SizeBytes, string Sha256);

internal sealed class WorkerSession(
    string sessionId,
    string workspaceRoot,
    IReadOnlyList<string> runtimeCapabilities,
    HotReloadDeltaSession session,
    Guid moduleId)
{
    public string SessionId { get; } = sessionId;

    public string WorkspaceRoot { get; } = workspaceRoot;

    public IReadOnlyList<string> RuntimeCapabilities { get; } = runtimeCapabilities;

    public HotReloadDeltaSession Session { get; } = session;

    public Guid ModuleId { get; } = moduleId;

    public string? PendingUpdateId { get; set; }
}

internal sealed class WorkerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
