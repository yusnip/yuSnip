param([switch]$CheckOnly)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repository = 'yusnip/yuSnip'
$api = "https://api.github.com/repos/$repository"
$releaseBase = "https://github.com/$repository/releases"
$headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'yuSnip-release' }
$assetNames = @('yuSnip.exe', 'version.json', 'deps.json')

function Get-GitHub($url, $requestHeaders = $headers) {
    Invoke-RestMethod -Uri $url -Headers $requestHeaders -UseBasicParsing -TimeoutSec 30
}

function Get-OptionalGitHub($url) {
    try { Get-GitHub $url } catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { return $null }
        throw
    }
}

function Get-PublishHeaders {
    $token = $env:GH_TOKEN
    if (-not $token) {
        $request = "protocol=https`nhost=github.com`npath=$repository.git`n`n"
        $credential = $request | git -c credential.interactive=false credential fill | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
        if ($credential) { $token = $credential.Substring(9) }
    }
    if (-not $token) { throw '请先登录有公开仓库发布权限的 GitHub 账号，或提供 GH_TOKEN。' }
    $authenticated = $headers.Clone()
    $authenticated.Authorization = "Bearer $token"
    if (-not (Get-GitHub $api $authenticated).permissions.push) {
        throw "当前 GitHub 账号没有 $repository 的发布权限。"
    }
    return $authenticated
}

function Assert-ReleaseAssets($release) {
    foreach ($name in $assetNames) {
        $asset = @($release.assets | Where-Object { $_.name -eq $name -and $_.state -eq 'uploaded' -and $_.size -gt 0 })
        if ($asset.Count -ne 1) { throw "公开 Release $($release.tag_name) 的附件 $name 缺失或不完整。" }
    }
}

