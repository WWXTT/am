@echo off
rem Amy 记忆服务（Yuno）一键启动：127.0.0.1:8457，彻底无窗（机制见 start_hidden.py）
rem 已在运行则直接退出（端口探活），否则 detached 拉起并写日志到 logs\service.log
cd /d "%~dp0"
python start_hidden.py
