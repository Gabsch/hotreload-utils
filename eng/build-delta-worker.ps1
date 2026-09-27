[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "hotreload-delta-worker-v1",
    [string]$UpstreamCommit = "28af8e7016d4b1ad30ed932f15bd56c033402457",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$publishRoot = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $repositoryRoot "artifacts\delta-worker\publish"
} else {
    [IO.Path]::GetFullPath($OutputPath)
}
$forkCommit = (git -C $repositoryRoot rev-parse HEAD).Trim()
$dirtyState = @(git -C $repositoryRoot status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect the Hot Reload delta worker source state."
}
if ($dirtyState.Count -ne 0) {
    throw "Refusing to publish from a dirty worktree because the recorded fork commit would not identify the packaged source exactly."
}
$versionsPath = Join-Path $repositoryRoot "eng\Versions.props"
[xml]$versions = Get-Content -LiteralPath $versionsPath -Raw
$roslynVersion = [string]$versions.Project.PropertyGroup.MicrosoftCodeAnalysisVersion
$msBuildVersion = [string]$versions.Project.PropertyGroup.MicrosoftBuildVersion
if ([string]::IsNullOrWhiteSpace($roslynVersion) -or
    [string]::IsNullOrWhiteSpace($msBuildVersion)) {
    throw "eng/Versions.props must define MicrosoftCodeAnalysisVersion and MicrosoftBuildVersion."
}

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
dotnet publish (Join-Path $repositoryRoot "src\HotReload.DeltaWorker\HotReload.DeltaWorker.csproj") `
    --configuration $Configuration `
    --framework net10.0 `
    --no-self-contained `
    -p:UseAppHost=false `
    --output $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "Hot Reload delta worker publish failed."
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE.txt") `
    -Destination (Join-Path $publishRoot "LICENSE-hotreload-utils.txt")
@"
Hot Reload delta worker $Version
Repository: https://github.com/Gabsch/hotreload-utils
Fork commit: $forkCommit
Upstream repository: https://github.com/dotnet/hotreload-utils
Upstream commit: $UpstreamCommit
Roslyn version: $roslynVersion
MSBuild version: $msBuildVersion
License: MIT
"@ | Set-Content -LiteralPath (Join-Path $publishRoot "PROVENANCE.txt") -Encoding UTF8

Write-Output $publishRoot
