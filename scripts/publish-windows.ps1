<#
.SYNOPSIS
Windows 版 RealtimeTranslator を自己完結 (self-contained) 形式で publish する。

.DESCRIPTION
.NET ランタイム未導入の Windows でもそのまま起動できる成果物を出力する。
framework-dependent にすると起動時に「.NET Desktop Runtime が必要」ダイアログが出るため、
配布用の既定は自己完結とする。

.EXAMPLE
# 既定の出力先はリポジトリ直下の artifacts/RealtimeTranslator-<runtime>。
pwsh -File scripts/publish-windows.ps1
pwsh -File scripts/publish-windows.ps1 -Runtime win-arm64
pwsh -File scripts/publish-windows.ps1 -Output C:\dist\RealtimeTranslator
pwsh -File scripts/publish-windows.ps1 -Runtime win-x64 -NoRestore
#>
[CmdletBinding()]
param(
    [string]$Output,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'windows/src/RealtimeTranslator.App/RealtimeTranslator.App.csproj'

if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = "artifacts/RealtimeTranslator-$Runtime"
}

# 相対パスは呼び出し元の作業ディレクトリではなくリポジトリ直下へ解決する。
if (-not [System.IO.Path]::IsPathRooted($Output)) {
    $Output = Join-Path $repositoryRoot $Output
}

$publishArgs = @(
    $project,
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--self-contained', 'true',
    '--output', $Output
)
if ($NoRestore) {
    $publishArgs += '--no-restore'
}

dotnet publish @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "publish failed with exit code $LASTEXITCODE"
}

Write-Host "published to $Output (RealtimeTranslator.App.exe)"
