# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""翻译引擎与宿主之间的传输层：单行 JSON 协议与原子输出。

与现有协议的关系
----------------
现有工具（含翻译）仍是「stdin 单行 JSON 请求 → stdout 单行 JSON 结果」：宿主
`EngineBridge.ParseResult`（Sources/AIPDF.Windows/Bridge/EngineBridge.cs）从 stdout
行末向前扫描，取最后一条能解析为含 `"ok"` 的 JSON 对象，跳过损坏行与通知行。
`engine.main()` 只打印唯一含 `"ok"` 的单行结果；翻译的抽取/翻译/回填结果整体放入
该结果的 `results` 字段，不再另发中间行。

关于早期的 NDJSON 分块协议（已移除）
------------------------------------
本模块曾提供 `write_ndjson_chunk` / `read_ndjson_chunks`，设计为「T10 流式分块」：
抽取/翻译过程中逐块把独立 JSON 行写到 stdout。但 `engine.py` 从未接入——整篇翻译
在内部完成抽取→翻译→回填后才由 `main()` 输出单行结果，宿主也没有流式消费分块
（`EngineBridge` 只读 stdout 的末行结果）。这些悬空函数因此是死代码，已删除。

决策（G）：选择「移除」而非「接线」，因为接线需要同时改动引擎编排、stdout 协议与
宿主消费端，属更大范围的协议变更，与本次加固目标不符。若将来确实需要流式进度，
应把发送端与宿主消费端一并设计并接入，而不是留下无人调用的实现。此处只保留确实
在用的原子输出（`atomic_write` / `atomic_replace`）与临时文件常量。

输出落盘（mono/dual PDF、降级报告）用 `bridge.atomic_write()` 或
`bridge.atomic_replace()`：崩在 replace 之前不会留下半成品给宿主打开。

本模块只依赖标准库：单独导入 bridge 不需要 PyMuPDF；只有触发
`PermissionError → TargetInUseError` 时才延迟导入 engine，保证 bridge 被 engine
导入时不产生循环依赖。
"""
from __future__ import annotations

import os
from pathlib import Path
import sys
import uuid

# 与 engine.py 一致：同目录原子替换的临时文件用这个前缀，便于 engine 启动清扫
# （cleanup_temp_dirs）在已登记目录里识别残留。
TEMP_PREFIX = ".aipdf_tmp_"


def _fsync_file(path):
    # 与 engine.py 同：Windows 上 O_RDONLY 句柄 fsync 会 EBADF，必须 O_RDWR。
    fd = os.open(str(path), os.O_RDWR)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def _fsync_dir(folder):
    # Windows 无法对目录句柄 fsync；os.replace 本身已原子，此处仅 POSIX 额外持久化。
    if sys.platform == "win32":
        return
    try:
        fd = os.open(str(folder), os.O_RDONLY)
    except OSError:
        return
    try:
        os.fsync(fd)
    except OSError:
        pass
    finally:
        os.close(fd)


def _discard(path):
    """尽力删除临时文件；被锁或已不存在都忽略。"""
    try:
        path.unlink()
    except OSError:
        pass


def _target_in_use_error():
    """延迟导入 engine 的 TargetInUseError。

    bridge 在 engine 内被导入时避免循环；单独使用 bridge（测试）时也不引入 PyMuPDF。
    必须复用 engine 的类：`engine._failure_result` 只在异常是 `UserError` 子类时才写
    `errorCode`，宿主据此走「另存为」回退。
    """
    from engine import TargetInUseError

    return TargetInUseError()


def atomic_replace(temp_path, final_path):
    """把已写好的同目录临时文件原子替换为最终文件。

    顺序：fsync(temp) → os.replace(temp, final) → fsync(目录，仅 POSIX)。
    `final_path` 只会被观测到旧内容或新内容，绝不会是半成品。Windows 上目标被其他
    进程以无 FILE_SHARE_DELETE 打开时，PermissionError 转成 engine 的
    `TargetInUseError`（code=target_in_use），宿主据此提示而不再盲目重试。任何失败都
    尽力删除临时文件，避免 `.aipdf_tmp_*` 残留。
    """
    temporary = Path(temp_path)
    target = Path(final_path)
    try:
        _fsync_file(temporary)
        os.replace(str(temporary), str(target))
    except PermissionError as error:
        raise _target_in_use_error() from error
    finally:
        # 与 engine._atomic_save_in_place 一致：成功路径 temp 已被 replace 移走（unlink
        # 失败即忽略），失败路径尽力删除，避免 `.aipdf_tmp_*` 残留。
        _discard(temporary)
    _fsync_dir(target.parent)
    return target


def atomic_write(final_path, data, encoding="utf-8"):
    """把 bytes/str 写入同目录临时文件，再由 `atomic_replace` 原子替换 `final_path`。

    temp 与 final 同目录，`os.replace` 不跨文件系统；崩在 replace 之前时 final 保持
    旧内容。返回最终路径。
    """
    target = Path(final_path)
    payload = data.encode(encoding) if isinstance(data, str) else bytes(data)
    temporary = target.parent / (TEMP_PREFIX + uuid.uuid4().hex)
    try:
        with open(temporary, "wb") as handle:
            handle.write(payload)
    except OSError:
        _discard(temporary)
        raise
    return atomic_replace(temporary, target)
