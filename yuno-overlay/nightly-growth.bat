@echo off
rem Amy 记忆夜间成长：备份 + 巩固/索引/修剪 + 睡眠（计划任务每日 03:30 调用）
cd /d "%~dp0"
if not exist logs mkdir logs
.venv\Scripts\python nightly_grow.py >> logs\nightly.log 2>&1
