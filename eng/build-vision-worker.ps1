[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "vision-hotreload-worker-v1",
    [string]$UpstreamCommit = "28af8e7016d4b1ad30ed932f15bd56c033402457"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputRoot = Join-Path $repositoryRoot "artifacts\vision-worker"
$publishRoot = Join-Path $outputRoot "publish"
$archivePath = Join-Path $outputRoot "vision-hotreload-worker-net10.0.zip"
$forkCommit = (git -C $repositoryRoot rev-parse HEAD).Trim()

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
dotnet publish (Join-Path $repositoryRoot "src\Vision.HotReload.Worker\Vision.HotReload.Worker.csproj") `
    --configuration $Configuration `
    --framework net10.0 `
    --no-self-contained `
    --output $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "Vision Hot Reload worker publish failed."
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE.txt") `
    -Destination (Join-Path $publishRoot "LICENSE-hotreload-utils.txt")
@"
Vision Hot Reload worker $Version
Repository: https://github.com/Gabsch/hotreload-utils
Fork commit: $forkCommit
Upstream repository: https://github.com/dotnet/hotreload-utils
Upstream commit: $UpstreamCommit
Roslyn version: 5.6.0-2.26178.1
MSBuild version: 17.11.48
License: MIT
"@ | Set-Content -LiteralPath (Join-Path $publishRoot "VISION-PROVENANCE.txt") -Encoding UTF8

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $archivePath
Write-Output $archivePath
