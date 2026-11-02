# 现场包离线回放：从 messages.jsonl 重建包，断言 schema/预算/耗时。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File qq-agent\tests\replay.ps1 [-Runs 3]
param(
    [int]$Runs = 3
)

$ErrorActionPreference = "Stop"
$qa = Split-Path $PSScriptRoot -Parent
$repo = Split-Path $qa -Parent
$exe = Join-Path $qa "src\QqAgent\bin\Release\net10.0-windows\qq-agent.exe"
$cfg = Join-Path $qa "config.json"
$tmp = Join-Path $env:TEMP "amy-replay-pack.json"

if (-not (Test-Path $exe)) { throw "先编译: dotnet build qq-agent\src\QqAgent\QqAgent.csproj -c Release" }

$maxBytes = ((Get-Content $cfg -Raw | ConvertFrom-Json).Scene.MaxBytes)
$fails = 0
$rows = @()

for ($i = 1; $i -le $Runs; $i++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $exe duty pack --rebuild --config $cfg --out $tmp | Out-Null
    $code = $LASTEXITCODE
    $sw.Stop()
    if ($code -ne 0) { Write-Output "[FAIL] run#$i pack exit=$code"; $fails++; continue }

    $j = Get-Content $tmp -Raw | ConvertFrom-Json
    $bytes = (Get-Item $tmp).Length
    $errs = @()
    if ($j.v -ne 2) { $errs += "v=$($j.v)" }
    if (-not $j.chat) { $errs += "chat 空" }
    if ($j.scene.now.Count -gt 8) { $errs += "now=$($j.scene.now.Count)" }
    if ($j.scene.ambient.Count -gt 24) { $errs += "ambient=$($j.scene.ambient.Count)" }
    if ($bytes -gt $maxBytes * 1.1) { $errs += "bytes=$bytes > $maxBytes*1.1" }
    if ($sw.ElapsedMilliseconds -gt 1500) { $errs += "elapsed=$($sw.ElapsedMilliseconds)ms" }
    if ($null -eq $j.trigger) { $errs += "trigger 缺失" }
    else {
        if (-not $j.trigger.line) { $errs += "trigger.line 缺失" }
        if (-not $j.trigger.uid) { $errs += "trigger.uid=0（查 roster-map）" }
    }
    if ($errs.Count -gt 0) { Write-Output ("[FAIL] run#{0}: {1}" -f $i, ($errs -join "; ")); $fails++ }
    else { Write-Output ("[ok]   run#{0}: {1}ms {2}B now={3} ambient={4} memes={5}" -f $i, $sw.ElapsedMilliseconds, $bytes, $j.scene.now.Count, $j.scene.ambient.Count, $j.memes.useful.Count) }
    $rows += [pscustomobject]@{ Run = $i; Ms = $sw.ElapsedMilliseconds; Bytes = $bytes }
}

if ($rows.Count -gt 0) {
    $avg = [math]::Round(($rows | Measure-Object Ms -Average).Average)
    $max = ($rows | Measure-Object Ms -Maximum).Maximum
    Write-Output "== pack 构建：平均 ${avg}ms / 最大 ${max}ms（预算 ≤1500ms，含进程启动）=="
}
Remove-Item $tmp -ErrorAction SilentlyContinue
if ($fails -gt 0) { Write-Output "== 失败 $fails 项 =="; exit 1 }
Write-Output "== 全部通过 =="
exit 0
