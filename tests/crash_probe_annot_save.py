# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""H-10 崩溃点注入回归：批注就地原子保存（backend/engine.py `_atomic_save_in_place`）。

在以下三个真实崩溃点用 `os._exit` 硬杀子进程（不可捕获，模拟断电/强杀）：
  A. after_temp          —— temp 已写入并通过校验，尚未 fsync/replace；
  B. before_replace      —— temp 已 fsync，即将调用 os.replace；
  C. after_replace       —— os.replace 已完成，尚未 fsync 目录/清理。

每个崩溃点断言：
  * A/B：原文件字节完全不变（SHA-256 相同）；
  * C ：目标文件已是可打开的有效 PDF（页数/基础文本保留，注释已写入）；
  * A/B/C：残留 `.aipdf_tmp_*` 的可清理性 —— 用 `cleanup_temp_dirs(max_age=0)` 清扫后归零。

本脚本可独立复跑（F3）：
    .venv\\Scripts\\python.exe tests\\crash_probe_annot_save.py
合成文件仅用于本次运行，不读取个人文档；全程使用临时目录与临时 AIPDF_STATE_DIR。
"""
from __future__ import annotations

import glob
import hashlib
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
ENGINE = REPO / "backend" / "engine.py"
TEMP_PREFIX = ".aipdf_tmp_"

MODE_EXITS = {"after_temp": 11, "before_replace": 12, "after_replace": 13}


def _load_engine():
    backend = str(REPO / "backend")
    if backend not in sys.path:
        sys.path.insert(0, backend)  # engine.py: from catalog import BY_ID
    spec = importlib.util.spec_from_file_location("aipdf_engine_probe", str(ENGINE))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _make_base_pdf(path: Path) -> None:
    import fitz

    doc = fitz.open()
    page = doc.new_page(width=595, height=842)
    page.insert_text((72, 72), "crash injection base content 0123456789")
    doc.save(str(path), garbage=4, deflate=True)
    doc.close()


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _residue(folder: Path):
    return sorted(Path(p).name for p in glob.glob(str(folder / (TEMP_PREFIX + "*"))))


def _child(mode: str, target: str) -> int:
    engine = _load_engine()

    if mode == "after_temp":
        # temp 写入成功、校验前硬杀：验证「半成品 temp 绝不进入源文件」的中间态。
        engine._validate_saved_temp = lambda *a, **k: os._exit(MODE_EXITS["after_temp"])
    elif mode == "before_replace":
        real_fsync = engine._fsync_file

        def fsync_then_die(path):
            real_fsync(path)
            os._exit(MODE_EXITS["before_replace"])

        # _fsync_file(temp) 紧随校验、os.replace 之前，只作用于 temp 路径。
        engine._fsync_file = fsync_then_die
    elif mode == "after_replace":
        real_fsync_dir = engine._fsync_dir

        def fsync_dir_then_die(folder):
            real_fsync_dir(folder)
            os._exit(MODE_EXITS["after_replace"])

        # os.replace 之后的下一个动作；此时 temp 已重命名为目标。
        engine._fsync_dir = fsync_dir_then_die
    elif mode == "cleanup":
        removed = engine.cleanup_temp_dirs(max_age=0)
        print(f"cleanup removed={removed}", file=sys.stderr)
        return 0
    else:
        raise SystemExit(f"unknown mode: {mode}")

    request = {
        "operation": "save_annotations",
        "files": [target],
        "options": {
            "annotations": [
                {"action": "add", "id": "crash-h1", "kind": "highlight",
                 "page": 0, "rect": [72, 72, 320, 90]},
            ]
        },
    }
    engine.dispatch(request)
    return 0  # 正常路径不应到达（注入点必杀）；到达即视为注入失败。


def _run(mode: str, target: Path, state_dir: Path):
    env = dict(os.environ)
    env["AIPDF_STATE_DIR"] = str(state_dir)
    env["PYTHONUTF8"] = "1"
    proc = subprocess.run(
        [sys.executable, str(Path(__file__).resolve()), "--child", mode, str(target)],
        capture_output=True, text=True, encoding="utf-8", errors="replace", env=env, timeout=180,
    )
    return proc


def main() -> int:
    work = Path(tempfile.mkdtemp(prefix="aipdf-h10-crash-"))
    state = Path(tempfile.mkdtemp(prefix="aipdf-h10-state-"))
    base = work / "base.pdf"
    _make_base_pdf(base)

    report = []
    report.append("H-10 崩溃点注入回归 — 批注就地原子保存 (backend/engine.py: _atomic_save_in_place)")
    report.append(f"时间: {time.strftime('%Y-%m-%d %H:%M:%S')}")
    report.append(f"合成源 PDF: {base}  sha256={_sha256(base)[:16]}  bytes={base.stat().st_size}")
    report.append("")

    failures = []
    for mode in ("after_temp", "before_replace", "after_replace"):
        target = work / f"{mode}.pdf"
        target.write_bytes(base.read_bytes())
        before_sha = _sha256(target)

        proc = _run(mode, target, state)
        rc = proc.returncode
        residue_before = _residue(work)

        check_original_intact = None
        check_target_changed = None
        detail = ""
        if mode in ("after_temp", "before_replace"):
            after_sha = _sha256(target)
            check_original_intact = after_sha == before_sha
            detail = f"原文件 SHA 不变={after_sha == before_sha}"
            if not check_original_intact:
                failures.append(f"{mode}: 原文件被改动")
        else:
            import fitz

            try:
                doc = fitz.open(str(target))
                pages = doc.page_count
                text = doc[0].get_text() or ""
                annots = sum(1 for p in doc for _ in (p.annots() or []))
                doc.close()
                check_target_changed = (
                    pages == 1 and "crash injection base content" in text and annots >= 1
                )
                detail = f"目标可开：pages={pages} 基础文本保留={'crash injection base content' in text} 注释数={annots}"
            except Exception as error:  # noqa: BLE001
                check_target_changed = False
                detail = f"目标损坏：{error}"
            if not check_target_changed:
                failures.append(f"{mode}: replace 后目标不是有效完整 PDF")

        expected_rc = MODE_EXITS[mode]
        rc_ok = rc == expected_rc
        if not rc_ok:
            failures.append(f"{mode}: 子进程退出码 {rc} != 期望 {expected_rc}（注入未命中）")

        report.append(f"[{mode}] exit={rc}（期望 {expected_rc}）{'OK' if rc_ok else 'FAIL'}；{detail}")
        if not rc_ok and proc.stderr.strip():
            report.append("    child stderr: " + proc.stderr.strip().replace("\n", " | ")[-500:])
        report.append(f"    残留 temp: {residue_before if residue_before else '（无）'}")

        # 残留可清理性：强制清扫（max_age=0）后应归零。
        if residue_before:
            cleanup = _run("cleanup", target, state)
            residue_after = _residue(work)
            cleanable = not residue_after
            report.append(
                f"    cleanup_temp_dirs(max_age=0) exit={cleanup.returncode} 清理后残留={residue_after or '（无）'} "
                f"{'OK' if cleanable else 'FAIL'}"
            )
            if not cleanable:
                failures.append(f"{mode}: 残留 temp 无法清理")
        report.append("")

    # 状态目录登记：确认崩溃目录被登记（下次启动可清扫）。
    registry = state / "tmp-dirs.json"
    report.append(f"tmp-dirs.json 存在={registry.is_file()}")
    if registry.is_file():
        try:
            dirs = json.loads(registry.read_text(encoding="utf-8"))
            report.append(f"tmp-dirs.json 内容={dirs}")
        except ValueError:
            report.append("tmp-dirs.json 解析失败（已在清理后重写为空）")

    report.append("")
    report.append("结论：" + ("PASS（三个崩溃点原文件/目标完整，残留可清理）" if not failures else "FAIL"))
    for failure in failures:
        report.append("  - " + failure)

    text = "\n".join(report) + "\n"
    out = REPO / ".omo" / "evidence" / "v2-task12-crash.txt"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(text, encoding="utf-8")
    print(text)
    return 1 if failures else 0


if __name__ == "__main__":
    if len(sys.argv) >= 4 and sys.argv[1] == "--child":
        raise SystemExit(_child(sys.argv[2], sys.argv[3]))
    raise SystemExit(main())
