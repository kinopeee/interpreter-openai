<#
.SYNOPSIS
Windows 版 RealtimeTranslator を自己完結 (self-contained) 形式で publish する。

.DESCRIPTION
.NET ランタイム未導入の Windows でもそのまま起動できる成果物を出力する。
framework-dependent にすると起動時に「.NET Desktop Runtime が必要」ダイアログが出るため、
配布用の既定は自己完結とする。

.EXAMPLE
pwsh -File scripts/publish-windows.ps1
pwsh -File scripts/publish-windows.ps1 -Output C:\dist\RealtimeTranslator
#>
[CmdletBinding()]
param(
    [string]$Output = 'artifacts/RealtimeTranslator-win-x64',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'windows/src/RealtimeTranslator.App/RealtimeTranslator.App.csproj'

dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $Output

if ($LASTEXITCODE -ne 0) {
    throw "publish failed with exit code $LASTEXITCODE"
}

Write-Host "published to $Output (RealtimeTranslator.App.exe)"
