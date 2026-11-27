param(
    [switch]$CheckOnly,
    [switch]$ForceWebsiteBinary
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

Write-Host '=== [1/2] GitHub 渠道发布 ==='
$ghParams = @{}
if ($CheckOnly) { $ghParams.CheckOnly = $true }
& (Join-Path $PSScriptRoot 'PublishGitHub.ps1') @ghParams

Write-Host ''
Write-Host '=== [2/2] 官网渠道发布（软件本体 + 更新日志）==='
$webParams = @{}
if ($CheckOnly) { $webParams.CheckOnly = $true }
if ($ForceWebsiteBinary) { $webParams.ForceBinary = $true }
& (Join-Path $PSScriptRoot 'PublishWebsite.ps1') @webParams
