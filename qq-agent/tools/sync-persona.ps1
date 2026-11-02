# 同步人设：根 persona.md 是唯一真源，Yuno persona pack 副本由本脚本镜像。
# Yuno 启动会把 yuno/personas/amy/persona.md 当作 scope='ai' 的自我认知基线；
# 两份漂移会让 daemon 侧人设与 Yuno 侧人设不一致（2026-10-26 实测 988 vs 899 chars）。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File qq-agent\tools\sync-persona.ps1
#      -Check 只比较不写；退出码 1 = 有漂移。
param(
    [switch]$Check
)

$ErrorActionPreference = "Stop"
$qa = Split-Path $PSScriptRoot -Parent          # qq-agent
$root = Split-Path $qa -Parent                  # 仓库根
$src = Join-Path $root "persona.md"
$dst = Join-Path $root "yuno\personas\amy\persona.md"

if (-not (Test-Path $src)) { throw "persona.md 不存在: $src" }
$new = [System.IO.File]::ReadAllText($src, [System.Text.Encoding]::UTF8)
$old = if (Test-Path $dst) { [System.IO.File]::ReadAllText($dst, [System.Text.Encoding]::UTF8) } else { "" }

if ($new -ceq $old) {
    Write-Output "persona 已一致（$($new.Length) chars）"
    exit 0
}

if ($Check) {
    Write-Output "persona 漂移：根 $($new.Length) chars / yuno 副本 $($old.Length) chars"
    exit 1
}

$dir = Split-Path $dst -Parent
New-Item -ItemType Directory -Force $dir | Out-Null
[System.IO.File]::WriteAllText($dst, $new, (New-Object System.Text.UTF8Encoding($false)))
Write-Output "persona 同步完成：根 $($new.Length) chars -> $dst"
