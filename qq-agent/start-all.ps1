# Amy 一键启动（合并入口）：依赖准备 → QQNT → Yuno → 值班脑依赖 → daemon。
# 生图（SD WebUI :7860）**故意不启动**：一开就吃 GPU/显存，会打扰工作；需要时手动按维护手册开。
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File qq-agent\start-all.ps1
#   ... -CheckOnly          只体检，不安装/不启动任何服务
#   ... -NoBuild            跳过 dotnet build
#   ... -NoInstall          跳过 npm/pip 安装（离线/快速启动）
#   ... -SkipYuno / -SkipQQ / -SkipDaemon
param(
    [switch]$CheckOnly,
    [switch]$NoBuild,
    [switch]$NoInstall,
    [switch]$SkipYuno,
    [switch]$SkipQQ,
    [switch]$SkipDaemon
)

$ErrorActionPreference = "Continue"
$QA = $PSScriptRoot                      # qq-agent
$REPO = Split-Path $QA -Parent
$CONFIG = Join-Path $QA "config.json"
$EXE = Join-Path $QA "src\QqAgent\bin\Release\net10.0-windows\qq-agent.exe"
$CSPROJ = Join-Path $QA "src\QqAgent\QqAgent.csproj"
$YUNO = Join-Path $REPO "yuno"
$YUNO_PY = Join-Path $YUNO ".venv\Scripts\python.exe"
$QQ = "C:\Program Files\Tencent\QQNT\QQ.exe"
$TOTAL = 6
$script:hardFails = 0
$script:softWarns = 0

function Step([int]$n, [string]$msg) { Write-Host ("[{0}/{1}] {2}" -f $n, $TOTAL, $msg) }
function Ok([string]$msg) { Write-Host ("      ok   " + $msg) }
function Warn([string]$msg) { $script:softWarns++; Write-Host ("      warn " + $msg) -ForegroundColor Yellow }
function Fail([string]$msg) { $script:hardFails++; Write-Host ("      FAIL " + $msg) -ForegroundColor Red }

function WriteLine([string]$m) { Write-Host $m }

function Get-YunoToken {
    try {
        $envFile = Join-Path $YUNO ".env"
        if (-not (Test-Path $envFile)) { return "" }
        foreach ($line in Get-Content $envFile -Encoding UTF8) {
            if ($line -match '^\s*YUNO_API_TOKEN\s*=\s*(.+?)\s*$') { return $Matches[1].Trim().Trim('"').Trim("'") }
        }
    } catch { }
    return ""
}

function Test-YunoHealth([string]$token, [int]$timeoutSec = 3) {
    try {
        $r = Invoke-RestMethod -Uri "http://127.0.0.1:8457/health" -Headers @{ Authorization = "Bearer $token" } -TimeoutSec $timeoutSec
        return $r
    } catch { return $null }
}

function Test-Port([int]$port) {
    try {
        $c = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
        return ($null -ne $c -and @($c).Count -gt 0)
    } catch { return $false }
}

function Ensure-BrainCli {
    $cli = Join-Path $QA "brain\node_modules\@deepseek-ai\dsh\lib\bin.js"
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { Fail "找不到 node（值班脑需要 Node.js）；装好后重跑"; return }
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { Fail "找不到 npm"; return }
    $nodeVer = (& node --version) 2>$null
    Ok ("node " + $nodeVer)
    if (Test-Path $cli) { Ok "值班脑 CLI 已安装"; return }
    if ($CheckOnly) { Warn "值班脑 CLI 未安装（CheckOnly 不安装）：跑 qq-agent\brain\install.ps1"; return }
    if ($NoInstall) { Fail "值班脑 CLI 未安装（-NoInstall 下不自动装）：跑 qq-agent\brain\install.ps1"; return }
    Step 3 "值班脑依赖缺失，安装 @deepseek-ai/dsh（首次约几分钟）"
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $QA "brain\install.ps1")
    if ($LASTEXITCODE -ne 0) { Fail "brain install.ps1 失败" } else { Ok "值班脑 CLI 安装完成" }
}

function Ensure-Build {
    if ($NoBuild) { Warn "跳过 dotnet build（-NoBuild）"; return }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail "找不到 dotnet（.NET 10 SDK）；装好后重跑"; return }
    if ($CheckOnly) {
        if (-not (Test-Path $EXE)) { Fail "qq-agent.exe 不存在（CheckOnly 不编译）" ; return }
        $exeTime = (Get-Item $EXE).LastWriteTimeUtc
        $newer = Get-ChildItem (Join-Path $QA "src\QqAgent") -Recurse -Include *.cs,*.csproj -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTimeUtc -gt $exeTime } | Select-Object -First 1
        if ($newer) { Warn ("源码比 exe 新（" + $newer.Name + "；CheckOnly 不编译）") } else { Ok "qq-agent.exe 已是最新" }
        return
    }
    $need = $true
    if (Test-Path $EXE) {
        $exeTime = (Get-Item $EXE).LastWriteTimeUtc
        $newer = Get-ChildItem (Join-Path $QA "src\QqAgent") -Recurse -Include *.cs,*.csproj -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTimeUtc -gt $exeTime } | Select-Object -First 1
        $need = ($null -ne $newer)
    }
    if (-not $need) { Ok "qq-agent.exe 已是最新"; return }
    WriteLine "      building qq-agent ..."
    & dotnet build $CSPROJ -c Release -v q 2>&1 | Select-Object -Last 3 | ForEach-Object { WriteLine ("      " + $_) }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $EXE)) { Fail "dotnet build 失败" } else { Ok "qq-agent 编译完成" }
}

