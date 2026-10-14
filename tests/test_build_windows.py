# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""构建脚本端到端冒烟（冒烟清单 #1）的可重复 unittest 包装。

对应 docs/手动冒烟清单-Windows.md 第 11-16 行「#1 构建脚本正常完成」：
退出码 0、dist/aipdf-windows/ 含 app/engine/helper 子目录与关键产物。

本文件采用「布局断言 + 手动 e2e 证据」拆分设计：
  * 默认单测（本模块全部用例）只做 dist 布局断言与 e2e 证据文件校验，
    秒级完成，可重复运行，不触发全量构建；
  * e2e 全量构建（成本 = 单次全量 scripts/build.ps1，本次实测约 108 秒，
    缓存冷态下含 pip/playwright/dotnet publish/wheel 下载会显著更久）
    已由 Task 9 手动执行一次，证据落在
    `.omo/evidence/test-gaps/task-9-build-e2e.txt`（UTF-16LE，PowerShell
    Out-File -Encoding Unicode 写出；用 Get-Content -Encoding Unicode 读）。
    证据含 COMMAND / EXIT_CODE / DURATION_SECONDS / 逐项 Test-Path /
    VERDICT。
  * 设置环境变量 AIPDF_BUILD_E2E=1 时，额外跑 test_full_build_subprocess
    （子进程全量调用 powershell -ExecutionPolicy Bypass -File
    scripts\\build.ps1，timeout 15 分钟 = 900000ms）。

断言清单镜像 scripts/build.ps1 真实产物，勿猜路径：
  * stage [6/7] $required 8 项（build.ps1:328-337）
  * stage [7/7] $requiredStage7 7 项（build.ps1:457-465）
"""
import os
import re
import subprocess
import time
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DIST = REPO / "dist" / "aipdf-windows"
BUILD_PS1 = REPO / "scripts" / "build.ps1"
EVIDENCE = REPO / ".omo" / "evidence" / "test-gaps" / "task-9-build-e2e.txt"
# 单测层不做全量构建（e2e 成本 = 单次全量 build）；显式 opt-in。
BUILD_TIMEOUT_SECONDS = 900  # ≥15 分钟，命令层给足
RUN_FULL_BUILD = os.environ.get("AIPDF_BUILD_E2E") == "1"

# build.ps1 stage [6/7]：基础布局断言（行 328-337）。
REQUIRED_STAGE6 = (
    "app/AIPDF.Windows.exe",
    "app/AIPDF.Windows.dll",
    "app/catalog.json",
    "engine/backend/engine.py",
    "engine/backend/catalog.py",
    "engine/backend/catalog.json",
    "helper/WindowsOcrHelper.exe",
    "README-Windows.txt",
)

# build.ps1 stage [7/7]：自包含布局断言（行 457-465）。
REQUIRED_STAGE7 = (
    "engine/python/python.exe",
    "engine/python/python314._pth",
    "engine/python/python314.zip",
    "engine/site-packages/fitz",
    "engine/site-packages/PIL",
    "engine/site-packages/playwright",
    "app/hostfxr.dll",
)


def read_evidence_text(path: Path) -> str:
    """读证据文件，容忍 PS5.1 UTF-16LE 与 UTF-8 两种落盘编码。"""
    raw = path.read_bytes()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16")
    return raw.decode("utf-8", errors="replace")


class BuildLayoutTests(unittest.TestCase):
    """dist 布局断言：镜像 build.ps1 内置的两组 required 清单。"""

    def test_dist_layout_stage6_critical_files(self):
        """stage [6/7] 8 项关键文件在位（冒烟 #1：app/engine/helper 三件套）。"""
        missing = [
            rel for rel in REQUIRED_STAGE6 if not (DIST / rel).is_file()
        ]
        self.assertEqual(
            [],
            missing,
            f"dist/aipdf-windows 缺少 stage6 关键产物: {missing}",
        )

    def test_dist_layout_stage7_self_contained_files(self):
        """stage [7/7] 7 项自包含产物在位（嵌入式 Python + site-packages + hostfxr）。"""
        missing = [
            rel for rel in REQUIRED_STAGE7 if not (DIST / rel).exists()
        ]
        self.assertEqual(
            [],
            missing,
            f"dist/aipdf-windows 缺少 stage7 自包含产物: {missing}",
        )

    def test_dist_has_app_engine_helper_subdirs(self):
        """冒烟 #1 口径：dist/aipdf-windows/ 含 app/engine/helper 三个子目录。"""
        for sub in ("app", "engine", "helper"):
            self.assertTrue(
                (DIST / sub).is_dir(),
                f"dist/aipdf-windows/{sub} 子目录不存在",
            )


class BuildEvidenceTests(unittest.TestCase):
    """e2e 证据文件校验（路径见模块 docstring）。"""

    def test_evidence_file_exists_and_records_pass(self):
        """证据文件存在，且 EXIT_CODE=0 / VERDICT=PASS / 15 项 Test-Path 全 True。"""
        self.assertTrue(
            EVIDENCE.is_file(),
            f"缺少 e2e 证据文件: {EVIDENCE}（见模块 docstring，Task 9 手动全量构建产出）",
        )
        text = read_evidence_text(EVIDENCE)
        exit_match = re.search(r"EXIT_CODE:\s*(\d+)", text)
        self.assertIsNotNone(exit_match, "证据缺少 EXIT_CODE 行")
        self.assertEqual("0", exit_match.group(1), "e2e 构建退出码非 0")
        self.assertIn("VERDICT: PASS", text)
        self.assertRegex(text, r"DURATION_SECONDS:\s*[\d.]+")
        false_paths = re.findall(r"Test-Path \S+ => False", text)
        self.assertEqual([], false_paths, f"证据中存在失败的 Test-Path: {false_paths}")


@unittest.skipUnless(
    RUN_FULL_BUILD,
    "e2e 全量构建成本 = 单次全量 scripts/build.ps1；"
    "默认跳过，设 AIPDF_BUILD_E2E=1 显式启用，"
    "已产出证据见 .omo/evidence/test-gaps/task-9-build-e2e.txt",
)
class FullBuildE2ETests(unittest.TestCase):
    """全量 build.ps1 端到端（opt-in，AIPDF_BUILD_E2E=1 才跑）。"""

    def test_full_build_subprocess_exit_zero(self):
        """子进程全量跑 build.ps1：退出码 0 且 stage6+7 产物齐全。"""
        self.assertTrue(BUILD_PS1.is_file(), f"未找到 {BUILD_PS1}")
        env = dict(os.environ)
        env["DOTNET_CLI_UI_LANGUAGE"] = "en-US"
        started = time.monotonic()
        proc = subprocess.run(
            [
                "powershell",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(BUILD_PS1),
            ],
            cwd=str(REPO),
            env=env,
            capture_output=True,
            timeout=BUILD_TIMEOUT_SECONDS,  # 900000ms 等效：900s
        )
        elapsed = time.monotonic() - started
        tail = (proc.stdout + proc.stderr)[-4000:]
        self.assertEqual(
            0,
            proc.returncode,
            f"build.ps1 退出码 {proc.returncode}（{elapsed:.1f}s），输出尾部:\n{tail}",
        )
        missing = [
            rel
            for rel in REQUIRED_STAGE6 + REQUIRED_STAGE7
            if not (DIST / rel).exists()
        ]
        self.assertEqual([], missing, f"全量构建后仍缺少产物: {missing}")


if __name__ == "__main__":
    unittest.main()
