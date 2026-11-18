#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""跑安卓端的单元测试。

工程放在带中文的路径里（E:\\我的大学资料库\\...），Gradle 起的测试进程按系统
默认编码去解类路径上的中文，解不出来就报 ClassNotFoundException——连类都加载
不到，不是代码的问题。绕法是把工程复制到纯 ASCII 路径下再跑。

    python run_tests.py            # 跑全部单测
    python run_tests.py --keep     # 跑完保留影子目录，方便看报告

单独构建 APK 不用这个脚本，直接 gradle assembleDebug 就行。
"""

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

PROJECT = Path(__file__).resolve().parent
JAVA_HOME = os.environ.get("JAVA_HOME") or r"F:\devenv\JDK17"
GRADLE = shutil.which("gradle")
SKIP = {"build", ".gradle", ".kotlin", ".idea"}


def copy_project(target: Path) -> None:
    for item in PROJECT.iterdir():
        if item.name in SKIP:
            continue
        if item.is_dir():
            shutil.copytree(item, target / item.name, ignore=shutil.ignore_patterns(*SKIP))
        else:
            shutil.copy2(item, target / item.name)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--keep", action="store_true", help="跑完保留影子目录")
    args = parser.parse_args()

    if not GRADLE:
        print("找不到 gradle，先把它加进 PATH（或设置 GRADLE_HOME）", file=sys.stderr)
        return 2

    work = Path(tempfile.mkdtemp(prefix="dida-android-test-"))
    shadow = work / "android"
    try:
        copy_project(shadow)
        env = dict(os.environ)
        env["JAVA_HOME"] = JAVA_HOME
        proc = subprocess.run(
            [GRADLE, "--offline", "testDebugUnitTest"],
            cwd=shadow,
            env=env,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        if proc.returncode == 0:
            print("单测通过")
        return proc.returncode
    finally:
        if args.keep:
            print("影子目录留在：", work)
        else:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
