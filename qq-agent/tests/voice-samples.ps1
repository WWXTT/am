# 声线样本：用真实现场包做底，替换触发句/意图，跑 Writer 生成候选，落一个可盲评的 md。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File qq-agent\tests\voice-samples.ps1 [-N 2]
# 输出：qq-agent\tests\out\voice-samples-<时间>.md
param(
    [int]$N = 2,
    [string]$Chat = ""
)

$ErrorActionPreference = "Stop"
$qa = Split-Path $PSScriptRoot -Parent
$repo = Split-Path $qa -Parent
$exe = Join-Path $qa "src\QqAgent\bin\Release\net10.0-windows\qq-agent.exe"
$cfg = Join-Path $qa "config.json"
$outDir = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force $outDir | Out-Null

if (-not (Test-Path $exe)) { throw "先编译: dotnet build qq-agent\src\QqAgent\QqAgent.csproj -c Release" }

# 底包：优先用 pending；没有就从 jsonl 重建
$baseTmp = Join-Path $env:TEMP "amy-voice-base.json"
if (Test-Path (Join-Path $qa "logs\amy-pending.json")) {
    Copy-Item (Join-Path $qa "logs\amy-pending.json") $baseTmp -Force
} else {
    $args = @("duty", "pack", "--rebuild", "--config", $cfg, "--out", $baseTmp)
    if ($Chat) { $args = @("duty", "pack", "--rebuild", "--chat", $Chat, "--config", $cfg, "--out", $baseTmp) }
    & $exe @args | Out-Null
}
$base = Get-Content $baseTmp -Raw | ConvertFrom-Json

$scenarios = @(
    @{ name = "被讨token";       trigger = "好猫发发善心匀一点token给我罢 @Amy"; now = @("我的是1060喵", "好猫发发善心匀一点token给我罢 @Amy"); intent = "把讨 token 接成讨饭，别惯着；点他 1060 老卡"; mood = "normal" },
    @{ name = "被骂没攻击性";    trigger = "你怎么骂人那么没攻击性啊";          now = @("你怎么骂人那么没攻击性啊"); intent = "不解释，反手抠他 ID/发言的蠢点，两句内收"; mood = "dark" },
    @{ name = "被质问割裂";      trigger = "你不觉得你很割裂么";                now = @("你不觉得你很割裂么", "好话说不明白"); intent = "不辩解，把『割裂』这个词还给他自己"; mood = "dark" },
    @{ name = "纯图无文字";      trigger = "[图片|]";                          now = @("[图片|]"); intent = "看不懂就沉默（应该输出 SILENT）"; mood = "normal" },
    @{ name = "冷场";            trigger = "……";                              now = @("……", "……"); intent = "冷场，可以只发个颜文字或者不发"; mood = "normal" },
    @{ name = "被 @ 提旧事";     trigger = "你上次说的那句还算数吗 @Amy";      now = @("你上次说的那句还算数吗 @Amy"); intent = "记性差，不认旧账，按这句重新接"; mood = "normal" },
    @{ name = "旧梗复读现场";    trigger = "寿来~ 寿来~";                      now = @("寿来~ 寿来~"); intent = "同一个梗今天用过，别再重复同样的说法"; mood = "normal" },
    @{ name = "新词出现";        trigger = "这叫凭空哈气";                      now = @("这叫凭空哈气", "还有凭空哈气"); intent = "按字面接这个新词，别装懂"; mood = "normal" },
    @{ name = "转发外部乐子";    trigger = "[聊天记录|某网红发言合集]";          now = @("[聊天记录|某网红发言合集]"); intent = "骂外面那个现象，站群友这边，不影射群友"; mood = "normal" },
    @{ name = "被要求发语音";    trigger = "说句话听听 @Amy";                  now = @("说句话听听 @Amy"); intent = "用文字回一句短的，别答应发语音"; mood = "normal" },
    @{ name = "深夜闲聊";        trigger = "这个点了还不睡？";                  now = @("这个点了还不睡？"); intent = "轻松接一句，不劝人睡觉"; mood = "normal" },
    @{ name = "有人吵架拉 Amy";  trigger = "Amy 你说他是不是傻逼";            now = @("Amy 你说他是不是傻逼"); intent = "不站队评判群友，阴阳提问的人"; mood = "normal" }
)

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $outDir "voice-samples-$stamp.md"
$lines = @("# 声线样本 $stamp", "", "底包：$($base.chat) / 生成 $N 条候选 / 温度见 config.Writer", "")

foreach ($s in $scenarios) {
    $pack = $base | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $pack.trigger.text = $s.trigger
    $pack.scene.now = @($s.now | ForEach-Object { [pscustomobject]@{ line = 0; t = ""; sender = "群友"; uid = 0; text = $_; is_self = $false; pics = 0 } })
    $pack.scene.ambient = @()
    $pack.self_recent = @()
    $tmpPack = Join-Path $env:TEMP "amy-voice-scene.json"
    $pack | ConvertTo-Json -Depth 20 | Set-Content $tmpPack -Encoding utf8

    $lines += "## $($s.name)"
    $lines += ""
    $lines += "- 触发：``$($s.trigger)``"
    $lines += "- 意图：$($s.intent)"
    $lines += "- 语气：$($s.mood)"
    $lines += ""
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $res = & $exe duty say --pack $tmpPack --intent $s.intent --mood $s.mood --n $N --config $cfg 2>&1
    $sw.Stop()
    $lines += '```'
    $lines += ($res | Out-String).TrimEnd()
    $lines += '```'
    $lines += ""
    $lines += "（$($sw.ElapsedMilliseconds)ms）"
    $lines += ""
    Write-Output ("[ok] {0} ({1}ms)" -f $s.name, $sw.ElapsedMilliseconds)
}

$lines | Set-Content $out -Encoding utf8
Remove-Item $baseTmp -ErrorAction SilentlyContinue
Write-Output "样本已写出：$out"

