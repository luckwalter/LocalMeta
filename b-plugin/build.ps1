# LocalMeta 插件编译 + 部署脚本
#
#   powershell -ExecutionPolicy Bypass -File build.ps1              # 编译并装进 Jellyfin 插件目录
#   powershell -ExecutionPolicy Bypass -File build.ps1 -PackOnly    # 只编译不部署
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Uninstall  # 卸载插件
#
# 前置：.NET 9 SDK。本机实测装在 <USERPROFILE>\.dotnet\dotnet.exe（9.0.318）。
# 注意本机 C:\Program Files\dotnet 是纯 runtime、没有 sdk 目录，且在 PATH 里排前面，
# 直接敲 `dotnet` 会命中那个空壳。脚本会自动挑真正带 SDK 的那个。
#
# 本文件必须存为 UTF-8 with BOM：PowerShell 5.1 默认按 GBK 解码 .ps1，
# 中文注释会把整行解析带崩（症状是注册/创建对象报"参数为 Null"）。

param(
    [switch]$PackOnly,
    [switch]$Uninstall,
    [string]$PluginDir = "C:\Jellyfin\Data\plugins\Jellyfin.Plugin.LocalMeta"
)

$ErrorActionPreference = "Stop"
$Proj = Join-Path $PSScriptRoot "Jellyfin.Plugin.LocalMeta\Jellyfin.Plugin.LocalMeta.csproj"
$Out  = Join-Path $PSScriptRoot "Jellyfin.Plugin.LocalMeta\bin\Release\net9.0\publish"

function Say($m) { Write-Host $m }

if ($Uninstall) {
    if (Test-Path $PluginDir) {
        Remove-Item -Path $PluginDir -Recurse -Force
        Say "已卸载插件目录: $PluginDir"
    } else {
        Say "插件目录不存在，无需卸载: $PluginDir"
    }
    Say "配置保留在 Data\plugins\configurations\Jellyfin.Plugin.LocalMeta.xml（如需彻底清除请手工删）"
    Say "重启 Jellyfin 生效"
    exit 0
}

if (-not (Test-Path $Proj)) { Say "找不到项目文件: $Proj"; exit 2 }

# 挑"真带 SDK 的" dotnet，不能只认 PATH
$candidates = @("$env:USERPROFILE\.dotnet\dotnet.exe", "C:\Program Files\dotnet\dotnet.exe")
$dotnet = $null
foreach ($c in $candidates) {
    if (-not (Test-Path $c)) { continue }
    $sdks = & $c --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and $sdks) { $dotnet = $c; break }
}
if (-not $dotnet) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source }
}
if (-not $dotnet -or -not (Test-Path $dotnet)) {
    Say "没找到带 .NET 9 SDK 的 dotnet。"
    Say "装法：https://dot.net/v1/dotnet-install.ps1 -Channel 9.0 -InstallDir `"$env:USERPROFILE\.dotnet`""
    Say "或直接改用 A 阶段的 localmeta.py（纯 Python，无需 SDK）。"
    exit 3
}

Say "== 编译 =="
Say "dotnet: $dotnet"
Say "sdk    : " + (& $dotnet --list-sdks 2>$null | Select-Object -First 1)
& $dotnet publish $Proj -c Release -o $Out
if ($LASTEXITCODE -ne 0) { Say "编译失败，看上面报错"; exit $LASTEXITCODE }

$dll = Join-Path $Out "Jellyfin.Plugin.LocalMeta.dll"
if (-not (Test-Path $dll)) {
    Say "产物主程序集不存在: $dll"
    Say "（csproj 的 RemoveHostAssemblies 目标误删自身时会触发这个提示）"
    exit 4
}

# 关键校验：宿主程序集绝不能进插件目录。覆盖会导致程序集版本冲突甚至 Jellyfin 起不来。
$hostDlls = @(Get-ChildItem -Path $Out -Filter *.dll -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -like "MediaBrowser.*" -or $_.Name -like "Jellyfin.*" } |
               Where-Object { $_.Name -ne "Jellyfin.Plugin.LocalMeta.dll" })
if ($hostDlls.Count -gt 0) {
    Say "中止：产物里混进了 Jellyfin 宿主程序集，部署会出事："
    $hostDlls | ForEach-Object { Say "  - $($_.Name)" }
    exit 5
}

if ($PackOnly) { Say "仅编译完成: $Out"; exit 0 }

Say "== 部署 =="
if (Test-Path $PluginDir) {
    $bak = "$PluginDir.bak_$(Get-Date -Format yyyyMMdd_HHmmss)"
    Say "已有插件目录，先备份到: $bak"
    Move-Item -Path $PluginDir -Destination $bak
}
New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null
Copy-Item -Path (Join-Path $Out "*") -Destination $PluginDir -Recurse -Force
Say "已部署: $PluginDir"

$n = (Get-ChildItem -Path $PluginDir -Recurse -File).Count
Say "共 $n 个文件"
Say ""
Say "下一步："
Say "  1. 重启 Jellyfin（换的是 dll，热加载不生效）"
Say "  2. 后台 -> 插件，确认 LocalMeta 出现且可点进配置页"
Say "  3. 在配置页填 javboss.db 路径与头像源目录（Gfriends 导出的 avatars 目录）"
Say "  4. 计划任务页跑一次「LocalMeta 人物资料补齐」，看进度与日志"
Say "  5. 控制台日志搜 [LocalMeta] 定位输出"
Say ""
Say "回滚：把上面 .bak_ 时间戳目录改回原名，或 build.ps1 -Uninstall"

