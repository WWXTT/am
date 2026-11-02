# 安装/校验 Amy 值班脑依赖：@deepseek-ai/dsh@<固定版本>
# 幂等：已存在且版本一致就跳过。改版本时同步 docs\维护手册.md 与 qq-agent\config.json。
param(
    [string]$Version = "0.2.1-rc.2",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$here = Split-Path $PSScriptRoot -Parent   # qq-agent
$brain = $PSScriptRoot
$pkg = Join-Path $brain "node_modules\@deepseek-ai\dsh\package.json"

function Get-PnpmFallback {
    $root = Join-Path $env:USERPROFILE ".dsh\dsh-runtimes\dsh-primary-runtime\dependencies\pnpm"
    if (Test-Path $root) {
        $cand = Get-ChildItem $root -Recurse -Filter "pnpm.cjs" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cand) { return $cand.FullName }
    }
    return $null
}

if (-not $Force -and (Test-Path $pkg)) {
    $cur = (Get-Content $pkg -Raw | ConvertFrom-Json).version
    if ($cur -eq $Version) {
        Write-Output "brain: @deepseek-ai/dsh $cur already installed, skip"
        exit 0
    }
    Write-Output "brain: installed $cur != wanted $Version, reinstalling"
}

$pkgJson = Join-Path $brain "package.json"
if (-not (Test-Path $pkgJson)) {
    '{ "name": "amy-brain", "private": true, "version": "1.0.0" }' | Set-Content -Path $pkgJson -Encoding utf8
}

# pnpm 兜底（profile 首次初始化可能需要）
$pnpm = Get-PnpmFallback
if ($pnpm) { $env:PATH = (Split-Path $pnpm -Parent) + ";" + $env:PATH }

Write-Output "brain: installing @deepseek-ai/dsh@$Version ..."
Push-Location $brain
try {
    & npm install "@deepseek-ai/dsh@$Version" --no-audit --no-fund --loglevel=error
    if ($LASTEXITCODE -ne 0) { throw "npm install failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

$bin = Join-Path $brain "node_modules\@deepseek-ai\dsh\lib\bin.js"
if (-not (Test-Path $bin)) { throw "dsh CLI entry not found: $bin" }
$ver = (Get-Content $pkg -Raw | ConvertFrom-Json).version
Write-Output "brain: installed $ver"
Write-Output "brain: cli = $bin"

