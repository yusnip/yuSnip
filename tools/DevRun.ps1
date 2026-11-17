$ErrorActionPreference = 'Stop'

# 定位仓库根目录及源码。
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
$srcRoot = Join-Path $root 'src'

$proj = Join-Path $srcRoot 'ScreenCaptureTool\ScreenCaptureTool.csproj'
if (-not (Test-Path -LiteralPath $proj)) { Write-Host '找不到项目文件。'; exit 1 }

Write-Host '==== yuSnip 快速开发运行 (JIT 增量构建) ===='
Write-Host ('源码目录: ' + $srcRoot)

# 1. 停止正在运行的 yuSnip / ScreenCaptureTool（否则 exe 被占用，编译无法覆盖输出）
$anyStopped = $false
$runningProcesses = @(Get-Process -Name 'yuSnip*', 'ScreenCaptureTool*' -ErrorAction SilentlyContinue)
foreach ($p in $runningProcesses) {
    try {
        Stop-Process -Id $p.Id -Force -ErrorAction Stop
        try { [void]$p.WaitForExit(3000) } catch { }
        $anyStopped = $true
    } catch {
        $alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
        if ($alive) {
            Write-Host "检测到 $($p.ProcessName) (PID=$($p.Id)) 正在运行且无法直接终止（可能为管理员权限），尝试提权关闭..."
            try {
                $proc = Start-Process -FilePath 'taskkill.exe' -ArgumentList "/F /PID $($p.Id)" -Verb RunAs -Wait -PassThru -WindowStyle Hidden
                Start-Sleep -Milliseconds 500
            } catch { }

            $alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
            if ($alive) {
                # 普通权限关不掉管理员实例。这里不能直接报错退出（旧行为）：此刻连构建都还没跑，
                # 用户只会看到误导性的「Build/Run FAILED!」，并以为代码改动没生效——实际是产物压根没更新。
                # 改为以管理员身份重新拉起本脚本，让第 1 步能在管理员权限下关掉它。
                Write-Host '该实例以管理员权限运行，需要管理员权限才能关闭，正在以管理员身份重新启动本脚本...'
                $selfPath = $MyInvocation.MyCommand.Path
                $relaunched = $null
                try {
                    $relaunched = Start-Process -FilePath 'powershell.exe' `
                        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $selfPath + '"')) `
                        -Verb RunAs -PassThru -ErrorAction Stop
                } catch {
                    $relaunched = $null
                }

                if (-not $relaunched) {
                    Write-Host '提权被取消。请手动退出托盘里的 yuSnip 后重试，或右键「以管理员身份运行」开发运行.bat。'
                    exit 1
                }

                # 提权窗口已接管后续流程：本进程正常退出，避免 bat 弹出误导性的 FAILED 横幅
                exit 0
            }
            $anyStopped = $true
        }
    }
}
if ($anyStopped) { Write-Host '已停止旧实例。' }

# 2. 增量构建：与 sln / csproj 保持一致采用 x64 平台
Write-Host '增量构建中...'
$out = Join-Path $root 'dist/dev'
Push-Location $srcRoot
try {
    & dotnet build $proj -c Debug -p:Platform=x64 --nologo -o $out
    if ($LASTEXITCODE -ne 0) { throw '构建失败，见上方错误信息。' }
} finally {
    Pop-Location
}

# 3. 更新开发版快捷方式。正式编译清理源码缓存后，此目录仍可使用。
$exe = Join-Path $out 'yuSnip.exe'

if (-not (Test-Path -LiteralPath $exe)) { Write-Host '未找到编译输出 yuSnip.exe。'; exit 1 }

function Save-DevelopmentShortcut($projectRoot, $target) {
    $linkPath = Join-Path $projectRoot 'yuSnip 开发版.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($linkPath)
    $link.TargetPath = $target
    $link.WorkingDirectory = Split-Path -Parent $target
    $link.Description = 'yuSnip Debug 开发版'
    $link.Save()
    Write-Host ('开发版快捷方式: ' + $linkPath)
}

Save-DevelopmentShortcut $root $exe

# 4. 启动并检测健康状态
Write-Host ('启动: ' + $exe)
$proc = Start-Process -FilePath $exe -WorkingDirectory $out -PassThru
Start-Sleep -Milliseconds 1200

if ($proc.HasExited) {
    # 本次启动的进程已退出，但"退出 ≠ 崩溃"，必须先排除两种正常让位，否则会把好端端的启动误报成崩溃：
    #   ① 提权接力（最常见）：设置里开着「管理员模式」而本次是以普通权限启动时，程序会拉起一个带
    #      -admin-relay 的高权限实例，然后自己 return 0 立即退出让位（见 Program.cs）。
    #   ② 已有实例：单实例锁被既有实例占着，程序 return 2 退出，命令行已转发给那个实例。
    # 判据统一成"退出之后是否仍有 yuSnip 在运行"：有 = 正常接管（实例已在托盘），没有才是真崩溃。
    # 留最多 5 秒轮询：UAC 确认框尚未点掉时，接力实例可能还没起来。
    $survivors = @()
    for ($i = 0; $i -lt 10 -and $survivors.Count -eq 0; $i++) {
        $survivors = @(Get-Process -Name 'yuSnip*', 'ScreenCaptureTool*' -ErrorAction SilentlyContinue)
        if ($survivors.Count -eq 0) { Start-Sleep -Milliseconds 500 }
    }

    if ($survivors.Count -gt 0 -and ($proc.ExitCode -eq 0 -or $proc.ExitCode -eq 2)) {
        $survivorIds = ($survivors | ForEach-Object { $_.Id }) -join ','
        if ($proc.ExitCode -eq 2) {
            Write-Host ('启动成功（已有实例在运行，命令行已转发，未重复启动。PID=' + $survivorIds + '）')
            Write-Host '注意：第 1 步未能停掉它（多半是管理员权限的实例），当前运行的可能仍是旧构建。'
            Write-Host '     请先手动退出托盘里的 yuSnip，再跑一次本脚本来加载新代码。'
        } else {
            Write-Host ('启动成功（本进程已让位给高权限实例，实例运行中。PID=' + $survivorIds + '）')
        }
        exit 0
    }

    Write-Host ''
    Write-Host '========================================'
    Write-Host ' 错误：yuSnip 启动后立即异常退出！'
    Write-Host " 退出码: $($proc.ExitCode)"

    $logFile = Join-Path $root 'log\app.log'
    if (Test-Path -LiteralPath $logFile) {
        Write-Host '--- 最近日志异常 (log/app.log) ---'
        Get-Content -LiteralPath $logFile -Tail 20 | ForEach-Object { Write-Host "  $_" }
        Write-Host '-----------------------------------'
    }
    Write-Host '========================================'
    exit 1
}

Write-Host '启动成功（进程运行中）。'
