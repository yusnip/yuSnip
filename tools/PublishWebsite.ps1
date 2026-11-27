param(
    [switch]$CheckOnly,
    [switch]$ForceBinary
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repository = 'yusnip/yuSnip'
$api = "https://api.github.com/repos/$repository"
$headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'yuSnip-release' }

$projectRoot = Split-Path -Parent $PSScriptRoot
$privateDir = Join-Path $projectRoot '.local-private\website-deploy'
$configPath = Join-Path $privateDir 'config.json'
if (-not (Test-Path -LiteralPath $configPath)) {
    throw "缺少官网部署配置：$configPath。请在 .local-private/website-deploy/ 中配置服务器信息与 SSH 私钥。"
}
$cfg = Get-Content -LiteralPath $configPath -Encoding UTF8 -Raw | ConvertFrom-Json
$keyPath = if ([IO.Path]::IsPathRooted($cfg.keyFile)) { $cfg.keyFile } else { Join-Path $privateDir $cfg.keyFile }
if (-not (Test-Path -LiteralPath $keyPath)) {
    throw "缺少 SSH 私钥文件：$keyPath"
}

$sshTarget = "$($cfg.user)@$($cfg.host)"
$sshPort = [string]$cfg.port
$websiteRoot = $cfg.websiteRoot.TrimEnd('/')
$yusnipDir = $cfg.yusnipDir.TrimEnd('/')
$publicBaseUrl = $cfg.publicBaseUrl.TrimEnd('/')

function Invoke-Ssh([string]$remoteCmd) {
    $out = & ssh.exe -i $keyPath -p $sshPort -o StrictHostKeyChecking=no -o ConnectTimeout=15 $sshTarget $remoteCmd 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "SSH 执行失败：$remoteCmd`n$($out -join "`n")"
    }
    return $out
}

function Invoke-ScpUpload([string]$localPath, [string]$remotePath) {
    $out = & scp.exe -i $keyPath -P $sshPort -o StrictHostKeyChecking=no -o ConnectTimeout=15 $localPath "${sshTarget}:${remotePath}" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "SCP 上传失败：$localPath -> $remotePath`n$($out -join "`n")"
    }
}

