param(
    [switch]$NativeAot,
    [ValidateSet('GitHub', 'Website')]
    [string]$DistributionChannel
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'src'
$project = 'ScreenCaptureTool/ScreenCaptureTool.csproj'
$artifacts = Join-Path $PSScriptRoot 'dist'
$buildOptions = @('-p:Platform=x64', '-p:UseSharedCompilation=false', '-m:1', '--nologo')
$previousOAuthConfig = $env:YuSnipOAuthConfig
if ($DistributionChannel) {
    $buildOptions += "-p:YuSnipDistributionChannel=$DistributionChannel"
    if ($NativeAot) {
        if (-not $env:YuSnipOAuthConfig) {
            $env:YuSnipOAuthConfig = Join-Path $PSScriptRoot '.local-private/google-drive-desktop-oauth.json'
        }
        $buildOptions += '-p:RequireYuSnipOAuth=true'
    }
}

# 从源码目录执行，以便 dotnet 读取随仓库提供的 global.json。
Push-Location -LiteralPath $source
try {
    if ($NativeAot) {
        dotnet publish $project -c Release -r win-x64 @buildOptions -o (Join-Path $artifacts 'win-x64')
    } else {
        dotnet build $project -c Debug @buildOptions -o (Join-Path $artifacts 'debug')
    }
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
} finally {
    $env:YuSnipOAuthConfig = $previousOAuthConfig
    Pop-Location
}
