# Vision Hot Reload Worker

This fork is pinned to upstream `dotnet/hotreload-utils` commit
`28af8e7016d4b1ad30ed932f15bd56c033402457`.

The Vision changes add a small session-oriented API and a JSON-over-stdio worker
around the official Roslyn Hot Reload generator. The worker keeps preview Roslyn
assemblies out of Vision's MCP process and preserves explicit prepare, commit,
discard, and end-session behavior.

Protocol v1 supports existing C# document edits in one emitting project. It emits
metadata, IL, and portable PDB deltas. Until Roslyn's ExternalAccess wrapper
exposes sequence-point line updates, edits that change line counts or `#line`
mappings are classified as restart-required.

The original CLI and generator behavior remain available. Vision does not treat
the experimental CLI as its production boundary.

## Build

```powershell
dotnet test tests\Vision.HotReload.Generator.Tests\Vision.HotReload.Generator.Tests.csproj
.\eng\build-vision-worker.ps1
```

The build script writes a complete framework-dependent worker archive under
`artifacts\vision-worker` with the MIT license and provenance record included.