function Invoke-ScpDownload([string]$remotePath, [string]$localPath) {
    $out = & scp.exe -i $keyPath -P $sshPort -o StrictHostKeyChecking=no -o ConnectTimeout=15 "${sshTarget}:${remotePath}" $localPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "SCP 下载失败：$remotePath -> $localPath`n$($out -join "`n")"
    }
}

function Build-WebsitePackage([string]$commit, [string]$work, [string]$expectedVersion) {
    $archive = Join-Path $work 'public.zip'
    Invoke-WebRequest -Uri "https://codeload.github.com/$repository/zip/$commit" -UseBasicParsing -OutFile $archive -TimeoutSec 300
    Expand-Archive -LiteralPath $archive -DestinationPath $work
    Rename-Item -LiteralPath (Join-Path $work "yuSnip-$commit") -NewName 'src'
    $root = Join-Path $work 'src'
    $versionFile = Join-Path $root '版本号.txt'
    if (-not (Test-Path -LiteralPath $versionFile)) { $versionFile = Join-Path $root 'VERSION' }
    if ((Get-Content -LiteralPath $versionFile -Raw).Trim() -ne $expectedVersion) {
        throw '公开源码压缩包中的版本与发布版本不同。'
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $studio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $studio) { throw '构建官网版需要 Visual Studio 的 x64 C++ 工具链。' }
    $vcvars = Join-Path $studio 'VC/Auxiliary/Build/vcvars64.bat'
    $output = Join-Path $work 'build'
    $source = Join-Path $root '01_源码'
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { $source = Join-Path $root 'src' }
    Push-Location -LiteralPath $source
    try {
        $command = 'call "' + $vcvars + '" && dotnet publish "ScreenCaptureTool/ScreenCaptureTool.csproj" -c Release -r win-x64 -p:Platform=x64 -p:YuSnipDistributionChannel=Website -p:EmbedNativeForSingleExe=true -p:DebugType=None -p:DebugSymbols=false -p:UseSharedCompilation=false -m:1 --nologo -o "' + $output + '"'
        & cmd.exe /d /c $command | Out-Host
        if ($LASTEXITCODE -ne 0) { throw '官网版 NativeAOT 构建失败。' }
    } finally { Pop-Location }

    $exe = Get-Item -LiteralPath (Join-Path $output 'yuSnip.exe')
    if ($exe.VersionInfo.FileVersion -ne "$expectedVersion.0") { throw '官网版 EXE 的文件版本不正确。' }

    $logFile = Join-Path $root '更新记录.txt'
    if (-not (Test-Path -LiteralPath $logFile)) { $logFile = Join-Path $root 'CHANGELOG.md' }
    $log = Get-Content -LiteralPath $logFile -Encoding UTF8 -Raw
    $entries = [regex]::Matches($log, '(?m)^### 更新日志[^\r\n]*')
    if ($entries.Count -eq 0) { throw '更新记录缺少发布说明。' }
    $end = if ($entries.Count -gt 1) { $entries[1].Index } else { $log.Length }
    return @{ Exe = $exe.FullName; Notes = $log.Substring($entries[0].Index, $end - $entries[0].Index).Trim() }
}

$version = (Get-Content -LiteralPath (Join-Path $projectRoot '版本号.txt') -Raw).Trim()
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw '版本号必须为 x.y.z。' }
$tag = "v$version"

$logPath = Join-Path $projectRoot '更新记录.txt'
if (-not (Test-Path -LiteralPath $logPath)) { throw "缺少更新记录：$logPath" }

$pythonCmd = Get-Command python -ErrorAction SilentlyContinue
if (-not $pythonCmd) { $pythonCmd = Get-Command python3 -ErrorAction SilentlyContinue }
if (-not $pythonCmd) { throw '同步官网更新日志需要 Python 3。' }

[void](Invoke-Ssh "echo ok")
$remoteManifest = Invoke-RestMethod -Uri "$publicBaseUrl/yusnip/version.json" -UseBasicParsing -TimeoutSec 15
Write-Host "官网当前线上版本：$($remoteManifest.version)，本地目标版本：$version"

$tagRef = $null
try {
    $tagRef = Invoke-RestMethod -Uri "$api/git/ref/tags/$tag" -Headers $headers -UseBasicParsing -TimeoutSec 30
} catch {
    if (-not $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
}
$sourceRef = if ($tagRef) { $tag } else { 'main' }
$commit = (Invoke-RestMethod -Uri "$api/commits/$sourceRef" -Headers $headers -UseBasicParsing -TimeoutSec 30).sha
if ($commit -notmatch '^[a-f0-9]{40}$') { throw '无法确定公开源码提交。' }

if ($CheckOnly) {
    Write-Host "官网预检通过（将使用提交 $commit 同步官网本体与更新日志）。"
    return
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('yuSnip-website-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $work)
try {
    $needBinary = $ForceBinary -or ($remoteManifest.version -ne $version)
    if ($needBinary) {
        Write-Host "[官网软件本体] 正在构建 Website 渠道 v$version ..."
        $pkg = Build-WebsitePackage $commit $work $version
        $exeHash = (Get-FileHash -LiteralPath $pkg.Exe -Algorithm SHA256).Hash.ToLowerInvariant()
        $v = [version]$version
        $manifest = [ordered]@{
            name         = 'yuSnip'
            version      = $version
            build        = $v.Major * 10000 + $v.Minor * 100 + $v.Build
            channel      = 'website'
            download_url = "$publicBaseUrl/yusnip/yuSnip-$version.exe"
            sha256       = $exeHash
            updated_at   = (Get-Date -Format 'yyyy-MM-dd')
            changelog    = $pkg.Notes
            force_update = $false
        }
        $manifestFile = Join-Path $work 'version.json'
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Depth 4), $utf8NoBom)

        $remoteTmpExe = "/tmp/yuSnip-$version.exe"
        $remoteTmpVer = "/tmp/yusnip-version-$version.json"
        Write-Host "[官网软件本体] 正在上传 yuSnip-$version.exe 与 version.json ..."
        Invoke-ScpUpload $pkg.Exe $remoteTmpExe
        Invoke-ScpUpload $manifestFile $remoteTmpVer

        $deployBinCmd = @(
            "sudo mv -f '$remoteTmpExe' '$yusnipDir/yuSnip-$version.exe'",
            "sudo chmod 644 '$yusnipDir/yuSnip-$version.exe'",
            "sudo chown www-data:www-data '$yusnipDir/yuSnip-$version.exe'",
            "sudo ln -f '$yusnipDir/yuSnip-$version.exe' '$yusnipDir/yuSnip.exe'",
            "sudo mv -f '$remoteTmpVer' '$yusnipDir/version.json'",
            "sudo chmod 644 '$yusnipDir/version.json'",
            "sudo chown www-data:www-data '$yusnipDir/version.json'"
        ) -join ' && '
        [void](Invoke-Ssh $deployBinCmd)

        $verified = Invoke-RestMethod -Uri "$publicBaseUrl/yusnip/version.json" -UseBasicParsing -TimeoutSec 15
        if ($verified.version -ne $version -or $verified.sha256 -ne $exeHash) {
            throw "官网 version.json 校验失败：线上版本=$($verified.version)，预期=$version"
        }
        Write-Host "[官网软件本体] 已更新至 v$version：$($verified.download_url)"
    } else {
        Write-Host "[官网软件本体] 线上已是 v$version，跳过重复编译与上传。"
    }

    Write-Host "[官网更新日志] 正在生成并同步 changelog.html 与 data/changelog.json ..."
    $docsHtml = Join-Path $work 'docs.html'
    $outHtml = Join-Path $work 'changelog.html'
    $outJson = Join-Path $work 'changelog.json'
    Invoke-ScpDownload "$websiteRoot/docs.html" $docsHtml

    $syncScript = Join-Path $PSScriptRoot 'sync_changelog.py'
    & $pythonCmd.Source $syncScript --txt $logPath --docs-html $docsHtml --out-html $outHtml --out-json $outJson
    if ($LASTEXITCODE -ne 0) { throw '生成官网更新日志失败。' }

    $remoteTmpHtml = '/tmp/yusnip-changelog.html'
    $remoteTmpJson = '/tmp/yusnip-changelog.json'
    Invoke-ScpUpload $outHtml $remoteTmpHtml
    Invoke-ScpUpload $outJson $remoteTmpJson

    $deployLogCmd = @(
        "sudo mv -f '$remoteTmpHtml' '$websiteRoot/changelog.html'",
        "sudo chmod 644 '$websiteRoot/changelog.html'",
        "sudo chown www-data:www-data '$websiteRoot/changelog.html'",
        "sudo mv -f '$remoteTmpJson' '$websiteRoot/data/changelog.json'",
        "sudo chmod 644 '$websiteRoot/data/changelog.json'",
        "sudo chown www-data:www-data '$websiteRoot/data/changelog.json'"
    ) -join ' && '
    [void](Invoke-Ssh $deployLogCmd)

    $logResp = Invoke-WebRequest -Uri "$publicBaseUrl/data/changelog.json" -UseBasicParsing -TimeoutSec 15
    if ($logResp.StatusCode -ne 200) { throw "官网 changelog.json 校验失败：HTTP $($logResp.StatusCode)" }
    Write-Host "[官网更新日志] 已同步：$publicBaseUrl/changelog.html"
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolved = (Resolve-Path -LiteralPath $work -ErrorAction SilentlyContinue).Path
    if ($resolved -and $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved) -match '^yuSnip-website-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
