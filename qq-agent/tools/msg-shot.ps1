# 消息截图工具（2026-10-27 owner 定案「以后就这么干」）
# 用途：图片缓存里取不到（引用缩略图 / 网络直渲）时，直接从 QQ 窗口截那条消息的气泡，存 PNG，再 sendimg 发群。
# 做法：snap 抓整窗（PrintWindow）→ dump 找目标消息的气泡矩形 → 按「屏幕坐标 − 窗口左上角」裁切。
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File qq-agent\tools\msg-shot.ps1 -Text "以后看到这张图"
#   ... -Text "..." -Out qq-agent\logs\img\x.png -Margin 16
# 输出：末行打印 PNG 绝对路径；退出码 0=成功，2=没找到那条消息（先滚到它），3=反复对不上坐标。
# 注意：daemon 会自己滚屏/挪窗，snap 与 dump 之间有竞态；脚本内部重试 3 次。

param(
    [Parameter(Mandatory = $true)][string]$Text,
    [string]$Out = "",
    [int]$Margin = 14
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "..\src\QqAgent\bin\Release\net10.0-windows\qq-agent.exe"
if (-not (Test-Path $exe)) { Write-Error "找不到 qq-agent.exe: $exe"; exit 1 }
$imgDir = Join-Path $PSScriptRoot "..\logs\img"

$rectRx = [regex]"rect=\((-?\d+),(-?\d+)\s+(\d+)x(\d+)\)"
$clsRx = [regex]"cls='([^']*)'"
$nameRx = [regex]"name='([^']*)'"

$snapPath = $null; $cw = 0; $ch = 0; $x1 = 0; $y1 = 0

for ($attempt = 1; $attempt -le 3; $attempt++) {
    # 1) 抓整窗 + 拿窗口 rect（同一时刻）
    $snapOut = (& $exe snap --out $imgDir 2>&1 | Out-String)
    $png = [regex]::Match($snapOut, "saved\s+(\S+\.png)").Groups[1].Value
    $winR = [regex]::Match($snapOut, "rect=\((-?\d+),(-?\d+)\s+(\d+)x(\d+)\)")
    if (-not $png -or -not $winR.Success) { Write-Error "snap 失败：`n$snapOut"; exit 1 }
    $winLeft = [int]$winR.Groups[1].Value; $winTop = [int]$winR.Groups[2].Value
    $winW = [int]$winR.Groups[3].Value;    $winH = [int]$winR.Groups[4].Value

    # 2) dump 找目标消息所在的气泡矩形
    $dump = (& $exe dump 2>&1 | Out-String)
    $lines = $dump -split "`r?`n"
    $blocks = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $c = $clsRx.Match($lines[$i]); $r = $rectRx.Match($lines[$i])
        if (-not ($c.Success -and $r.Success)) { continue }
        $cls = $c.Groups[1].Value
        if ($cls -like "*message-container*" -and $cls -notlike "*msg-content-container*") {
            $blocks += [pscustomobject]@{
                Start = $i
                Left  = [int]$r.Groups[1].Value; Top = [int]$r.Groups[2].Value
                W     = [int]$r.Groups[3].Value; H   = [int]$r.Groups[4].Value
                Hit   = $false
            }
        }
    }
    if ($blocks.Count -eq 0) { Write-Error "dump 里没有 message-container，窗口可能没开在聊天页"; exit 2 }

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $n = $nameRx.Match($lines[$i])
        if (-not $n.Success -or $n.Groups[1].Value -notlike "*$Text*") { continue }
        $blk = $blocks | Where-Object { $_.Start -le $i } | Select-Object -Last 1
        if ($blk) { $blk.Hit = $true }
    }
    $hit = $blocks | Where-Object { $_.Hit } | Select-Object -Last 1
    if (-not $hit) { Write-Error "可视区里找不到包含「$Text」的消息（先滚到那条消息再跑）"; exit 2 }

    # 3) 坐标校验：整条气泡必须落在窗口内（否则就是 snap/dump 之间被滚屏或挪窗了）
    $top = $hit.Top - $winTop; $bottom = $top + $hit.H
    $left = $hit.Left - $winLeft; $right = $left + $hit.W
    if ($top -lt -40 -or $bottom -gt $winH + 40 -or $left -lt -40 -or $right -gt $winW + 40) {
        if ($attempt -lt 3) { Start-Sleep -Milliseconds 800; continue }
        Write-Error "目标消息不在可视区（rect 越界）：$($hit.Left),$($hit.Top) $($hit.W)x$($hit.H)；窗口 ${winLeft},${winTop} ${winW}x${winH}"
        exit 3
    }

    $x1 = [Math]::Max(0, $left - $Margin);  $y1 = [Math]::Max(0, $top - $Margin)
    $x2 = [Math]::Min($winW, $right + $Margin); $y2 = [Math]::Min($winH, $bottom + $Margin)
    $cw = $x2 - $x1; $ch = $y2 - $y1
    if ($cw -le 0 -or $ch -le 0) { if ($attempt -lt 3) { Start-Sleep -Milliseconds 800; continue } ; exit 3 }
    $snapPath = (Resolve-Path $png).Path
    break
}

# 4) 裁切保存
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile($snapPath)
$bmp = New-Object System.Drawing.Bitmap($cw, $ch)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.DrawImage($img, (New-Object System.Drawing.Rectangle(0, 0, $cw, $ch)), (New-Object System.Drawing.Rectangle($x1, $y1, $cw, $ch)), [System.Drawing.GraphicsUnit]::Pixel)
$dest = if ($Out) { Join-Path (Get-Location) $Out } else { Join-Path $imgDir ("msgshot-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".png") }
$dest = [System.IO.Path]::GetFullPath($dest)
$dir = Split-Path -Parent $dest
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$bmp.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $img.Dispose()

Write-Output "crop ${cw}x${ch} <- window (${winLeft},${winTop}) ${winW}x${winH}"
Write-Output $dest
exit 0
