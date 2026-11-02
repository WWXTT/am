# 截图并发送（Amy 值班能力，2026-10-27）
# 用法（仓库根执行）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File qq-agent\tools\shot-send.ps1 -Chat "潮汐"
#   ... -All                     连同其它 QQ 顶层窗口一起抓（转发查看器/大图窗）
#   ... -NoSend                  只截图不发送
#   ... -OutDir qq-agent\logs\img
# 说明：
#   - 截图走 qq-agent.exe snap（PrintWindow，被遮挡也能截）；默认只截 QQ 主窗。
#   - 发送走 qq-agent.exe sendimg（剪贴板位图+文件双格式，自动核会话标题）。
#   - 产物落在 qq-agent\logs\img（= Action.SendDirs 白名单内），所以也能用
#     `duty sendimg --seq N --file qq-agent\logs\img\<png>` 发，保持行动审计。
param(
    [string]$Chat = "潮汐",
    [switch]$All,
    [switch]$NoSend,
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"
if (-not $RepoRoot) {
    $RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
}
$QA = Join-Path $RepoRoot "qq-agent"
$EXE = Join-Path $QA "src\QqAgent\bin\Release\net10.0-windows\qq-agent.exe"
$OutDir = Join-Path $QA "logs\img"
New-Item -ItemType Directory -Force $OutDir | Out-Null

$before = @(Get-ChildItem (Join-Path $QA "logs") -Filter "snap-*.png" -File -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty FullName)

Push-Location $QA
try {
    if ($All) { & $EXE snap --all } else { & $EXE snap }
    if ($LASTEXITCODE -ne 0) { throw "snap 失败（exit=$LASTEXITCODE）" }
} finally { Pop-Location }

$new = @(Get-ChildItem (Join-Path $QA "logs") -Filter "snap-*.png" -File |
    Where-Object { $before -notcontains $_.FullName } |
    Sort-Object LastWriteTime -Descending)
if ($new.Count -eq 0) { throw "找不到新截图" }

$src = $new[0].FullName
$dst = Join-Path $OutDir $new[0].Name
Copy-Item $src $dst -Force
Write-Output ("[shot-send] 截图: " + $dst)

if ($NoSend) { Write-Output "[shot-send] -NoSend：只截图"; exit 0 }

Push-Location $QA
try {
    & $EXE sendimg $Chat $dst
    $code = $LASTEXITCODE
} finally { Pop-Location }
if ($code -ne 0) { throw "sendimg 失败（exit=$code）" }
Write-Output ("[shot-send] 已发送到 '" + $Chat + "'")
