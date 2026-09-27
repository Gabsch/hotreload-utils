# Hot Reload Delta Worker

This fork is pinned to upstream `dotnet/hotreload-utils` commit
`28af8e7016d4b1ad30ed932f15bd56c033402457`.

The fork changes add a small session-oriented API and a JSON-over-stdio worker
around the official Roslyn Hot Reload generator. The worker keeps preview Roslyn
assemblies out of the consuming process and preserves explicit prepare, commit,
discard, and end-session behavior.

Protocol v2 supports existing C# and Razor document edits in one emitting
project. It emits metadata, IL, portable PDB, and exact `.dlines`
sequence-point-correlation artifacts. Line-moving edits are accepted only when
the worker can prove a complete, unambiguous mapping from committed and updated
source/PDB evidence. Ambiguous mappings, `#line` directives, and unsupported
document changes are classified as restart-required before a delta is exposed.
Line-moving Razor updates require restart when generated sequence points cannot
be tied to Razor syntax identity. Updates that change the method-definition
table also require restart so a full-PDB baseline can never drift from the
runtime's EnC method tokens.

The original CLI and generator behavior remain available. The worker does not treat
the experimental CLI as its production boundary.

Payload publishing requires a clean worktree so `PROVENANCE.txt` identifies the
exact source commit used to build the archive.

## Build

```powershell
dotnet test tests\HotReload.DeltaGenerator.Tests\HotReload.DeltaGenerator.Tests.csproj
.\eng\build-delta-worker.ps1
```

The build script writes a complete framework-dependent, DLL-only worker payload
under `artifacts\delta-worker` with the MIT license and provenance record included.
