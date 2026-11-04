param(
    [switch]$NativeAot
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '01_源码'
$project = 'ScreenCaptureTool/ScreenCaptureTool.csproj'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$buildOptions = @('-p:Platform=x64', '-p:UseSharedCompilation=false', '-m:1', '--nologo')

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
    Pop-Location
}
