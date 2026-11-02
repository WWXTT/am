@echo off
setlocal EnableDelayedExpansion
title qq-agent daemon
rem Amy duty sentry. Window open = on duty; close window / Ctrl+C = stop.
rem NOTE: keep this file ASCII-only. cmd.exe parses .bat in the OEM codepage,
rem so UTF-8 Chinese comments get garbled and break the script (2026-10-29 bug).
rem
rem 2026-10-29 watchdog: the daemon can die on a UIA COM fault (0x8000FFFF).
rem Restart it 5s after an abnormal exit, max 20 times. Crash detail is written
rem by the program itself to logs\daemon-crash.log.
rem Exit code -1073741510 (0xC000013A) = user closed the window / Ctrl+C -> do not restart.
cd /d "%~dp0"
set /a tries=0

:loop
set /a tries+=1
src\QqAgent\bin\Release\net10.0-windows\qq-agent.exe daemon --config config.json
set code=%ERRORLEVEL%
echo.
if "%code%"=="0" goto done
if "%code%"=="-1073741510" goto done
if !tries! GEQ 20 (
  echo ===== daemon crashed 20 times, stop auto-restart (see logs\daemon-crash.log) =====
  goto done
)
echo ===== daemon exited abnormally (code %code%), restart in 5s (attempt !tries!) =====
rem ping instead of timeout: timeout needs a console input handle and dies with
rem "Input redirection is not supported" when the bat is started without one.
ping -n 6 127.0.0.1 >nul
goto loop

:done
echo ===== daemon exited (code %code%) =====
pause