function Ensure-YunoDeps {
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { Fail "找不到 python；Yuno 需要 Python"; return $false }
    if (-not (Test-Path $YUNO_PY)) {
        if ($CheckOnly) { Warn "Yuno venv 不存在（yuno\.venv）；CheckOnly 不创建"; return $false }
        if ($NoInstall) { Warn "Yuno venv 不存在且 -NoInstall：跳过"; return $false }
        WriteLine "      创建 yuno\.venv 并安装依赖（首次可能几分钟，网络要通）..."
        & python -m venv (Join-Path $YUNO ".venv")
        if (Test-Path $YUNO_PY) {
            & $YUNO_PY -m pip install --upgrade pip 2>&1 | Out-Null
            $req = Join-Path $YUNO "requirements.txt"
            if (Test-Path $req) { & $YUNO_PY -m pip install -r $req 2>&1 | Select-Object -Last 3 | ForEach-Object { WriteLine ("      " + $_) } }
        }
    }
    if (-not (Test-Path $YUNO_PY)) { Fail "Yuno venv 仍不可用"; return $false }
    $probe = & $YUNO_PY -c "import fastapi, uvicorn; print('ok')" 2>&1
    if (($probe -join "") -notmatch 'ok') {
        if ($CheckOnly) { Warn "Yuno 依赖不完整（fastapi/uvicorn 导入失败）"; return $false }
        if ($NoInstall) { Warn "Yuno 依赖不完整且 -NoInstall：跳过安装"; return $false }
        $req = Join-Path $YUNO "requirements.txt"
        if (Test-Path $req) {
            WriteLine "      pip install -r yuno\requirements.txt ..."
            & $YUNO_PY -m pip install -r $req 2>&1 | Select-Object -Last 3 | ForEach-Object { WriteLine ("      " + $_) }
        }
        $probe = & $YUNO_PY -c "import fastapi, uvicorn; print('ok')" 2>&1
        if (($probe -join "") -notmatch 'ok') { Fail "Yuno 依赖安装失败"; return $false }
    }
    Ok "Yuno venv 依赖可用"
    return $true
}

function Start-YunoIfNeeded {
    if ($SkipYuno) { Warn "跳过 Yuno（-SkipYuno）"; return }
    $token = Get-YunoToken
    if (-not $token) { Fail "yuno\.env 里没有 YUNO_API_TOKEN；无法健康检查"; return }
    $h = Test-YunoHealth $token
    if ($h) { Ok ("Yuno 已在运行：" + ($h | ConvertTo-Json -Compress)); return }
    if ($CheckOnly) { Warn "Yuno 未运行（CheckOnly 不启动）"; return }
    if (-not (Test-Path $YUNO_PY)) { Fail "Yuno 未运行且 venv 不可用"; return }

    WriteLine "      启动 Yuno（:8457）..."
    Push-Location $YUNO
    try {
        & python start_hidden.py 2>&1 | Select-Object -First 2 | ForEach-Object { WriteLine ("      " + $_) }
    } catch {
        WriteLine ("      start_hidden 失败：" + $_.Exception.Message)
    } finally { Pop-Location }

    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 3
        $h = Test-YunoHealth $token
        if ($h) { $ready = $true; Ok ("Yuno 就绪：" + ($h | ConvertTo-Json -Compress)); break }
        if (($i % 5) -eq 4) { WriteLine ("      ... 等待 Yuno 加载 embedder（" + (($i + 1) * 3) + "s）") }
    }
    if (-not $ready) {
        Fail "Yuno 未就绪（看 yuno\logs\service.log）"
        WriteLine "      提示：若 start_hidden.py 报 WinError 5，说明当前进程在受限 job 里；请在资源管理器里双击 一键启动.bat"
    }
}

function Start-QqIfNeeded {
    if ($SkipQQ) { Warn "跳过 QQ（-SkipQQ）"; return }
    $procs = @(Get-Process QQ -ErrorAction SilentlyContinue)
    $main = $procs | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
    if ($main) { Ok ("QQ 已在运行（pid=" + $main.Id + "）"); return }
    if (Test-Path $QQ) {
        if ($procs.Count -gt 0) { Warn "QQ 进程在但没有主窗口（可能收托盘）；请手动点开 QQ 窗口"; return }
        if ($CheckOnly) { Warn "QQ 未运行（CheckOnly 不启动）"; return }
        WriteLine "      启动 QQNT（--force-renderer-accessibility）..."
        Start-Process -FilePath $QQ -ArgumentList "--force-renderer-accessibility" | Out-Null
        Start-Sleep -Seconds 15
        $main = @(Get-Process QQ -ErrorAction SilentlyContinue) | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
        if ($main) { Ok ("QQ 窗口就绪（pid=" + $main.Id + "）") } else { Warn "QQ 已启动但窗口未就绪（登录/托盘？）" }
    } else {
        Fail ("找不到 QQ 客户端：" + $QQ)
    }
}

