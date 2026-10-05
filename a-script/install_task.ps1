# LocalMeta 计划任务注册 / 卸载脚本
#
#   powershell -ExecutionPolicy Bypass -File install_task.ps1             # 注册（重复执行=重建）
#   powershell -ExecutionPolicy Bypass -File install_task.ps1 -Uninstall  # 卸载
#   powershell -ExecutionPolicy Bypass -File install_task.ps1 -Hour 4 -Minute 30
#
# 改完 config.json 重跑注册命令即可；任务始终指向同一份脚本文件，不复制。
# 当前终端执行策略若是 Restricted，脚本文件会被拒绝加载，用 -ExecutionPolicy Bypass 调用。

param(
    [switch]$Uninstall,
    [string]$TaskName = "LocalMeta-ActorRefill",
    [int]$Hour = 3,
    [int]$Minute = 10,
    [string]$PythonPath = ""
)

$LogPath = Join-Path $PSScriptRoot "logs\task_install.txt"
function Say($m) {
    Write-Host $m
    Add-Content -Path $LogPath -Value $m -Encoding UTF8
}

# 定位 Python：优先用传入值，其次 PATH，最后扫常见位置（含 venv）。
# 不写死绝对路径，否则换机器就找不到。
function Find-Python {
    if ($PythonPath) {
        if (Test-Path $PythonPath) { return $PythonPath }
        Say "指定的 Python 不存在: $PythonPath"
        exit 2
    }

    $cmd = Get-Command python -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $cmd = Get-Command python3 -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        "$env:USERPROFILE\.workbuddy\binaries\python\envs\default\Scripts\python.exe",
        "$PSScriptRoot\..\.venv\Scripts\python.exe",
        "$PSScriptRoot\.venv\Scripts\python.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

$Py     = Find-Python
$Dir    = $PSScriptRoot
$Script = Join-Path $Dir "localmeta.py"

if (-not $Py) {
    Say "找不到 Python。用 -PythonPath 指定，或先建 venv：python -m venv .venv"
    exit 2
}
if (-not (Test-Path $Script)){ Say "找不到脚本: $Script"; exit 2 }

New-Item -ItemType Directory -Force -Path (Join-Path $Dir "logs") | Out-Null
Say "=== $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==="

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Say "已卸载计划任务: $TaskName"
    exit 0
}

# 已存在就先删，保证参数改动能生效
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

$at = [datetime]::ParseExact(("{0:d2}:{1:d2}" -f $Hour, $Minute), "HH:mm", $null)
$action  = New-ScheduledTaskAction -Execute $Py -Argument ("`"" + $Script + "`"" ) -WorkingDirectory $Dir
$trigger = New-ScheduledTaskTrigger -Daily -At $at

# 只带这两个开关实测可用；其余设置用系统默认值，避免参数解析坑
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

try {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Description "Jellyfin 人物资料本地兜底补齐（每天自动补缺）" | Out-Null
    Say "计划任务已注册: $TaskName"
} catch {
    Say "注册失败: $($_.Exception.Message)"
    exit 3
}

Say "  执行: $Py `"$Script`""
Say "  工作目录: $Dir"
Say "  频率: 每天 $(("{0:d2}:{1:d2}" -f $Hour, $Minute))"
Say "  日志: $(Join-Path $Dir 'logs\scheduled.log')"
Say ""
Say "验证: Get-ScheduledTask -TaskName '$TaskName' | Select TaskName,State"
Say "手动触发: Register-ScheduledTask 建好后用 任务计划程序 GUI 右键运行"
Say "卸载: powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Uninstall"

