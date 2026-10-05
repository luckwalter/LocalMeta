# LocalMeta 插件部署脚本
#
#   powershell -ExecutionPolicy Bypass -File deploy.ps1              # 部署（会先停 Jellyfin）
#   powershell -ExecutionPolicy Bypass -File deploy.ps1 -KeepRunning # 部署后不重启（调试用）
#   powershell -ExecutionPolicy Bypass -File deploy.ps1 -Uninstall   # 卸载
#
# 本文件必须存为 UTF-8 with BOM（PowerShell 5.1 按 GBK 解码会崩）。
#
# ============================ 部署规则（踩坑总结）============================
#
# 【规则 1】Jellyfin 会【递归】扫描插件目录下每一个 .dll，尝试当程序集加载。
#          任何非托管文件都会抛 BadImageFormatException，
#          进而在 meta.json 里把整个插件标成 status=Malfunctioned 并跳过加载。
#
#          实测踩到两次：
#            - 插件根目录放 e_sqlite3.dll（我以为这样更保险）→ 直接崩
#            - runtimes/win-arm/native/e_sqlite3.dll 等一堆 RID 原生库 → 直接崩
#          所以 runtimes/ 整个目录都不能带，原生库由 SQLitePCLRaw 自行解析。
#
# 【规则 2】宿主程序集（MediaBrowser.* / Jellyfin.*）绝不能进插件目录。
#          覆盖会导致程序集版本冲突甚至 Jellyfin 起不来。
#          csproj 的 RemoveHostAssemblies 目标已剔除，部署时再校验一次。
#
# 【规则 3】Microsoft.Extensions.* / EntityFrameworkCore / Polly / Newtonsoft
#          这些宿主基础设施，插件也不该带副本。实测 8 个版本与宿主不一致
#          （如 Microsoft.Data.Sqlite 宿主 9.0.1125 vs 插件 9.0.24），
#          留着可能与宿主抢程序集。
#
# 【规则 4】加载失败后 Jellyfin 会记住，meta.json 里写 status=Malfunctioned。
#          即使修好文件也不加载，必须删掉 meta.json 让它重新扫描。
#          注意 meta.json 的 name 是按【目录名】记的，改目录名要一并删。
#
# 【规则 5】改 dll 必须重启 Jellyfin（不是热加载）。
#          停的时候要连 Jellyfin.Windows.Tray.exe 一起停，
#          否则 Tray 会把 jellyfin.exe 再拉起来。
#          启动用 Tray（长期驻留），它实际路径在 jellyfin-windows-tray\ 子目录。
# ============================================================================

