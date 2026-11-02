"""夜间成长任务包装（计划任务调用 nightly-growth.bat → 本文件）。

为什么需要包装：Yuno 的 tools.py 与服务一样存在"环境变量必须先于模块导入"的
顺序问题（YUNO_DB_BACKEND、DeepSeek key），这里加载 .env 后再以子进程运行
各子命令。顺序：backup → memory-grow（巩固/索引/修剪）→ memory-sleep（睡眠/REM）。
"""
import os
import subprocess
import sys
from datetime import datetime
from pathlib import Path

from dotenv import load_dotenv

ROOT = Path(__file__).resolve().parent
load_dotenv(ROOT / ".env", override=True)
os.environ.setdefault("YUNO_DB_BACKEND", "sqlite")

PY = str(ROOT / ".venv" / "Scripts" / "python.exe")


def run(cmd: list) -> int:
    print(f"\n===== {datetime.now():%Y-%m-%d %H:%M:%S} tools.py {' '.join(cmd)} =====", flush=True)
    return subprocess.run([PY, str(ROOT / "tools.py"), *cmd], cwd=str(ROOT)).returncode


def refresh_cards() -> None:
    """印象卡增量刷新兜底（after_chat 自动触发是主路径）。
    走运行中服务的 /amy/refresh_cards；服务不在线则跳过，不算夜间任务失败。"""
    import urllib.request
    token = os.getenv("YUNO_API_TOKEN", "")
    req = urllib.request.Request(
        "http://127.0.0.1:8457/amy/refresh_cards?scope=group:123456789&force=false",
        method="POST", headers={"Authorization": f"Bearer {token}"})
    try:
        with urllib.request.urlopen(req, timeout=600) as r:
            print(f"\n===== 印象卡刷新 =====\n{r.read().decode()[:800]}", flush=True)
    except Exception as e:
        print(f"\n===== 印象卡刷新跳过（服务不在线或失败）：{e} =====", flush=True)


def main() -> int:
    rc = 0
    for cmd in (["backup"], ["memory-grow"], ["memory-sleep"]):
        try:
            rc |= run(cmd)
        except Exception as e:
            print(f"[nightly] {cmd} 异常: {e}", flush=True)
            rc |= 1
    refresh_cards()
    print(f"[nightly] 完成 rc={rc}", flush=True)
    return rc


if __name__ == "__main__":
    sys.exit(main())
