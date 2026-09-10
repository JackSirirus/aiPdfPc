#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""验证工具通道映射表与 catalog.json 的一致性。

用法：python 设计/v1.0-阅读器/prd/verify-mapping.py
退出码：0 = 全部通过，非零 = 存在问题。
"""
import json
import re
import sys
from pathlib import Path

# 使用脚本所在目录的绝对路径拼接，避免 Windows 中文路径编码问题
_SCRIPT_DIR = Path(__file__).resolve().parent
_CATALOG = _SCRIPT_DIR / ".." / ".." / ".." / "backend" / "catalog.json"
CATALOG = _CATALOG.resolve()
MAPPING_TABLE = _SCRIPT_DIR / "01-工具通道映射表.md"
VALID_CHANNELS = {"起始页", "临时标签", "向导", "右抽屉"}


def load_catalog_ids() -> list[str]:
    """从 catalog.json 读取全部工具 ID（保序）。"""
    data = json.loads(CATALOG.read_text(encoding="utf-8"))
    return [t["id"] for t in data]


def parse_mapping_table() -> dict[str, str]:
    """从 Markdown 表格中提取 {工具ID: 通道} 映射。"""
    lines = MAPPING_TABLE.read_text(encoding="utf-8").splitlines()
    mapping: dict[str, str] = {}
    for line in lines:
        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.split("|")]
        # cells[0] 和 cells[-1] 是行首/尾的空字符串
        cells = [c for c in cells if c]
        if len(cells) < 4:
            continue
        # 跳过表头和分隔行
        if cells[0] == "工具ID" or all(c.startswith("-") for c in cells):
            continue
        tool_id = cells[0]
        channel = cells[3]  # 第4列是通道
        mapping[tool_id] = channel
    return mapping


def main() -> int:
    errors: list[str] = []

    # 1. 读取 catalog 全部 ID
    catalog_ids = load_catalog_ids()
    if len(catalog_ids) != 32:
        errors.append(f"catalog.json 应有 32 个工具，实际 {len(catalog_ids)} 个")
    catalog_set = set(catalog_ids)

    # 2. 解析映射表
    mapping = parse_mapping_table()
    mapped_ids = set(mapping.keys())

    # 3. 检查是否全部覆盖
    missing = catalog_set - mapped_ids
    extra = mapped_ids - catalog_set
    if missing:
        errors.append(f"未映射的工具 ({len(missing)}): {sorted(missing)}")
    if extra:
        errors.append(f"映射表中多余的工具 ({len(extra)}): {sorted(extra)}")

    # 4. 检查通道值合法性
    for tool_id, channel in mapping.items():
        if channel not in VALID_CHANNELS:
            errors.append(f"{tool_id}: 通道 '{channel}' 不合法，应为 {sorted(VALID_CHANNELS)}")

    # 5. 输出结果
    mapped_count = len(mapped_ids & catalog_set)
    unmapped_count = len(missing)
    if errors:
        print("验证失败：")
        for e in errors:
            print(f"  [FAIL] {e}")
        print(f"\n{mapped_count}/{len(catalog_ids)} mapped, {unmapped_count} unmapped")
        return 1

    print(f"{mapped_count}/{len(catalog_ids)} mapped, {unmapped_count} unmapped")
    return 0


if __name__ == "__main__":
    sys.exit(main())