param(
    [switch]$Uninstall,
    [switch]$KeepRunning,
    [string]$PluginDir = "C:\Jellyfin\Data\plugins\Jellyfin.Plugin.LocalMeta",
    [string]$PublishDir = ""
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$copyPyDir = $Root
if (-not $PublishDir) {
    $PublishDir = Join-Path $Root "Jellyfin.Plugin.LocalMeta\bin\Release\net9.0\publish"
}
$JellyfinDir = "C:\Jellyfin"
$JellyfinExe = Join-Path $JellyfinDir "jellyfin.exe"
$TrayExe = Join-Path $JellyfinDir "jellyfin-windows-tray\Jellyfin.Windows.Tray.exe"

function Say($m) { Write-Host $m }

# 宿主已提供的基础设施，插件带副本会引发版本冲突（规则 2、3）
$HostProvided = @(
    "MediaBrowser.*.dll", "Jellyfin.*.dll",
    "Microsoft.Extensions.*.dll",
    "Microsoft.EntityFrameworkCore*.dll",
    "Polly*.dll", "Newtonsoft.Json.dll",
    "BitFaster.Caching.dll", "Diacritics.dll", "Emby.Naming.dll",
    "ICU4N*.dll", "J2N.dll", "NEbml.Core.dll"
)

function Find-Python {
    # 必须跳过 Microsoft\WindowsApps\python.exe：那是应用商店的【占位符】，
    # 在非交互会话里执行是空壳（什么都不做、不报错、退出码 0）。
    # 实测踩过：Get-Command python 第一个命中它，导致后续步骤静默无输出。
    $skip = @(
        "$env:LOCALAPPDATA\Microsoft\WindowsApps",
        "$env:APPDATA\Microsoft\WindowsApps"
    )
    foreach ($name in @("python", "python3", "py")) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if (-not $cmd) { continue }
        $src = $cmd.Source
        if (-not $src) { continue }
        $bad = $false
        foreach ($s in $skip) { if ($src -like "$s*") { $bad = $true } }
        if ($bad) { continue }
        # 实际跑一下确认能用（占位符返回非 0 或无输出）
        $v = & $src -c "import sys; print(sys.version_info[0])" 2>$null
        if ($LASTEXITCODE -eq 0 -and $v) { return $src }
    }
    foreach ($c in @("$env:USERPROFILE\.workbuddy\binaries\python\envs\default\Scripts\python.exe",
                     "$env:USERPROFILE\.workbuddy\binaries\python\versions\3.13.12\python.exe",
                     "$PSScriptRoot\..\.venv\Scripts\python.exe")) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

function Stop-Jellyfin {
    $procs = Get-Process jellyfin, Jellyfin.Windows.Tray -ErrorAction SilentlyContinue
    if (-not $procs) { Say "Jellyfin 未在运行"; return }
    foreach ($p in $procs) {
        Say "停止 $($p.Name) PID $($p.Id)"
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 3
    $left = Get-Process jellyfin, Jellyfin.Windows.Tray -ErrorAction SilentlyContinue
    if ($left) { Say "警告：仍有残留进程 $(($left.Name) -join ',')" }
    else { Say "已全部停止" }
}

function Start-Jellyfin {
    if (-not (Test-Path $TrayExe)) {
        Say "找不到 Tray: $TrayExe"
        Say "改为直接启动 jellyfin.exe（注意：可能随会话结束被回收）"
        Start-Process -FilePath $JellyfinExe -WorkingDirectory $JellyfinDir | Out-Null
        return
    }
    # 用 Tray 启动，jellyfin.exe 才会独立驻留
    Start-Process -FilePath $TrayExe -WorkingDirectory (Split-Path $TrayExe) | Out-Null
    Say "已通过 Tray 启动，等待就绪..."
    for ($i = 5; $i -le 60; $i += 5) {
        Start-Sleep -Seconds 5
        try {
            $r = Invoke-WebRequest -Uri "http://127.0.0.1:8096/System/Info/Public" `
                 -UseBasicParsing -TimeoutSec 5
            Say "就绪：HTTP $($r.StatusCode)（${i}s）"
            return
        } catch { }
    }
    Say "60 秒内没响应，去看日志：$JellyfinDir\Data\log\"
}

if ($Uninstall) {
    Stop-Jellyfin
    if (Test-Path $PluginDir) {
        # 移到 plugins 树之外：Jellyfin 会递归扫描 plugins\ 下所有 dll（规则 1），
        # 留个 .disabled_ 目录在里面会让原生库被当程序集加载，服务起不来。
        $bakDir = Join-Path (Split-Path $PluginDir -Parent | Split-Path -Parent) "LocalMeta_backup"
        New-Item -ItemType Directory -Force -Path $bakDir | Out-Null
        $bak = Join-Path $bakDir "Jellyfin.Plugin.LocalMeta.disabled_$(Get-Date -Format yyyyMMdd_HHmmss)"
        Move-Item $PluginDir $bak -Force
        Say "插件目录已移到: $bak"
        Say "回滚：把该目录名改回 $PluginDir 再重启 Jellyfin"
    } else {
        Say "插件目录不存在"
    }
    $cfg = Join-Path $JellyfinDir "Data\plugins\configurations\Jellyfin.Plugin.LocalMeta.xml"
    if (Test-Path $cfg) { Say "配置仍在: $cfg" }
    if (-not $KeepRunning) { Start-Jellyfin }
    exit 0
}

if (-not (Test-Path (Join-Path $PublishDir "Jellyfin.Plugin.LocalMeta.dll"))) {
    Say "找不到编译产物: $PublishDir"
    Say "先跑 build.ps1 -PackOnly"
    exit 2
}

Stop-Jellyfin

Say "== 备份旧目录 =="
# 不直接删：插件目录可能正被占用，且删错了无法还原。
# 但备份目录必须放在 plugins 树【之外】—— Jellyfin 会递归扫描
# plugins\ 下所有子目录里的 dll（规则 1），留在这里会让服务起不来。
$bak = $null
if (Test-Path $PluginDir) {
    $bakDir = Join-Path (Split-Path $PluginDir -Parent | Split-Path -Parent) "LocalMeta_backup"
    New-Item -ItemType Directory -Force -Path $bakDir | Out-Null
    $bak = Join-Path $bakDir "Jellyfin.Plugin.LocalMeta_$(Get-Date -Format yyyyMMdd_HHmmss)"
    Move-Item $PluginDir $bak -Force
    Say "  旧目录已移到: $bak"
    Say "  （必须在 plugins\\ 之外，否则 Jellyfin 会扫描到里面的 dll）"
} else {
    New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null
    Say "  新建目录"
}

Say "== 复制文件（交给 Python）==="
# 为什么不用 PowerShell 的 Copy-Item：
# 实测在受限环境下它会「报成功但文件没落地」
# —— 命令返回 0、目录却建不出来，后面 Test-Path 才发现主程序集不存在，极难查。
# 改用 Python 逐文件复制 + 逐个校验大小，结果写进 deploy_copy.txt 再读回来。
$Py = Find-Python
if (-not $Py) { Say "找不到 Python，无法复制文件"; exit 7 }
$copyPy = Join-Path $copyPyDir "copy_files.py"
if (-not (Test-Path $copyPy)) { Say "缺少辅助脚本: $copyPy"; exit 7 }

$copyOut = Join-Path $Root "deploy_copy.txt"
Say "  python: $Py"
Say "  脚本  : $copyPy"

# 把 python 的 stdout/stderr 单独落盘：出错时才能看到真实原因
# （之前直接输出到管道，失败信息被截断，只剩半句）
$pyErr = Join-Path $Root "copy_stderr.txt"
$pyOut = Join-Path $Root "copy_stdout.txt"
$null = & $Py $copyPy --publish $PublishDir --dest $PluginDir --log $copyOut 1> $pyOut 2> $pyErr
$rc = $LASTEXITCODE

if ($rc -ne 0) {
    Say "复制失败（rc=$rc）"
    if (Test-Path $pyErr) {
        $errTxt = Get-Content $pyErr -Raw
        if ($errTxt.Trim()) {
            Say "  python stderr:"
            $errTxt.Trim() -split "`n" | Select-Object -First 12 | ForEach-Object { Say "    $_" }
        }
    }
    if (Test-Path $copyOut) { Get-Content $copyOut | ForEach-Object { Say "  $_" } }
    exit $rc
}
if (Test-Path $copyOut) { Get-Content $copyOut | ForEach-Object { Say $_ } }

Say "== 部署后校验 =="
$all = @(Get-ChildItem $PluginDir -Recurse -File)
Say "  文件总数: $($all.Count)"

$subdirs = @(Get-ChildItem $PluginDir -Directory)
if ($subdirs.Count -gt 0) {
    Say "中止：插件目录存在子目录，Jellyfin 会递归扫描其中的 dll（规则 1）："
    $subdirs | ForEach-Object { Say "  - $($_.Name)/" }
    exit 3
}

$nonManaged = @($all | Where-Object {
    $_.Extension -eq ".dll" -and $_.Name -notin @(
        "Jellyfin.Plugin.LocalMeta.dll", "Microsoft.Data.Sqlite.dll",
        "SQLitePCLRaw.core.dll", "SQLitePCLRaw.batteries_v2.dll",
        "SQLitePCLRaw.provider.e_sqlite3.dll")
})
if ($nonManaged.Count -gt 0) {
    Say "中止：以下 dll 会被 Jellyfin 当程序集加载，可能导致 BadImageFormat："
    $nonManaged | ForEach-Object { Say "  - $($_.Name)" }
    exit 4
}

if (-not (Test-Path (Join-Path $PluginDir "Jellyfin.Plugin.LocalMeta.dll"))) {
    Say "中止：主程序集不存在"
    exit 5
}
Say "  校验通过 ✓"

# 规则 4：清掉可能存在的 Malfunctioned 标记
$meta = Join-Path $PluginDir "meta.json"
if (Test-Path $meta) {
    Remove-Item $meta -Force
    Say "  已删除旧 meta.json（清除 Malfunctioned 标记）"
}

if ($KeepRunning) {
    Say ""
    Say "已部署但未启动（-KeepRunning）。手动启动后验证："
    Say "  Select-String -Path '$JellyfinDir\Data\log\*.log' -Pattern 'LocalMeta'"
} else {
    Start-Jellyfin
    Say ""
    Say "下一步："
    Say "  1. 后台 → 插件，确认 LocalMeta 显示 Active"
    Say "  2. 点进 LocalMeta 配置页，填两个数据源路径（见下）"
    Say "  3. 控制台日志搜 [LocalMeta] 看载入结果"
    Say ""
    Say "要填的路径（按本机实际位置）："
    Say "  头像源目录      : C:\Users\<你>\WorkBuddy\<日期>\gf"
    Say "  资料库(javboss) : C:\Users\<你>\WorkBuddy\<日期>\gf\javboss.db"
}







