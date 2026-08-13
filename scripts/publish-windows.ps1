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
pwsh -File scripts/publish-windows.ps1 -Runtime win-x64 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [string]$Output,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$Version,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'windows/src/RealtimeTranslator.App/RealtimeTranslator.App.csproj'

if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = "artifacts/RealtimeTranslator-$Runtime"
}

# Windows PowerShell 5.1 (.NET Framework) には IsPathFullyQualified がない。
# IsPathRooted は C:dist や \dist も true にするので、完全修飾だけを絶対パスとして扱う。
$isUnc = $Output.StartsWith('\\')
$isDriveAbsolute = $Output -match '^[A-Za-z]:[\\/]'
if (-not ($isUnc -or $isDriveAbsolute)) {
    if ($Output -match '^[A-Za-z]:' -or $Output.StartsWith('\') -or $Output.StartsWith('/')) {
        throw "-Output must be a fully qualified path (e.g. C:\dist\...) or a repository-relative path. Refusing: $Output"
    }
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

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9]+)*$') {
        throw "Version must look like 0.1.0 (no path separators): $Version"
    }
    $publishArgs += "-p:Version=$Version"
    $publishArgs += "-p:InformationalVersion=$Version"
}

dotnet publish @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "publish failed with exit code $LASTEXITCODE"
}

Write-Host "published to $Output (RealtimeTranslator.App.exe)"
