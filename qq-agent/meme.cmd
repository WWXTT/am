@echo off
chcp 65001 >nul
rem meme lookup: curated -> CHIME vectors -> knowthismeme MCP (--web). ASCII only; keep CRLF.
rem usage: qq-agent\meme.cmd "term"        qq-agent\meme.cmd "doge" --web
"%~dp0..\yuno\.venv\Scripts\python.exe" "%~dp0tools\meme-lookup.py" %*