function Get-ReleaseOAuthConfig($projectRoot) {
    $path = $env:YuSnipOAuthConfig
    if (-not $path) { $path = Join-Path $projectRoot '.local-private/google-drive-desktop-oauth.json' }
    try {
        $file = Get-Item -LiteralPath $path
        if ($file.PSIsContainer -or $file.Length -gt 64KB) { throw 'Invalid file' }
        $credentials = ([IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json).installed
        if ($credentials.client_id -isnot [string] -or $credentials.client_id -cnotmatch '^[A-Za-z0-9._-]+\.apps\.googleusercontent\.com$' -or
            $credentials.client_secret -isnot [string] -or $credentials.client_secret -cnotmatch '^[A-Za-z0-9_-]+$') {
            throw 'Invalid credentials'
        }
        return $file.FullName
    } catch {
        throw '正式发布需要有效的 Google 桌面 OAuth 配置。请在本机提供 YuSnipOAuthConfig 或 .local-private/google-drive-desktop-oauth.json。'
    }
}

function Build-GitHubPackage($commit, $work, $expectedVersion, $oauthConfig) {
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
    if (-not $studio) { throw '构建公开版需要 Visual Studio 的 x64 C++ 工具链。' }
    $vcvars = Join-Path $studio 'VC/Auxiliary/Build/vcvars64.bat'
    $output = Join-Path $work 'build'
    # 公开仓库保留原有源码目录名，也兼容以后独立迁移为 src。
    $source = Join-Path $root '01_源码'
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { $source = Join-Path $root 'src' }
    Push-Location -LiteralPath $source
    $previousOAuthConfig = $env:YuSnipOAuthConfig
    try {
        $env:YuSnipOAuthConfig = $oauthConfig
        $command = 'call "' + $vcvars + '" && dotnet publish "ScreenCaptureTool/ScreenCaptureTool.csproj" -c Release -r win-x64 -p:Platform=x64 -p:YuSnipDistributionChannel=GitHub -p:RequireYuSnipOAuth=true -p:EmbedNativeForSingleExe=true -p:DebugType=None -p:DebugSymbols=false -p:UseSharedCompilation=false -m:1 --nologo -o "' + $output + '"'
        & cmd.exe /d /c $command | Out-Host
        if ($LASTEXITCODE -ne 0) { throw '公开版 NativeAOT 构建失败。' }
    } finally {
        $env:YuSnipOAuthConfig = $previousOAuthConfig
        Pop-Location
    }
    $exe = Get-Item -LiteralPath (Join-Path $output 'yuSnip.exe')
    if ($exe.VersionInfo.FileVersion -ne "$expectedVersion.0") { throw '公开版 EXE 的文件版本不正确。' }

    $logFile = Join-Path $root '更新记录.txt'
    if (-not (Test-Path -LiteralPath $logFile)) { $logFile = Join-Path $root 'CHANGELOG.md' }
    $log = Get-Content -LiteralPath $logFile -Encoding UTF8 -Raw
    $entries = [regex]::Matches($log, '(?m)^### 更新日志[^\r\n]*')
    if ($entries.Count -eq 0) { throw '公开更新记录缺少发布说明。' }
    $end = if ($entries.Count -gt 1) { $entries[1].Index } else { $log.Length }
    return @{ Exe = $exe.FullName; Notes = $log.Substring($entries[0].Index, $end - $entries[0].Index).Trim() }
}

function Add-ReleaseAsset($release, $path, $contentType, $requestHeaders) {
    $name = [IO.Path]::GetFileName($path)
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $asset = Invoke-RestMethod -Uri "https://uploads.github.com/repos/$repository/releases/$($release.id)/assets?name=$name" `
        -Method Post -Headers $requestHeaders -InFile $path -ContentType $contentType -UseBasicParsing -TimeoutSec 600
    if ($asset.state -ne 'uploaded' -or $asset.digest -ne "sha256:$hash") { throw "公开附件 $name 上传校验失败。" }
    return $hash
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $projectRoot '版本号.txt') -Raw).Trim()
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw '版本号必须为 x.y.z。' }
$tag = "v$version"
$existing = Get-OptionalGitHub "$api/releases/tags/$tag"
$latest = Get-GitHub "$api/releases/latest"
if ($latest.tag_name -match '^v(\d+\.\d+\.\d+)$' -and [version]$Matches[1] -gt [version]$version) {
    throw "公开仓库已有更高版本 $($latest.tag_name)，请核对本地版本号。"
}
$publishHeaders = $null
$draft = $null
$work = $null
try {
    if ($existing) {
        Assert-ReleaseAssets $existing
        Write-Host "公开 Release $tag 已存在，将保留现有附件：$($existing.html_url)"
    } else {
        $publishHeaders = Get-PublishHeaders
        $tagRef = Get-OptionalGitHub "$api/git/ref/tags/$tag"
        $sourceRef = if ($tagRef) { $tag } else { 'main' }
        $commit = (Get-GitHub "$api/commits/$sourceRef").sha
        if ($commit -notmatch '^[a-f0-9]{40}$') { throw '无法确定公开源码提交。' }
        $sourceUrl = "https://raw.githubusercontent.com/$repository/$commit"
        try {
            $versionUrl = $sourceUrl + '/' + [uri]::EscapeDataString('版本号.txt')
            $publicVersion = (Invoke-WebRequest -Uri $versionUrl -UseBasicParsing -TimeoutSec 30).Content.Trim()
        } catch {
            if (-not $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
            $publicVersion = (Invoke-WebRequest -Uri "$sourceUrl/VERSION" -UseBasicParsing -TimeoutSec 30).Content.Trim()
        }
        if ($publicVersion -ne $version) {
            throw "公开源码版本是 $publicVersion，本地是 $version。请先把经过脱敏的新版本同步到公开仓库。"
        }
        $marker = "<!-- yuSnip-public-source:$commit -->"
        $releases = Get-GitHub "$api/releases?per_page=100" $publishHeaders
        $draft = $releases | Where-Object { $_.draft -and $_.tag_name -eq $tag } | Select-Object -First 1
        if ($draft -and ($draft.target_commitish -ne $commit -or -not $draft.body.Contains($marker))) {
            throw "已有 $tag 草稿，请核对其公开源码提交后再发布。"
        }
        Assert-ReleaseAssets $latest
        Write-Host "公开版将从 $repository 的提交 $commit 构建。"
    }
    $needsBuild = -not $existing -and (-not $draft -or @($draft.assets).Count -lt $assetNames.Count)
    if ($needsBuild) {
        $oauthConfig = Get-ReleaseOAuthConfig $projectRoot
    }
    if ($CheckOnly) { return }

    if ($needsBuild) {
        $work = Join-Path ([IO.Path]::GetTempPath()) ('yuSnip-public-' + [Guid]::NewGuid().ToString('N'))
        [void](New-Item -ItemType Directory -Path $work)
        $package = Build-GitHubPackage $commit $work $version $oauthConfig
        Copy-Item -LiteralPath $package.Exe -Destination (Join-Path $projectRoot 'yuSnip.exe') -Force
        $deps = Join-Path $work 'deps.json'
        $publicDeps = $latest.assets | Where-Object name -eq 'deps.json' | Select-Object -First 1
        Invoke-WebRequest -Uri "$releaseBase/download/$($latest.tag_name)/deps.json" -OutFile $deps -UseBasicParsing -TimeoutSec 30
        if ($publicDeps.digest -ne ('sha256:' + (Get-FileHash -LiteralPath $deps -Algorithm SHA256).Hash.ToLowerInvariant())) {
            throw '公开版组件清单校验失败。'
        }
    }

    if (-not $existing -and $work) {
        if (-not $draft) {
            $body = @{ tag_name = $tag; target_commitish = $commit; name = "yuSnip $tag（Windows x64 GitHub 版）"; draft = $true; body = "$($package.Notes)`n`n$marker" }
            $draft = Invoke-RestMethod -Uri "$api/releases" -Method Post -Headers $publishHeaders -Body ([Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json))) -ContentType 'application/json; charset=utf-8' -UseBasicParsing -TimeoutSec 30
        }
        $exeAsset = $draft.assets | Where-Object name -eq 'yuSnip.exe' | Select-Object -First 1
        if ($exeAsset) {
            if ($exeAsset.state -ne 'uploaded' -or $exeAsset.digest -notmatch '^sha256:[a-f0-9]{64}$') { throw '草稿中的 yuSnip.exe 附件不完整。' }
            $exeHash = $exeAsset.digest.Substring(7)
        } else {
            $exeHash = Add-ReleaseAsset $draft $package.Exe 'application/octet-stream' $publishHeaders
        }
        if (-not ($draft.assets | Where-Object name -eq 'deps.json')) {
            [void](Add-ReleaseAsset $draft $deps 'application/json' $publishHeaders)
        }
        if (-not ($draft.assets | Where-Object name -eq 'version.json')) {
            $v = [version]$version
            $manifest = @{ name='yuSnip'; version=$version; build=$v.Major * 10000 + $v.Minor * 100 + $v.Build; channel='github'
                download_url="$releaseBase/download/$tag/yuSnip.exe"; sha256=$exeHash; updated_at=(Get-Date -Format 'yyyy-MM-dd')
                changelog=$package.Notes; force_update=$false }
            $file = Join-Path $work 'version.json'
            [IO.File]::WriteAllText($file, ($manifest | ConvertTo-Json))
            [void](Add-ReleaseAsset $draft $file 'application/json' $publishHeaders)
        }
    }
    if ($draft) {
        $draft = Get-GitHub "$api/releases/$($draft.id)" $publishHeaders
        Assert-ReleaseAssets $draft
    }

    if ($draft) {
        $published = Invoke-RestMethod -Uri "$api/releases/$($draft.id)" -Method Patch -Headers $publishHeaders -Body '{"draft":false,"make_latest":"true"}' -ContentType 'application/json' -UseBasicParsing -TimeoutSec 30
        if ($published.draft -or $published.tag_name -ne $tag) { throw 'GitHub Release 未成功公开，请检查草稿后重试。' }
        Write-Host "公开 Release 已发布：$($published.html_url)"
    }
} finally {
    if ($publishHeaders) { $publishHeaders.Clear() }
    if ($work) {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $resolved = (Resolve-Path -LiteralPath $work -ErrorAction SilentlyContinue).Path
        if ($resolved -and $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolved) -match '^yuSnip-public-[a-f0-9]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