function Start-DaemonIfNeeded {
    if ($SkipDaemon) { Warn "跳过 daemon（-SkipDaemon）"; return }
    $procs = @(Get-Process qq-agent -ErrorAction SilentlyContinue)
    if ($procs.Count -gt 0) { Ok ("daemon 已在运行（pid=" + $procs[0].Id + "）"); return }
    if ($CheckOnly) { Warn "daemon 未运行（CheckOnly 不启动）"; return }
    if (-not (Test-Path $EXE)) { Fail "daemon 未编译：qq-agent.exe 不存在"; return }
    WriteLine "      启动 daemon（新窗口 = 值班直播台）..."
    Start-Process -FilePath "cmd.exe" -ArgumentList "/c", "daemon.bat" -WorkingDirectory $QA -WindowStyle Normal | Out-Null
    Start-Sleep -Seconds 6
    $procs = @(Get-Process qq-agent -ErrorAction SilentlyContinue)
    if ($procs.Count -gt 0) { Ok ("daemon 已启动（pid=" + $procs[0].Id + "）") } else { Fail "daemon 未启动（看新窗口报错，或在 qq-agent\ 下跑 daemon.bat）" }
}

# ==================== 主流程 ====================
WriteLine ""
WriteLine "=== Amy 启动（合并入口） ==="
if ($CheckOnly) { WriteLine "模式：CheckOnly（只体检，不安装/不启动）" }
WriteLine ("仓库：" + $REPO)

Step 1 "检查配置/数据"
if (Test-Path $CONFIG) {
    try {
        $cfg = Get-Content $CONFIG -Raw | ConvertFrom-Json
        Ok ("config.json 可读：Mode=" + $cfg.AmyRuntime.Mode + " NotifyOnly=" + $cfg.Daemon.NotifyOnly)
        if ([string]::IsNullOrWhiteSpace($cfg.Model.ApiKey)) { Fail "config.json → Model.ApiKey 为空（值班脑 spawn 依赖它）" } else { Ok "模型密钥已配置" }
    } catch { Fail ("config.json 解析失败：" + $_.Exception.Message) }
} else { Fail ("缺 config.json：" + $CONFIG) }
foreach ($p in @("data\scope-map.json", "data\roster-map.json", "data\memes.json")) {
    if (Test-Path (Join-Path $QA $p)) { Ok $p } else { Warn ("缺 " + $p) }
}
foreach ($p in @("yuno\data\bge-large-zh-v1.5", "yuno\data\persona-amy")) {
    if (Test-Path (Join-Path $REPO $p)) { Ok $p } else { Warn ("缺 " + $p + "（Yuno 会尝试下载/新建，首次可能很慢）") }
}
foreach ($p in @("docs\哨兵.md", "docs\维护手册.md", "duty.md", "persona.md")) {
    if (Test-Path (Join-Path $REPO $p)) { Ok $p } else { Warn ("缺 " + $p) }
}
New-Item -ItemType Directory -Force (Join-Path $QA "logs") | Out-Null

Step 2 "编译 qq-agent"
Ensure-Build

Step 3 "值班脑依赖（DSH headless）"
Ensure-BrainCli

Step 4 "Yuno 记忆服务（:8457）"
$null = Ensure-YunoDeps
Start-YunoIfNeeded

Step 5 "QQ 客户端"
Start-QqIfNeeded

Step 6 "daemon（哨兵）"
Start-DaemonIfNeeded

WriteLine ""
WriteLine "=== 生图（SD WebUI :7860）不启动 ==="
WriteLine "  生图一开就吃 GPU/显存，会打扰工作；需要时按 docs\维护手册.md「生图」小节手动开。"
WriteLine ""
WriteLine "=== 自检（duty doctor） ==="
if (Test-Path $EXE) {
    & $EXE duty doctor --config $CONFIG
} else { Warn "qq-agent.exe 不存在，跳过 duty doctor" }

WriteLine ""
if ($script:hardFails -gt 0) { WriteLine ("== 启动有 " + $script:hardFails + " 项失败（警告 " + $script:softWarns + "） ==") -ForegroundColor Red; exit 1 }
WriteLine ("== 就绪（警告 " + $script:softWarns + "） ==") -ForegroundColor Green
if (-not $CheckOnly -and -not $SkipDaemon) {
    WriteLine "下一步：在群里发一条消息验证；值班直播看 daemon 窗口或 qq-agent\logs\amy-live.log"
}
exit 0




