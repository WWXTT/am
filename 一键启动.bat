@echo off
title Amy one-click start
rem 合并后的唯一启动入口：依赖准备 + QQNT + Yuno + 值班脑依赖 + daemon
rem 具体步骤在 qq-agent\start-all.ps1；本文件只负责调用与保留窗口
rem 参数透传：-CheckOnly 只体检 / -NoBuild / -NoInstall / -SkipYuno / -SkipQQ / -SkipDaemon
rem 生图(SD WebUI :7860)故意不启动：占 GPU 会打扰工作，需要时手动按维护手册开
rem 本文件保持 GBK + CRLF；功能性 echo 只用 ASCII
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0qq-agent\start-all.ps1" %*
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
    echo === exited with errors: %RC% ===
) else (
    echo === done ===
)
echo You can close this window; the daemon runs in its own window.
pause >nul