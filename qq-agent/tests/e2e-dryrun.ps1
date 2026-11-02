# 全链演练（不发送）：rebuild 现场包 -> 改成 dry_run + 自定义 seq -> spawn DSH 值班脑 -> 校验 live/ack/dryrun。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File qq-agent\tests\e2e-dryrun.ps1 [-TimeoutSec 300]
param(
    [int]$TimeoutSec = 300,
    [int]$Seq = 9001
)

$ErrorActionPreference = "Stop"
$qa = Split-Path $PSScriptRoot -Parent
$repo = Split-Path $qa -Parent
$exe = Join-Path $qa "src\QqAgent\bin\Release\net10.0-windows\qq-agent.exe"
$cfg = Join-Path $qa "config.json"
$logs = Join-Path $qa "logs"
$pending = Join-Path $logs "amy-pending.json"
$ack = Join-Path $logs "amy-ack.json"
$live = Join-Path $logs "amy-live.log"
$dry = Join-Path $logs "amy-dryrun.txt"
$brainLog = Join-Path $logs "brain-e2e.log"

if (-not (Test-Path $exe)) { throw "先编译: dotnet build qq-agent\src\QqAgent\QqAgent.csproj -c Release" }
$cli = Join-Path $qa "brain\node_modules\@deepseek-ai\dsh\lib\bin.js"
if (-not (Test-Path $cli)) { throw "值班脑未安装：powershell -File qq-agent\brain\install.ps1" }

$conf = Get-Content $cfg -Raw | ConvertFrom-Json
$patch = Join-Path $qa ($conf.AmyRuntime.Dsh.Patch -replace '^brain\\', 'brain\')
$dshHome = Join-Path $qa ($conf.AmyRuntime.Dsh.Home -replace '^brain\\', 'brain\')
$selfUid = 1000000001

# 1) 备份现状
$backupPending = Join-Path $env:TEMP "amy-pending-backup.json"
$hadPending = Test-Path $pending
if ($hadPending) { Copy-Item $pending $backupPending -Force }

# 2) 建包（dry_run）
$tmpPack = Join-Path $env:TEMP "amy-e2e-pack.json"
& $exe duty pack --rebuild --config $cfg --out $tmpPack | Out-Null
if ($LASTEXITCODE -ne 0) { throw "pack 重建失败" }
$j = Get-Content $tmpPack -Raw | ConvertFrom-Json
$j.seq = $Seq
$j.dry_run = $true
$j.ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$j.notify_at = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
$j | ConvertTo-Json -Depth 20 | Set-Content $pending -Encoding utf8

# 3) spawn 值班脑
$env:DSH_HOME = $dshHome
$env:DSH_TELEMETRY_DISABLED = "1"
$env:DEEPSEEK_API_KEY = $conf.Model.ApiKey
Remove-Item $dry -ErrorAction SilentlyContinue

$task = "你在跑 Amy 的值班回合（seq=$Seq，dry_run=true）。先读 qq-agent\logs\amy-pending.json（现场包 v2）和仓库根的 duty.md。这是演练：先写意图到 qq-agent\logs\amy-intent.txt，再 qq-agent\amy.cmd duty chat --seq $Seq --intent-file qq-agent\logs\amy-intent.txt --mood dark（dry_run 自动不发送）；也可以 duty skip --seq $Seq --why 演练跳过。做完就结束，不要旁白。"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "node"
$psi.WorkingDirectory = $repo
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
function Quote-Arg([string]$s) {
    if ($s -match '[\s"]') { return '"' + ($s -replace '"', '\"') + '"' }
    return $s
}
$psi.Arguments = (@($cli, "--profile", $conf.AmyRuntime.Dsh.Profile, "--patch", $patch, "--json", $task) | ForEach-Object { Quote-Arg ([string]$_) }) -join " "

$jsonl = Join-Path $logs "messages.jsonl"
$linesBefore = if (Test-Path $jsonl) { (Get-Content $jsonl -Encoding UTF8 | Measure-Object -Line).Lines } else { 0 }

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$proc = [System.Diagnostics.Process]::Start($psi)
$outTask = $proc.StandardOutput.ReadToEndAsync()
$errTask = $proc.StandardError.ReadToEndAsync()
$exited = $proc.WaitForExit($TimeoutSec * 1000)
$sw.Stop()
if (-not $exited) { try { $proc.Kill($true) } catch { }; Write-Output "[FAIL] 值班脑超时 ${TimeoutSec}s（日志：$brainLog）"; exit 1 }
$stdout = $outTask.Result
$stderr = $errTask.Result
$stdout | Set-Content $brainLog -Encoding utf8
$stderr | Set-Content "$brainLog.err" -Encoding utf8
$code = $proc.ExitCode

Write-Output ("[info] brain exit={0} elapsed={1:F0}s" -f $code, $sw.Elapsed.TotalSeconds)
$fails = 0

if ($code -ne 0) { Write-Output "[FAIL] 值班脑 exit=$code（日志：$brainLog，stderr：$brainLog.err）"; $fails++ }

$liveTail = if (Test-Path $live) { Get-Content $live -Encoding UTF8 } else { @() }
$liveHit = $liveTail | Where-Object { $_ -match "amy#$Seq (SAY|SKIP|SEND)" }
if ($liveHit) { Write-Output "[ok]   live: $($liveHit[-1])" } else { Write-Output "[FAIL] live 里没有 amy#$Seq 的 SAY/SKIP 行"; $fails++ }

try {
    $a = Get-Content $ack -Raw | ConvertFrom-Json
    if ($a.seq -eq $Seq) { Write-Output "[ok]   ack seq=$($a.seq)" } else { Write-Output "[FAIL] ack seq=$($a.seq) != $Seq（ack 写入失败？logs 不可写）"; $fails++ }
} catch { Write-Output "[FAIL] ack 读取失败"; $fails++ }

if (Test-Path $dry) { Write-Output "[ok]   dryrun: $((Get-Content $dry -Encoding UTF8 | Where-Object { $_ -and $_ -notmatch '^\[' }).Count) 行候选" }
else { Write-Output "[warn] 没有 amy-dryrun.txt（可能值班脑选择了 skip）" }

$sent = $false
if (Test-Path $jsonl) {
    $newLines = Get-Content $jsonl -Encoding UTF8 | Select-Object -Skip $linesBefore
    $sent = ($newLines | Where-Object { $_ -match '"IsSelf":true' } | Measure-Object).Count -gt 0
}
if ($sent) { Write-Output "[warn] 演练期间出现 IsSelf 消息（演练不应发送，请人工核对）" }

# 4) 还原 pending
if ($hadPending) { Copy-Item $backupPending $pending -Force } else { Remove-Item $pending -ErrorAction SilentlyContinue }
Remove-Item $tmpPack -ErrorAction SilentlyContinue

if ($fails -gt 0) { Write-Output "== 失败 $fails 项 =="; exit 1 }
Write-Output "== 演练通过（未发送）=="
exit 0





