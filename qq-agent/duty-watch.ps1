# 人工观察台的唤醒源（DSH GUI 会话）：用 pwsh 工具以 run_in_background=true 运行本脚本，
#   实际命令写 `powershell -NoProfile -ExecutionPolicy Bypass -File qq-agent\duty-watch.ps1`。
#   不要直接写 `pwsh`：本机没装 PowerShell 7、PATH 里没有 pwsh.exe，DSH 的 pwsh 工具落地就是 5.1，
#   命令里再写 pwsh 会 CommandNotFoundException → 后台任务秒退、值班无人唤醒（2026-10-27 实录）。
# 盯 qq-agent\logs\amy-pending.json 变化（daemon NotifyOnly 模式下原子投递新消息）。
# 变化后先「静置」SettleSec 秒（默认 15）再退出 —— 群里一秒一条时，立即退出会让会话一轮只处理一条，
# 白白多烧十几轮；静置够久再叫醒，一次处理最新那一包（ack 只认最新 seq，跳过中间包不算漏）。
# 自动脑模式（Daemon.NotifyOnly=false + AmyRuntime.Mode=dsh）下不需要本脚本：daemon 自己 spawn 值班脑。
# 参数 -TimeoutSec 仅测试用（到时退出 exit=1）；正式值守不传 = 无限等到下一条消息。
param(
    [int]$TimeoutSec = 0,
    [int]$PollMs = 1500,
    [int]$SettleSec = 15
)
$pending = Join-Path $PSScriptRoot 'logs\amy-pending.json'

# 基线：启动时的 (大小, mtime)；daemon 用 temp+Move 原子替换，任一维度变化即视为新投递
$baseLen = 0L
$baseMtime = [datetime]::MinValue
if (Test-Path $pending) {
    $fi = Get-Item $pending
    $baseLen = $fi.Length
    $baseMtime = $fi.LastWriteTimeUtc
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($true) {
    Start-Sleep -Milliseconds $PollMs
    if ($TimeoutSec -gt 0 -and $sw.Elapsed.TotalSeconds -gt $TimeoutSec) {
        Write-Output 'watch-timeout'
        exit 1
    }
    if (-not (Test-Path $pending)) {
        # daemon 收 ack 后会删 pending：消失时 baseline 归零，重现（新投递）必触发，免疫 size 巧合相等
        $baseLen = 0L
        $baseMtime = [datetime]::MinValue
        continue
    }
    # 2026-10-26 竞态防御：Test-Path 与 Get-Item 之间文件被删/原子替换（daemon 重启、收 ack）会抛异常整脚本 exit 1
    # ——值班生命线断（19:05 实录）；拿不到文件就当本轮无事，下轮轮询再看
    $fi = Get-Item $pending -ErrorAction SilentlyContinue
    if (-not $fi) { continue }
    if ($fi.Length -ne $baseLen -or $fi.LastWriteTimeUtc -ne $baseMtime) {
        # 静置：等到 pending 连续 SettleSec 秒不再变化（群里刷屏时把一波消息攒成一轮处理）
        $lastLen = $fi.Length
        $lastMtime = $fi.LastWriteTimeUtc
        $stableSince = [datetime]::UtcNow
        while ($true) {
            Start-Sleep -Milliseconds $PollMs
            if ($TimeoutSec -gt 0 -and $sw.Elapsed.TotalSeconds -gt $TimeoutSec) {
                Write-Output 'pending-changed+watch-timeout'
                exit 0
            }
            $f2 = Get-Item $pending -ErrorAction SilentlyContinue
            if (-not $f2) { $stableSince = [datetime]::UtcNow; continue }
            if ($f2.Length -ne $lastLen -or $f2.LastWriteTimeUtc -ne $lastMtime) {
                $lastLen = $f2.Length
                $lastMtime = $f2.LastWriteTimeUtc
                $stableSince = [datetime]::UtcNow
                continue
            }
            if (([datetime]::UtcNow - $stableSince).TotalSeconds -ge $SettleSec) { break }
        }
        Write-Output 'pending-changed'
        exit 0
    }
}

