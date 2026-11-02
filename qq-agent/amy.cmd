@echo off
rem Amy duty CLI wrapper (ASCII only; keep CRLF). Example:
rem   qq-agent\amy.cmd duty chat --seq 12 --intent-file qq-agent\logs\amy-intent.txt
rem   qq-agent\amy.cmd duty skip --seq 12 --why "not worth replying"
setlocal
set "HERE=%~dp0"
"%HERE%src\QqAgent\bin\Release\net10.0-windows\qq-agent.exe" %* --config "%HERE%config.json"
