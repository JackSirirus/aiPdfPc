# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""翻译引擎与宿主之间的传输层：NDJSON 分块、单行上限与原子输出。

与现有协议的关系
----------------
现有工具仍是「stdin 单行 JSON 请求 → stdout 单行 JSON 结果」：宿主
`EngineBridge.ParseResult`（Sources/AIPDF.Windows/Bridge/EngineBridge.cs）从 stdout
行末向前扫描，取最后一条能解析为含 `"ok"` 的 JSON 对象，跳过损坏行与通知行。
翻译链路在此之上叠加 NDJSON 分块：抽取/翻译过程中每个分块作为独立 JSON 行写入
stdout，最后仍由 `engine.main()` 打印唯一含 `"ok"` 的单行结果。分块行不带 `"ok"`，
向前扫描不会误取；非 JSON 行也被跳过，所以既有 32 个工具的协议不受影响。

T10 在 engine.py 的集成点（本文件不改动 engine.py）
--------------------------------------------------
- 导入：在 engine.py 顶部或 dispatch 内 `from translate import bridge`。
- `dispatch()`（约 1398-1449）在翻译分支用
  `bridge.write_ndjson_chunk(sys.stdout, {...})` 逐块输出抽取/翻译结果；单块超过
  `MAX_CHUNK_BYTES` 时自动改写 oversize 哨兵，流继续。
- `main()`（约 1461-1479）的 `print(json.dumps(result, ensure_ascii=False))` 保持为
  最后一行，即宿主向后扫描命中的单行结果；bridge 只在其之前追加 NDJSON 行。
- 输出落盘（mono/dual PDF、sidecar）用 `bridge.atomic_write()` 或
  `bridge.atomic_replace()`：崩在 replace 之前不会留下半成品给宿主打开。

单行上限（需求总结.md §宿主↔引擎桥接）
------------------------------------
任一行 JSON 不超过 1 MB，避免多 MB 单行造成宿主内存峰值与管道死锁。抽取每块序列化
后超过 `MAX_CHUNK_BYTES` 时，改写为
`{"error": "extraction_chunk_oversize", "chunk_id": N}` 哨兵行，该页降级处理，流不断。

本模块只依赖标准库：单独导入 bridge 不需要 PyMuPDF；只有触发
`PermissionError → TargetInUseError` 时才延迟导入 engine，保证 bridge 被 engine
导入时不产生循环依赖。
"""
from __future__ import annotations

import io
import json
import os
from pathlib import Path
import sys
import uuid

# 单行 JSON 的硬上限（字节）。超过则改写为 oversize 哨兵，见模块 docstring。
MAX_CHUNK_BYTES = 1_000_000

# 与 engine.py 一致：同目录原子替换的临时文件用这个前缀，便于 engine 启动清扫
# （cleanup_temp_dirs）在已登记目录里识别残留。
TEMP_PREFIX = ".aipdf_tmp_"

_OVERSIZE_ERROR = "extraction_chunk_oversize"


def _serialize(chunk):
    """紧凑序列化：分块是线上协议，省掉多余空格可降低触顶概率。"""
    return json.dumps(chunk, ensure_ascii=False, separators=(",", ":"))


def _open_writer(file_or_handle):
    """返回 (handle, should_close)。路径按 UTF-8 追加打开；已是流则原样使用。"""
    if isinstance(file_or_handle, (str, os.PathLike)):
        return open(file_or_handle, "ab"), True
    return file_or_handle, False


def _open_reader(file_or_handle):
    """返回 (handle, should_close)。路径按 UTF-8 文本打开；已是流则原样使用。"""
    if isinstance(file_or_handle, (str, os.PathLike)):
        return open(file_or_handle, "r", encoding="utf-8"), True
    return file_or_handle, False


def _write_bytes(handle, payload):
    """写入 UTF-8 字节；文本流（engine stdout 已 pin 为 UTF-8）写解码后的字符串。"""
    if isinstance(handle, io.TextIOBase):
        handle.write(payload.decode("utf-8"))
    else:
        handle.write(payload)


def _tell(handle):
    """可定位流返回当前位置，否则 None（管道/字符流不可 seek）。"""
    try:
        return handle.tell()
    except (OSError, ValueError, AttributeError):
        return None


def _seek(handle, position):
    if position is None:
        return
    try:
        handle.seek(position)
    except (OSError, ValueError, AttributeError):
        pass


def write_ndjson_chunk(file_or_handle, chunk_dict):
    """写一行 NDJSON（JSON + `\\n`），返回写入字节数（含换行）。

    `chunk_dict` 序列化后超过 `MAX_CHUNK_BYTES` 时不写原文，改写 oversize 哨兵行，
    保证单块永远不会撑爆宿主内存或阻塞管道；调用方应继续发送后续块。
    """
    payload = _serialize(chunk_dict).encode("utf-8")
    if len(payload) > MAX_CHUNK_BYTES:
        chunk_id = chunk_dict.get("chunk_id") if isinstance(chunk_dict, dict) else None
        payload = _serialize({"error": _OVERSIZE_ERROR, "chunk_id": chunk_id}).encode("utf-8")
    line = payload + b"\n"
    handle, should_close = _open_writer(file_or_handle)
    try:
        _write_bytes(handle, line)
        handle.flush()
    finally:
        if should_close:
            handle.close()
    return len(line)


def read_ndjson_chunks(file_or_handle):
    """从 NDJSON 流逐个产出解析后的 JSON 对象。

    遇到无法解析的行（含第三方库通知、损坏的半行），或遇到带 `"ok"` 的单行结果
    （引擎最终结果行）即停止；这两类都是流的终止符，不作为分块产出。若底层流可
    定位，终止行会被回退，保留给调用方随后读取——单行 JSON 结果不被吞掉。
    """
    handle, should_close = _open_reader(file_or_handle)
    try:
        while True:
            start = _tell(handle)
            raw = handle.readline()
            if not raw:
                return
            text = raw.decode("utf-8", "replace") if isinstance(raw, bytes) else raw
            line = text.strip()
            if not line:
                continue
            try:
                item = json.loads(line)
            except ValueError:
                _seek(handle, start)
                return
            if not isinstance(item, dict):
                _seek(handle, start)
                return
            if "ok" in item:
                _seek(handle, start)
                return
            yield item
    finally:
        if should_close:
            handle.close()


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
