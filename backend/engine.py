# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""Local PDF worker. One JSON request on stdin, one JSON result on stdout.

Local documents stay on this Mac. URL conversion explicitly fetches web content.
Each run owns a fresh output directory; input files are opened read-only.
"""
from __future__ import annotations

import difflib
from contextlib import redirect_stdout
import html
import io
import ipaddress
import json
import locale
import math
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
from urllib.parse import urlsplit
import uuid

import pymupdf as fitz
from PIL import Image, ImageChops, ImageEnhance, ImageOps
from catalog import BY_ID

if sys.platform == "win32":
    # The JSON protocol is UTF-8 end to end. Windows pipes and consoles default
    # to the legacy ANSI code page (e.g. cp936), which would mangle CJK paths
    # and messages, so pin stdio to UTF-8 on this platform only.
    for _stream in (sys.stdin, sys.stdout, sys.stderr):
        try:
            _stream.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError):
            pass


class UserError(Exception):
    pass


class CancelledError(UserError):
    """用户取消（SIGTERM/SIGINT 或 LLM 中止）。与普通失败区分：半成品任务目录会被清理，
    而不是像普通失败那样保留供排查。宿主据此 errorCode=`cancelled` 识别取消。"""

    code = "cancelled"


class InPlaceSaveError(UserError):
    """就地批注保存失败。携带机器可读 code，经 JSON 顶层 `errorCode` 传给 WPF 桥，
    使 WPF 能区分「目标被占用」并提示关闭其他程序/另存为，而非盲目重试。"""

    code = "inplace_save_failed"

    def __init__(self, message, code=None):
        super().__init__(message)
        if code:
            self.code = code


class TargetInUseError(InPlaceSaveError):
    """os.replace 目标被其他进程占用（Windows 无 FILE_SHARE_DELETE）时的可辨识错误。"""

    code = "target_in_use"

    def __init__(self):
        super().__init__("目标文件正被其他程序占用，无法写入。请关闭正在打开该文件的程序后重试，或另存为副本。", "target_in_use")


CHILDREN: list[subprocess.Popen] = []


def terminate_tree(proc):
    """Kill a helper and everything it spawned, not just the direct child."""
    if sys.platform == "win32":
        # taskkill /T walks the whole parent-child tree and force-terminates it;
        # os.killpg does not exist here and proc.terminate() would orphan grandchildren.
        detail = ""
        try:
            done = subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)], capture_output=True)
            if done.returncode == 0:
                return
            # taskkill emits console-ANSI text, not UTF-8.
            detail = (done.stderr or done.stdout).decode(locale.getpreferredencoding(False), errors="replace").strip()
        except FileNotFoundError:
            detail = "taskkill 不可用"
        print(f"taskkill 终止进程树失败（pid={proc.pid}）：{detail}", file=sys.stderr, flush=True)
        if proc.poll() is None:
            proc.terminate()
        return
    try:
        os.killpg(proc.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass


def stop(*_):
    for child in CHILDREN:
        if child.poll() is None:
            terminate_tree(child)
    raise CancelledError("处理已取消。已完成的文件保留在本次输出目录。")


def progress(message, fraction=0):
    print(json.dumps({"message": message, "progress": fraction}, ensure_ascii=False), file=sys.stderr, flush=True)


def run_process(args, timeout=180):
    env = None
    popen_flags = {}
    if sys.platform == "win32":
        # Helper processes must speak UTF-8 too, regardless of the console code page.
        env = {**os.environ, "PYTHONIOENCODING": "utf-8", "PYTHONUTF8": "1"}
        # A fresh process group is the Windows counterpart of start_new_session;
        # it keeps console Ctrl events away from helpers, whose tree stop()
        # addresses via taskkill instead.
        popen_flags["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    child = subprocess.Popen([str(a) for a in args], stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True, env=env, **popen_flags)
    CHILDREN.append(child)
    try:
        out, err = child.communicate(timeout=timeout)
        if sys.platform == "win32" and isinstance(out, bytes):
            # Consume helper output as UTF-8; stray non-UTF-8 bytes degrade to
            # U+FFFD instead of crashing JSON parsing.
            out = out.decode("utf-8", errors="replace")
        if child.returncode:
            raise UserError("本地转换引擎未能完成处理。请检查源文件是否可正常打开。")
        return out
    except subprocess.TimeoutExpired:
        terminate_tree(child)
        child.communicate()
        raise UserError("本地转换超时。请缩小文件后重试。")
    finally:
        CHILDREN.remove(child)


def _windows_office_registry_candidates():
    """从 LibreOffice UNO InstallPath 注册表键发现安装目录，覆盖自定义安装位置。"""
    try:
        import winreg
    except ImportError:  # 非 Windows 平台
        return []
    found = []
    for view in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
        for subkey in (r"SOFTWARE\LibreOffice\UNO\InstallPath",
                       r"SOFTWARE\WOW6432Node\LibreOffice\UNO\InstallPath"):
            try:
                with winreg.OpenKey(view, subkey) as key:
                    value = winreg.QueryValueEx(key, "")[0]
            except OSError:
                continue
            if value:
                found.append(str(Path(value) / "soffice.exe"))
    return found


def find_office():
    if sys.platform == "win32":
        # Windows：按优先级探测，找不到时返回 None，由上游提示用户安装。
        paths = [os.environ.get("AIPDF_OFFICE", "")]
        # 注册表发现，覆盖自定义安装目录（与 Install.ps1 Test-LibreOffice 保持一致）。
        paths += _windows_office_registry_candidates()
        paths += [r"C:\Program Files\LibreOffice\program\soffice.exe",
                  r"C:\Program Files (x86)\LibreOffice\program\soffice.exe"]
        paths += [shutil.which("soffice") or ""]
        return next((p for p in paths if p and Path(p).is_file()), None)
    paths = [os.environ.get("AIPDF_OFFICE", ""),
             "/Applications/LibreOffice.app/Contents/MacOS/soffice",
             str(Path.home() / ".cache/codex-runtimes/codex-primary-runtime/dependencies/native/libreoffice-headless/libreoffice/LibreOfficeDev.app/Contents/MacOS/soffice")]
    paths += [shutil.which("libreoffice") or "", shutil.which("soffice") or ""]
    return next((p for p in paths if p and Path(p).is_file()), None)


def vision_path():
    if sys.platform == "win32":
        # Windows：内置 Windows.Media.Ocr 的 C# helper（自包含，不依赖本机 .NET 运行时）。
        # Release 优先，Debug 兜底；自包含构建产物位于 win-x64 子目录。
        helper_dir = Path(__file__).resolve().parent.parent / "Sources" / "WindowsOcrHelper"
        for config in ("Release", "Debug"):
            exe = helper_dir / "bin" / config / "net8.0-windows10.0.19041.0" / "win-x64" / "WindowsOcrHelper.exe"
            if exe.is_file():
                return str(exe)
        # 安装布局：<install>/helper/WindowsOcrHelper.exe
        # engine.py 位于 <install>/engine/backend/engine.py → parents[2] 为安装根目录。
        install_helper = Path(__file__).resolve().parents[2] / "helper" / "WindowsOcrHelper.exe"
        if install_helper.is_file():
            return str(install_helper)
        return None
    paths = [os.environ.get("AIPDF_VISION", ""), str(Path(__file__).resolve().parent / "VisionHelper"),
             str(Path(__file__).resolve().parent.parent / ".build/release/VisionHelper")]
    return next((p for p in paths if p and Path(p).is_file()), None)


def web_path():
    if sys.platform == "win32":
        return None  # Windows helper 路径在接入网页渲染时补充。
    if vision_path():
        sibling=Path(vision_path()).with_name("WebHelper")
        if sibling.is_file(): return str(sibling)
    return None


def health():
    return {"ok": True, "office": bool(find_office()), "vision": bool(vision_path()),
            "engine": fitz.VersionBind}


def number(options, key, default, minimum=None, maximum=None):
    try:
        n = float(options.get(key, default))
    except (ValueError, TypeError):
        raise UserError(f"{key} 必须是数字。")
    if not math.isfinite(n) or (minimum is not None and n < minimum) or (maximum is not None and n > maximum):
        raise UserError(f"{key} 超出允许范围。")
    return n


def flag(options, key, default=False):
    return str(options.get(key, str(default))).lower() == "true"


def parse_pages(spec, total, duplicates=False):
    if not str(spec).strip():
        return list(range(total))
    result = []
    for item in str(spec).replace("，", ",").split(","):
        m = re.fullmatch(r"\s*(\d+)\s*(?:-\s*(\d+)\s*)?", item)
        if not m:
            raise UserError("页码格式不正确，请输入类似 1-3,5 的范围。")
        a, b = int(m[1]), int(m[2] or m[1])
        if not (1 <= a <= total and 1 <= b <= total):
            raise UserError(f"页码超出范围，这份文件共有 {total} 页。")
        result.extend(range(a - 1, b, 1) if a <= b else range(a - 1, b - 2, -1))
    return result if duplicates else list(dict.fromkeys(result))


def open_pdf(path, password=""):
    try:
        doc = fitz.open(path)
    except Exception:
        raise UserError(f"无法读取 {Path(path).name}。文件可能损坏或格式不受支持。")
    # Capture the encryption state before authenticate(): in PyMuPDF 1.28
    # `is_encrypted` flips to False once a password is accepted, and an
    # empty-user-password document never reports needs_pass. The in-place
    # annotation save uses this to preserve (or drop) encryption on save.
    doc._aipdf_encrypted = bool(doc.is_encrypted or doc.needs_pass)
    if not doc.is_pdf:
        doc.close()
        raise UserError("此工具需要 PDF 文件。")
    if doc.needs_pass and not doc.authenticate(password):
        doc.close()
        raise UserError(f"{Path(path).name} 需要正确的打开密码。")
    if not len(doc):
        doc.close()
        raise UserError("PDF 没有可处理的页面。")
    return doc


def inspect_file(path, password=""):
    with open_pdf(path, password) as doc:
        fields = []
        for page in doc:
            for widget in page.widgets() or []:
                fields.append({"name": widget.field_name, "value": str(widget.field_value or ""),
                               "type": widget.field_type_string, "page": page.number + 1,
                               "choices": widget.choice_values or [],
                               "states": widget.button_states() if widget.field_type in (2, 5) else {}})
        return {"ok": True, "pages": len(doc), "fields": fields}


def save_pdf(doc, path, **kwargs):
    if not len(doc):
        raise UserError("操作后没有剩余页面。")
    # Never append incrementally: redacted/encrypted outputs must not retain old revisions.
    doc.save(path, garbage=4, deflate=True, encryption=fitz.PDF_ENCRYPT_NONE, **kwargs)
    with fitz.open(path) as check:
        if check.page_count != len(doc):
            raise UserError("输出文件验证失败。")


def rgb(value):
    if not re.fullmatch(r"#[0-9A-Fa-f]{6}", value):
        raise UserError("颜色格式应为 #3159D9。")
    return tuple(int(value[i:i+2], 16) / 255 for i in (1, 3, 5))


def area(page, options):
    x, y = number(options, "x", 48, 0), number(options, "y", 72, 0)
    w, h = number(options, "width", 240, 1), number(options, "height", 64, 1)
    # All UI coordinates refer to the visible, rotated page, from its upper left.
    r = fitz.Rect(x, y, x+w, y+h)
    if not page.rect.contains(r):
        raise UserError("选择区域超出了页面，请在预览中重新框选。")
    return r * page.derotation_matrix


def insert_text(page, rect, text, size=18, color=(0, 0, 0), opacity=1, align=0):
    if not text:
        raise UserError("请输入文字内容。")
    if any(ord(c)>255 for c in text):
        css_color="rgb("+",".join(str(round(v*255)) for v in color)+")"
        alignment={0:"left",1:"center",2:"right"}.get(align,"left")
        css=f"* {{font-family:sans-serif;font-size:{size}pt;color:{css_color};text-align:{alignment};}} body {{margin:0;padding:0;}}"
        remaining,_=page.insert_htmlbox(rect,html.escape(text).replace("\n","<br>"),css=css,opacity=opacity,scale_low=1)
    else:
        remaining = page.insert_textbox(rect, text, fontsize=size, fontname="helv", color=color,
                                        fill_opacity=opacity, align=align)
    if remaining < 0:
        raise UserError("文字放不进所选区域，请扩大区域或减小字号。")


# --- V2 批注写回 + 就地原子保存 -------------------------------------------------
# 批注链路刻意不进 BY_ID（32 工具目录不变）：它就地写回源文件而非任务输出目录，
# 并且是唯一允许在保存时保留源文件加密的路径（用已知用户密码显式 AES-256 重加密）。
# 普通工具的 save_pdf 契约（输出到任务目录 + PDF_ENCRYPT_NONE）保持不变。
TEMP_PREFIX = ".aipdf_tmp_"
ANNOTATION_OPERATIONS = ("save_annotations", "add_highlight_annot", "add_freetext_annot",
                        "add_ink_annot", "add_shape_annot")
# 比这个更年轻的 temp 可能是并发的另一引擎进程（同目录不同文件）正在写，勿删。
_TEMP_STALE_SECONDS = 600


def _state_dir():
    override = os.environ.get("AIPDF_STATE_DIR")
    if override:
        return Path(override)
    if sys.platform == "win32":
        base = os.environ.get("APPDATA") or str(Path.home() / "AppData" / "Roaming")
        return Path(base) / "AIPDF"
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "AIPDF"
    return Path(os.environ.get("XDG_DATA_HOME") or (Path.home() / ".local" / "share")) / "AIPDF"


def _temp_registry_path():
    return _state_dir() / "tmp-dirs.json"


def _read_temp_dirs():
    try:
        data = json.loads(_temp_registry_path().read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return []
    return [str(item) for item in data if isinstance(item, str)] if isinstance(data, list) else []


def _write_temp_dirs(dirs):
    try:
        target = _temp_registry_path()
        target.parent.mkdir(parents=True, exist_ok=True)
        temporary = target.with_name(f"{target.name}.{uuid.uuid4().hex}.tmp")
        temporary.write_text(json.dumps(dirs, ensure_ascii=False), encoding="utf-8")
        os.replace(str(temporary), str(target))
    except OSError:
        pass


def _record_temp_dir(directory):
    """记录曾就地保存的目录，供下次启动清扫崩溃残留（M-4）。"""
    folder = str(Path(directory))
    dirs = _read_temp_dirs()
    if folder not in dirs:
        dirs.append(folder)
        _write_temp_dirs(dirs)


def cleanup_temp_dirs(max_age=_TEMP_STALE_SECONDS):
    """启动时清扫已记录目录中残留的 `.aipdf_tmp_*`。

    只删除早于 max_age 的文件：新写的 temp 可能属于并发引擎进程（同目录不同
    文件）。杀软/索引器瞬时锁导致删除失败时保留该目录登记，下次启动再扫（M-4）。
    """
    now = time.time()
    survivors, removed = [], 0
    for folder in _read_temp_dirs():
        directory = Path(folder)
        if not directory.is_dir():
            continue
        keep = False
        for leftover in directory.glob(TEMP_PREFIX + "*"):
            try:
                if (now - leftover.stat().st_mtime) < max_age:
                    keep = True  # 可能是在途 temp：保留登记，留待下次
                    continue
                leftover.unlink()
                removed += 1
            except FileNotFoundError:
                pass
            except OSError:
                keep = True  # 杀软/索引器锁：保留登记，下次重试
        if keep:
            survivors.append(folder)
    _write_temp_dirs(survivors)
    return removed


def _doc_is_encrypted(doc):
    captured = getattr(doc, "_aipdf_encrypted", None)
    if captured is not None:
        return bool(captured)
    return bool(doc.needs_pass or doc.is_encrypted)


def _fsync_file(path):
    # Windows 上 O_RDONLY 句柄 fsync 会 EBADF，必须 O_RDWR。
    fd = os.open(str(path), os.O_RDWR)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def _fsync_dir(folder):
    # H-3：Windows 无法对目录句柄 fsync（os.open(dir) 抛 PermissionError）。
    # 同目录 os.replace 本身原子，目录 fsync 是 POSIX 额外的持久化保障，仅在那里保留。
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


def _count_annots(doc):
    # annot_xrefs() 只读 /Annots 引用，不解码注释字符串。加密文档上必须用它：
    # PyMuPDF 1.28.2 读取 AES 文档的注释字符串时可能抛 aes padding/UnicodeDecodeError。
    return sum(len(page.annot_xrefs() or []) for page in doc)


def _validate_saved_temp(temp, expected_pages, expected_annots, encrypted, password):
    """替换前校验 temp 可重新打开，且页数、注释数与内存一致。

    加密文档额外用 pypdf 交叉校验注释数：MuPDF 1.28.2 读取 AES 注释字符串不可靠
    （PyMuPDF 侧缺陷，文件本身有效，pypdf 可用原用户密码解密）；页面数仍以 PyMuPDF 校验。
    """
    with fitz.open(str(temp)) as check:
        if check.needs_pass and password:
            check.authenticate(password)
        if check.page_count != expected_pages:
            raise UserError("保存校验失败：输出页数与内存不一致，原文件未改动。")
        if not encrypted and _count_annots(check) != expected_annots:
            raise UserError("保存校验失败：输出注释数与内存不一致，原文件未改动。")
    if encrypted:
        from pypdf import PdfReader
        reader = PdfReader(str(temp))
        if reader.is_encrypted and not reader.decrypt(password):
            raise UserError("保存校验失败：输出加密无法用原密码打开，原文件未改动。")
        if len(reader.pages) != expected_pages:
            raise UserError("保存校验失败：输出页数与内存不一致，原文件未改动。")
        if sum(len(page.get("/Annots") or []) for page in reader.pages) != expected_annots:
            raise UserError("保存校验失败：输出注释数与内存不一致，原文件未改动。")


def _close_doc(doc):
    # PyMuPDF 重复 close 会 ValueError("document closed")，用于 finally 的幂等关闭。
    try:
        doc.close()
    except ValueError:
        pass


def _atomic_save_in_place(doc, path, password=""):
    """原子替换源文件：temp（同目录）→ doc.save → 重开校验页数/注释数 → fsync temp
    → 关闭原句柄 → os.replace → fsync 目标目录 → 清理。任一步失败都不改动原文件。

    Windows 上 MuPDF 会持有源文件句柄，replace 前必须关闭（否则 WinError 5）。
    本函数在成功路径会关闭 doc；调用方用 _close_doc() 幂等收尾。

    加密：源文档加密且会话持有用户密码时，用相同的用户密码 + 原权限位显式 AES-256
    重加密，保证仍加密且原用户密码可解（AC-13.2）。非加密文档保持不加密，与普通工具
    契约一致。password 不写入磁盘、日志、argv 或临时文件名。
    """
    target = Path(path)
    folder = target.parent
    temporary = folder / f"{TEMP_PREFIX}{uuid.uuid4().hex}"
    _record_temp_dir(folder)  # 先登记：save 与 replace 之间崩溃也能被下次启动清扫
    kwargs = {"garbage": 4, "deflate": True}
    encrypted = _doc_is_encrypted(doc)
    if encrypted:
        if doc.needs_pass and not password:
            raise UserError("加密文档需要密码才能保存批注。")
        # 显式重加密而非 PDF_ENCRYPT_KEEP：PyMuPDF 1.28.2 在用户密码会话下用 KEEP
        # 复用原加密上下文时，可能把新增注释的字符串写成空/乱码（MuPDF AES 缺陷）。
        # 用已知用户密码 + 新随机 owner 密码 + 原权限位重新加密，产物稳定可验证；
        # 代价是 owner 密码无法保留（会话不持有，见已知限制）。
        kwargs.update(encryption=fitz.PDF_ENCRYPT_AES_256, user_pw=password,
                      owner_pw=uuid.uuid4().hex, permissions=doc.permissions)
    else:
        kwargs["encryption"] = fitz.PDF_ENCRYPT_NONE
    expected_pages = expected_annots = None
    try:
        expected_pages = len(doc)
        expected_annots = _count_annots(doc)
        doc.save(str(temporary), **kwargs)
        # 保存后立即关闭原句柄：① Windows 上 os.replace 需要它；② 避免同进程内两个
        # AES 文档并存（MuPDF 1.28.2 的全局 AES 状态缺陷）。
        _close_doc(doc)
        # 替换前先校验：损坏/半成品 temp 绝不能进入源文件。
        _validate_saved_temp(temporary, expected_pages, expected_annots, encrypted, password)
    except UserError:
        raise
    except Exception as error:
        # 损坏/畸形源文件可能由 MuPDF 抛非 UserError；统一为可解析错误，原文件保持不动。
        raise UserError("保存失败，原文件未改动。") from error
    try:
        _fsync_file(temporary)
        try:
            os.replace(str(temporary), str(target))
        except PermissionError as error:
            # H-1：目标被其他程序以无 FILE_SHARE_DELETE 打开（Windows）。可辨识、不重试轰炸。
            raise TargetInUseError() from error
        _fsync_dir(folder)
    finally:
        # 失败时删除 temp（若 unlink 也被锁则留下，交由下次启动的 cleanup_temp_dirs 清扫）；
        # 替换成功后 temp 已不存在。
        try:
            temporary.unlink()
        except OSError:
            pass
    return target


def _quad_rects(raw):
    if raw is None:
        return None
    if isinstance(raw, fitz.Rect):
        return [raw]
    if isinstance(raw, (list, tuple)):
        if len(raw) == 4 and all(isinstance(v, (int, float)) for v in raw):
            return [fitz.Rect(raw)]  # 单个 [x0, y0, x1, y1]
        return [fitz.Rect(item) for item in raw]
    raise UserError("批注选区坐标格式不正确。")


def add_highlight_annot(page, quads=None, rect=None, color=(1, 1, 0)):
    """添加标准 Highlight 注释（颜色 + 四边形点）。

    坐标为可见页、左上原点的 pt（与 area() 和 WPF PdfCoordinateMapper 一致），
    经 page.derotation_matrix 转换后再写入（V2 禁旋转页批注，非旋转页为恒等）。
    """
    raw = quads if quads is not None else ([rect] if rect is not None else None)
    rects = _quad_rects(raw)
    if not rects:
        raise UserError("高亮批注缺少选区坐标。")
    if isinstance(color, str):
        color = rgb(color)
    annot = page.add_highlight_annot(quads=[r * page.derotation_matrix for r in rects])
    annot.set_colors(stroke=tuple(color))
    annot.set_opacity(0.5)
    annot.update()
    return annot


def _note_box(page, rect):
    width = min(max(140.0, rect.width), max(140.0, page.rect.width))
    height = min(max(60.0, rect.height + 24.0), max(60.0, page.rect.height))
    left = min(max(rect.x0, 0.0), max(0.0, page.rect.width - width))
    top = min(max(rect.y0, 0.0), max(0.0, page.rect.height - height))
    return fitz.Rect(left, top, left + width, top + height)


def add_freetext_annot(page, rect, text, color=(1, 1, 0.7), fontsize=11):
    """添加 FreeText 笔记注释（Q7 裁决命名；不得复用 :269 的绘字函数 insert_text）。"""
    text = str(text or "")
    if not text.strip():
        raise UserError("笔记内容不能为空。")
    area = fitz.Rect(rect)
    if area.is_empty:
        raise UserError("笔记位置无效。")
    fontname = "china-s" if any(ord(c) > 255 for c in text) else "helv"
    box = _note_box(page, area)
    annot = page.add_freetext_annot(box * page.derotation_matrix, text, fontsize=fontsize,
                                    fontname=fontname, text_color=(0, 0, 0), fill_color=tuple(color), align=0)
    annot.set_info(content=text)
    annot.update()
    return annot


def add_ink_annot(page, strokes, color=(0, 0, 0), width=2):
    """添加 Ink 手写注释（V3 T11）。strokes 为多段折线（每段点序列），坐标为可见页左上原点 pt。

    坐标经 page.derotation_matrix 转换后再写入（与高亮/area() 同一口径，非旋转页为恒等）。
    """
    if not strokes:
        raise UserError("手写批注缺少笔迹。")
    matrix = page.derotation_matrix
    paths = []
    for stroke in strokes or []:
        points = []
        for point in stroke or []:
            try:
                points.append((float(point[0]), float(point[1])))
            except (TypeError, ValueError, IndexError):
                raise UserError("手写批注坐标格式不正确。")
        # 转换到未旋转页面空间；退化段（<2 点）丢弃。
        if len(points) >= 2:
            moved = []
            for x, y in points:
                p = fitz.Point(x, y) * matrix
                moved.append((p.x, p.y))
            paths.append(moved)
    if not paths:
        raise UserError("手写批注至少需要一个有效笔迹。")
    if isinstance(color, str):
        color = rgb(color)
    annot = page.add_ink_annot(paths)
    annot.set_colors(stroke=tuple(color))
    annot.set_border(width=max(0.1, float(width)))
    annot.update()
    return annot


def _shape_endpoints(raw):
    if not isinstance(raw, (list, tuple)) or len(raw) != 2:
        raise UserError("线/箭头批注需要两个端点。")
    try:
        start, end = raw
        return (float(start[0]), float(start[1])), (float(end[0]), float(end[1]))
    except (TypeError, ValueError, IndexError):
        raise UserError("线/箭头批注端点格式不正确。")


def add_shape_annot(page, shape, rect=None, points=None, color=(0, 0, 0), width=2):
    """添加形状注释（V3 T11）：square/circle 用外接矩形，line/arrow 用两点（起点→终点）。

    坐标经 page.derotation_matrix（与高亮/area() 同一口径）。箭头 = Line 注释 +
    set_line_ends(NONE, OPEN_ARROW)（尖刺 #4 定稿；方向恒为起点→终点，无二义）。
    """
    if isinstance(color, str):
        color = rgb(color)
    matrix = page.derotation_matrix
    kind = str(shape or "").strip().lower()
    if kind in ("square", "rect", "rectangle"):
        if rect is None:
            raise UserError("矩形批注缺少坐标。")
        annot = page.add_rect_annot(fitz.Rect(rect) * matrix)
    elif kind in ("circle", "ellipse", "oval"):
        if rect is None:
            raise UserError("圆形批注缺少坐标。")
        annot = page.add_circle_annot(fitz.Rect(rect) * matrix)
    elif kind in ("line", "arrow"):
        start, end = _shape_endpoints(points)
        annot = page.add_line_annot(fitz.Point(*start) * matrix, fitz.Point(*end) * matrix)
        if kind == "arrow":
            annot.set_line_ends(fitz.PDF_ANNOT_LE_NONE, fitz.PDF_ANNOT_LE_OPEN_ARROW)
    else:
        raise UserError("不支持的形状类型。")
    annot.set_colors(stroke=tuple(color))
    annot.set_border(width=max(0.1, float(width)))
    annot.update()
    return annot


def _find_annots(doc, action):
    """按稳定 /NM 名（name/handle）或同会话 xref 定位注释；跨保存 xref 会被 garbage
    重编号，故名字才是 T4 应持久化的句柄。"""
    wanted = action.get("name") or action.get("handle")
    xref = action.get("xref")
    found = []
    for page in doc:
        for annot in page.annots() or []:
            if wanted is not None:
                if annot.info.get("name") == str(wanted):
                    found.append((page, annot))
            elif xref is not None and annot.xref == xref:
                found.append((page, annot))
    return found


def apply_annotation_actions(doc, actions):
    """把 add/delete/setColor 批注动作应用到内存文档，返回 client id → 稳定注释名映射。"""
    handles = {}
    for action in actions:
        what = str(action.get("action", "add")).strip().lower()
        if what == "add":
            try:
                page_index = int(action.get("page", 0))
            except (TypeError, ValueError):
                raise UserError("批注页码格式不正确。")
            if not 0 <= page_index < len(doc):
                raise UserError("批注页码超出范围。")
            page = doc[page_index]
            kind = str(action.get("kind", "highlight")).strip().lower()
            color = rgb(action.get("color") or "#FFFF00")
            handle = str(action.get("id") or action.get("name") or uuid.uuid4().hex)
            if kind == "highlight":
                annot = add_highlight_annot(page, quads=action.get("quads"), rect=action.get("rect"), color=color)
            elif kind == "note":
                # 笔记 = 关联高亮 + FreeText；两者共用同一稳定名，删除时一并移除。
                highlight = add_highlight_annot(page, quads=action.get("quads"), rect=action.get("rect"), color=color)
                highlight.set_name(handle)
                highlight.update()
                annot = add_freetext_annot(page, action.get("noteRect") or action.get("rect"), action.get("text", ""))
            elif kind == "ink":
                annot = add_ink_annot(page, action.get("strokes"), color=color, width=action.get("width", 2))
            elif kind == "shape":
                annot = add_shape_annot(page, action.get("shape"), rect=action.get("rect"),
                                        points=action.get("points"), color=color, width=action.get("width", 2))
            else:
                raise UserError("不支持的批注类型。")
            # 注释 /NM 名跨保存稳定（xref 会被 garbage 重编号），T4 用它删除/改色。
            annot.set_name(handle)
            annot.update()
            handles[str(action.get("id") or handle)] = handle
        elif what in ("delete", "remove"):
            targets = _find_annots(doc, action)
            if not targets:
                raise UserError("找不到要删除的批注。")
            for page, annot in targets:
                page.delete_annot(annot)
        elif what in ("setcolor", "recolor", "color"):
            # V3 T11：改色覆盖所有带描边的注释类型（高亮/手写/矩形/圆/线/多边形）；
            # FreeText 不参与（与 V2 笔记语义一致：笔记改色只改关联高亮的 stroke）。
            stroke_types = ("Highlight", "Ink", "Square", "Circle", "Line", "Polygon")
            targets = [item for item in _find_annots(doc, action) if item[1].type[1] in stroke_types]
            if not targets:
                raise UserError("找不到要改色的批注。")
            for _, annot in targets:
                annot.set_colors(stroke=rgb(action.get("color") or "#FFFF00"))
                annot.update()
        else:
            raise UserError("不支持的批注操作。")
    return handles


def _normalize_annotation_actions(request, operation):
    options = request.get("options", {}) or {}
    raw = request.get("annotations")
    if raw is None:
        raw = options.get("annotations")
    if raw is None:
        single = request.get("annotation", options.get("annotation"))
        if isinstance(single, dict):
            raw = [single]
        elif operation in ("add_highlight_annot", "add_freetext_annot", "add_ink_annot", "add_shape_annot"):
            raw = [options]
    if raw is None:
        return []
    if not isinstance(raw, list) or any(not isinstance(item, dict) for item in raw):
        raise UserError("批注数据格式不正确。")
    default_kind = {"add_highlight_annot": "highlight", "add_freetext_annot": "note",
                    "add_ink_annot": "ink", "add_shape_annot": "shape"}.get(operation)
    if default_kind:
        for item in raw:
            item.setdefault("action", "add")
            item.setdefault("kind", default_kind)
    return raw


def save_annotations_request(request, operation="save_annotations"):
    """专用批注保存 dispatch：在 BY_ID 与 outputDir 校验之前处理，故自建等价校验（M-1）。"""
    files = [str(Path(p).expanduser().resolve()) for p in request.get("files", [])]
    options = request.get("options", {}) or {}
    if not files:
        raise UserError("请先选择文件。")
    if len(files) != 1:
        raise UserError("批注保存一次处理一份文件。")
    path = files[0]
    if not Path(path).is_file():
        raise UserError(f"文件不存在：{Path(path).name}")
    if Path(path).suffix.lower().lstrip(".") != "pdf":
        raise UserError(f"{Path(path).name} 的格式不适用于此工具。")
    actions = _normalize_annotation_actions(request, operation)
    if not actions:
        raise UserError("没有需要保存的批注。")
    password = str(options.get("password", "") or "")
    doc = open_pdf(path, password)
    try:
        handles = apply_annotation_actions(doc, actions)
        _atomic_save_in_place(doc, path, password)
    finally:
        _close_doc(doc)
    return {"ok": True, "outputs": [path], "annotationHandles": handles}


# --- V1.1 翻译接入（T10） ------------------------------------------------------
# 翻译是范围扩展后的专用操作：它在 BY_ID 之前处理（目录 id 为 translate_batch，
# 请求 operation 为 translate），自建校验并新建任务输出目录。API Key 只从 stdin
# JSON 的 options 读入并直接交给 LLMClient，绝不写入 argv / 环境变量 / 日志。
_TRANSLATE_FORMATS = {
    "both": ("mono", "dual"),
    "mono": ("mono",),
    "dual": ("dual",),
    "md": ("markdown",),
    "mono+md": ("mono", "markdown"),
    "dual+md": ("dual", "markdown"),
    "both+md": ("mono", "dual", "markdown"),
}
# 自定义模板：用户只填 system 指令，user 骨架由 prompt.build_custom_template 补齐。
_CUSTOM_TEMPLATE_KEY = "custom"


def _custom_translation_template(options, prompt_module):
    """从 options 解析自定义模板：优先显式 system+user 字典，回退到 system 正文。

    返回经 `prompt.build_custom_template` 校验的模板，供 `build_prompt(custom_template=...)`。
    非法输入抛 `UserError`，绝不静默回退到通用模板。
    """
    raw = options.get("customTemplate")
    try:
        if isinstance(raw, dict):
            return prompt_module.build_custom_template(raw.get("system"), raw.get("user"))
        system = str(options.get("customPrompt") or "").strip()
        if not system:
            raise UserError("已选「自定义」模板，请填写 Prompt 内容。")
        return prompt_module.build_custom_template(system)
    except prompt_module.PromptError as error:
        raise UserError(str(error)) from error


def _parse_translation_json(content):
    """解析模型返回的逐段译文：`[{"index","text"}]`、`{"segments":[...]}` 或纯文本数组。"""
    text = (content or "").strip()
    if text.startswith("```"):
        text = re.sub(r"^```[a-zA-Z0-9_-]*\s*", "", text)
        text = re.sub(r"\s*```$", "", text)
    try:
        data = json.loads(text)
    except ValueError as error:
        raise ValueError("翻译输出不是合法 JSON。") from error
    if isinstance(data, dict):
        if isinstance(data.get("segments"), list):
            data = data["segments"]
        elif "text" in data:
            data = [data]
        else:
            data = [data]
    if not isinstance(data, list) or not data:
        raise ValueError("翻译输出为空。")
    segments = []
    for item in data:
        value = item.get("text") if isinstance(item, dict) else item
        if not isinstance(value, str) or not value.strip():
            raise ValueError("翻译输出缺少文本。")
        segments.append(value)
    return segments


def _segment_validator(llm):
    """llm.translate 的 schema 校验回调：失败抛 ValidationError 触发其内部重试一次。"""
    def validate(content):
        try:
            texts = _parse_translation_json(content)
        except ValueError as error:
            raise llm.ValidationError(str(error)) from error
        if len(texts) != 1:
            raise llm.ValidationError("翻译输出段落数与请求不一致。")
    return validate


def _translate_one_segment(client, pair, source_text, page_no, report, source_lang, target_lang):
    """翻译一段并校验；校验不过按规格逐段重译一次，仍失败保留原文并标降级。"""
    from translate import llm, verify
    validator = _segment_validator(llm)
    for _attempt in range(2):
        try:
            content = client.translate(pair, validator=validator)
        except llm.AbortedError as error:
            # 用户取消：转成可辨识的 CancelledError，交由上层清理半成品任务目录。
            raise CancelledError(str(error)) from error
        except (llm.OverBudgetError, llm.AuthError,
                llm.NetworkError, llm.RateLimitError, llm.OverLimitError) as error:
            # 配置/网络/成本类错误终止整个任务；文案不含服务商原始报文。
            raise UserError(str(error)) from error
        except llm.LLMError:
            continue  # 拒译 / 空响应 / schema: 重译一次，仍失败走降级
        try:
            candidate = _parse_translation_json(content)[0]
        except (ValueError, IndexError):
            continue
        if verify.verify_translation(source_text, candidate, source_lang, target_lang).ok:
            return candidate
    report.add(page_no, verify.UNTRANSLATED_ECHO)
    return ""


def _translate_blocks(blocks, report, client, template_name, glossary_entries, source_lang,
                      target_lang, custom_template=None):
    """按 token 分块逐段翻译，再按源块下标重组（chunk T4 + prompt T6 + LLM T5）。

    发送前对每段源文调用 `prompt.scan_untrusted_instructions`：命中已知注入模式（决策 #18）
    即**不发送该段**、保留原文，并在降级报告记 `suspicious_instruction`，绝不静默翻译。
    RTL/竖排块（`block.unsupported_script`，规格 §2.4）同样不发送、保留原文；该原因由
    回填阶段（`reflow.backfill_page`）记入降级报告。

    **对齐契约**：`translations` 按 `Chunk.chunks` 的请求段顺序排列，
    `len(translations) == sum(len(chunk.chunks))`（即 `request_total`）。每个块前导的
    `overlap_count` 个上下文重叠段**不参与回填**，用空串占位以保持逐段对位；这样
    `chunk.reassemble_chunks` 无需在「请求对齐 / 核心对齐」之间猜测，避免重叠段造成的
    错位或丢段（见 chunk.reassemble_chunks）。
    """
    from translate import chunk, prompt, verify
    chunks = chunk.chunk_blocks(blocks)
    # F2：超长段落被切分时，chunk 只标记 Chunk.degraded/reasons，编排层必须把
    # `segment_split` 按受影响页记入降级报告，否则用户无从得知该页被切段。
    for item in chunks:
        if not item.degraded:
            continue
        for index in item.split_indices:
            block = blocks[index]
            report.add(block.page_no, verify.SEGMENT_SPLIT,
                       {"index": getattr(block, "index", None)})
    progress(f"正在翻译，共 {len(chunks)} 个分块", 0.25)
    translations = []
    for item in chunks:
        core = [blocks[index] for index in item.source_indices]
        if not core:
            continue
        # 待译文本：普通块用原块文本；被切分的超长段落用 `Chunk.core_segments` 的原子片段。
        # 超长段落切成 ≥2 片时同一块下标会重复出现，只有按片段发送才能让每片各自 ≤ 硬上限；
        # 否则整段被重复发送、浪费额度，重组时又会丢弃片段。`core_segments` 与
        # `source_indices` 一一对应，`split_indices` 标记本块内被切分的源块下标。
        split_indices = set(item.split_indices)
        segments = [segment if index in split_indices else blocks[index].text
                    for index, segment in zip(item.source_indices, item.core_segments)]
        # 请求段顺序 = [上下文重叠段…, 核心段…]；重叠段仅作提示，用占位符对齐，不参与回填。
        translations.extend([""] * item.overlap_count)
        pairs = prompt.build_prompt(segments, template_name=template_name,
                                    glossary=glossary_entries, custom_template=custom_template)
        for block, segment, pair in zip(core, segments, pairs):
            if getattr(block, "unsupported_script", ""):
                translations.append("")  # §2.4：不支持的脚本不发送、保留原文
                continue
            hits = prompt.scan_untrusted_instructions(segment)
            if hits:
                report.add(block.page_no, verify.SUSPICIOUS_INSTRUCTION,
                           {"patterns": hits, "index": getattr(block, "index", None)})
                translations.append("")
                continue
            translations.append(_translate_one_segment(client, pair, segment, block.page_no,
                                                       report, source_lang, target_lang))
    return chunk.reassemble_chunks(chunks, translations)


def _report_payload(report):
    """降级报告 payload：`to_dict()` 合并受限文档披露，供 sidecar 与 dispatch 结果共用。

    受限披露（`report.source_protection`，由 `output.write_outputs` 落属性）不在固定
    schema 内，必须显式合并；sidecar 与 `results[].report` 用同一份，避免二者断链
    （R5-1：宿主读 `results[].report.source_protection`）。
    """
    payload = report.to_dict()
    info = getattr(report, "source_protection", None)
    if info:
        payload["source_protection"] = info
    return payload


def _overflow_summary(report):
    """从每页 `reasons` 聚合溢出/裁切：`{pages, clipPages, warn}`（R5-11）。

    `pages` = 命中 `overflow_margin`(S2) 或 `clip`(S3) 的页数；`clipPages` = 仅命中
    `clip` 的页数；`warn` = 是否存在溢出页。消费方（含批量页）据此无需遍历逐页明细
    即可非静默地把「本文件 N 页译文超出原文范围，其中 M 页被裁切」呈现给用户。
    """
    from translate.verify import CLIP, OVERFLOW_MARGIN
    pages = clip_pages = 0
    for item in report.to_dict().get("pages", []):
        reasons = set(item.get("reasons", []))
        if reasons & {OVERFLOW_MARGIN, CLIP}:
            pages += 1
        if CLIP in reasons:
            clip_pages += 1
    return {"pages": pages, "clipPages": clip_pages, "warn": pages > 0}


def _translate_document(source_doc, source_path, options, out, client, formats, high_risk):
    """单文件：抽取 T3 → 分块 T4 → prompt T6 → LLM T5 → 校验 T7 → 回填 T8 → 输出 T9。"""
    from translate import extract, output, verify
    bridge = _bridge_module()
    progress("正在抽取文本", 0.1)
    extraction = extract.extract_document(source_doc)
    report = verify.DegradationReport(task_id=uuid.uuid4().hex[:12], total_pages=len(source_doc))
    for page in extraction.report["pages"]:
        for reason in page["reasons"]:
            report.add(page["page"], reason)
    # 复用抽取结果按页分组，避免再次抽取（扫描页会重复触发 OCR），并保证
    # 扁平译文的下标与 output.page_translations 的页偏移完全对齐。
    blocks_by_page = [[] for _ in range(len(source_doc))]
    for block in extraction.blocks:
        blocks_by_page[block.page_no - 1].append(block)
    translations = _translate_blocks(extraction.blocks, report, client,
                                     str(options.get("template") or "generic"),
                                     options.get("_glossary"),
                                     options.get("sourceLang"), options.get("targetLang"),
                                     custom_template=options.get("_custom_template"))
    progress("正在回填排版", 0.85)
    result = output.write_outputs(source_doc, source_path, translations, report, out,
                                  marker=bool(options.get("marker", True)), high_risk=high_risk,
                                  formats=formats, blocks_by_page=blocks_by_page, task_dir=out)
    bridge.atomic_write(out / f"{Path(source_path).stem}-降级报告.json",
                        json.dumps(_report_payload(report), ensure_ascii=False, indent=2))
    return result, report


def _is_loopback_endpoint(endpoint):
    """端点是否为本机 loopback（127.0.0.1 / ::1 / localhost）。

    与 C# `TranslationProviderConfig.IsLoopbackEndpoint` 同口径：用户自备的本地
    OpenAI 兼容服务（Ollama / LM Studio）常无需密钥，loopback 时允许空 apiKey；
    非 loopback（云端）仍强制要求密钥。
    """
    value = str(endpoint or "").strip()
    if not value:
        return False
    try:
        parts = urlsplit(value)
    except ValueError:
        return False
    if parts.scheme not in ("http", "https"):
        return False
    host = (parts.hostname or "").strip().lower()
    if not host:
        return False
    if host == "localhost":
        return True
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False


def _require_secure_endpoint(endpoint):
    """非 loopback 端点必须 https：拒绝明文 `http://`（本机 loopback 仍允许 http）。

    与 C# `TranslationProviderConfig` 的云端 https 要求同口径；本机自备服务
    （Ollama / LM Studio，loopback）常只监听 http，保持放行。非 http/https 的
    其它 scheme（含缺 scheme）一律拒绝，避免把密钥发往未预期协议。
    """
    value = str(endpoint or "").strip()
    scheme = urlsplit(value).scheme.lower()
    if scheme == "https" or (scheme == "http" and _is_loopback_endpoint(value)):
        return
    raise UserError("翻译服务端点必须使用 https（仅本机 loopback 允许 http）。")


def translate_request(request):
    """翻译操作 dispatch：在 BY_ID 与 outputDir 校验之前处理，自建等价校验（T10）。"""
    from translate import glossary as glossary_module
    from translate import llm, prompt
    files = [str(Path(p).expanduser().resolve()) for p in request.get("files", [])]
    options = request.get("options", {}) or {}
    if not files:
        raise UserError("请先选择文件。")
    for path in files:
        if not Path(path).is_file():
            raise UserError(f"文件不存在：{Path(path).name}")
        if Path(path).suffix.lower().lstrip(".") != "pdf":
            raise UserError(f"{Path(path).name} 的格式不适用于此工具。")
    api_key = str(options.get("apiKey", "") or "")
    endpoint = str(options.get("endpoint", "") or "").strip()
    model = str(options.get("model", "") or "").strip()
    if not endpoint:
        raise UserError("请先配置翻译服务端点。")
    _require_secure_endpoint(endpoint)
    if not model:
        raise UserError("请先配置翻译模型。")
    if not api_key and not _is_loopback_endpoint(endpoint):
        # 本地 loopback（Ollama / LM Studio）常无需密钥；云端必须提供。
        raise UserError("请先配置翻译服务 API Key。")
    template_name = str(options.get("template") or "generic")
    custom_template = None
    if template_name == _CUSTOM_TEMPLATE_KEY:
        custom_template = _custom_translation_template(options, prompt)
        high_risk = False
    elif template_name in prompt.PRESET_TEMPLATES:
        high_risk = prompt.PRESET_TEMPLATES[template_name].get("high_risk", False)
    else:
        raise UserError(f"未知的翻译模板：{template_name}。")
    formats = _TRANSLATE_FORMATS.get(str(options.get("format") or "both"))
    if formats is None:
        raise UserError("输出形态只能是 both、mono、dual、md 或其组合（如 both+md）。")
    cap = str(options.get("maxTokens", "") or "").strip()
    try:
        cost_cap = int(cap) if cap else None
    except ValueError:
        raise UserError("单任务 token 上限必须是整数。")
    glossary_entries = None
    glossary_path = str(options.get("glossary", "") or "").strip()
    if glossary_path:
        try:
            glossary_entries = glossary_module.load_glossary(glossary_path)
        except glossary_module.GlossaryError as error:
            raise UserError(str(error)) from error
    options = {**options, "_glossary": glossary_entries, "_custom_template": custom_template}
    parent = Path(request.get("outputDir", "")).expanduser()
    if not parent.is_absolute() or not parent.is_dir():
        raise UserError("请选择有效的输出文件夹。")
    out = parent / ("AIPDF-" + time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6])
    out.mkdir(mode=0o700)
    started = time.monotonic()
    try:
        client = llm.LLMClient(endpoint, api_key, model, cost_cap=cost_cap)
        outputs, results = [], []
        for index, path in enumerate(files):
            progress(f"正在翻译文件 {index + 1}/{len(files)}", index / len(files))
            with open_pdf(path, str(options.get("password", "") or "")) as source_doc:
                result, report = _translate_document(source_doc, path, options, out, client,
                                                     formats, high_risk)
            for kind in ("mono", "dual", "markdown"):
                if result.get(kind):
                    outputs.append(Path(result[kind]))
            results.append({"file": path, "taskDir": result["taskDir"],
                            "mono": result.get("mono"), "dual": result.get("dual"),
                            "markdown": result.get("markdown"),
                            # 与 sidecar 同源：含 source_protection，宿主据此读受限披露（R5-1）。
                            "report": _report_payload(report),
                            # 不丢弃 write_outputs 返回的受限披露（R5-1）。
                            "sourceProtection": result.get("sourceProtection"),
                            # 溢出/裁切聚合，供结果区与批量页非静默告警（R5-11）。
                            "overflow": _overflow_summary(report)})
        progress("翻译完成", 1.0)
        return {"ok": True, "outputs": [str(p) for p in outputs], "outputDir": str(out),
                "results": results,
                "inputBytes": sum(Path(f).stat().st_size for f in files),
                "outputBytes": sum(p.stat().st_size for p in outputs),
                "elapsed": round(time.monotonic() - started, 2)}
    except CancelledError:
        # 取消发生在 mono 与 dual 之间等中途：任务目录里只有半成品，清理掉，
        # 避免用户看到/误用一个不完整的「成果」。普通失败仍保留目录供排查。
        shutil.rmtree(out, ignore_errors=True)
        raise
    except Exception as error:
        raise UserError(f"{str(error) if isinstance(error, UserError) else '翻译失败，请检查文件格式、网络与配置。'}\n本次输出目录：{out}") from error


# --- V1.1 划词（选中即译）轻量翻译 op -----------------------------------------
# 与整篇 translate 共用 prompt/llm/verify，但不落任务目录、不产出 PDF：只把一段
# 选中文本翻回一个字符串。它同样在 BY_ID 之前处理，自校验证（T14 划词卡片）。
_TRANSLATE_TEXT_LIMIT = 20000  # 单次划词字符上限，远低于桥接单行 1MB


def translate_text_request(request):
    """划词翻译 dispatch：返回 `{ok, translation, degraded?, elapsed}`（不产出文件）。"""
    from translate import glossary as glossary_module
    from translate import detect, llm, prompt, verify

    options = request.get("options", {}) or {}
    text = str(options.get("text", "") or "")
    if not text.strip():
        raise UserError("请先选中要翻译的文字。")
    if len(text) > _TRANSLATE_TEXT_LIMIT:
        raise UserError(f"选中文字过长（超过 {_TRANSLATE_TEXT_LIMIT} 字符），请改用整篇翻译。")
    endpoint = str(options.get("endpoint", "") or "").strip()
    model = str(options.get("model", "") or "").strip()
    api_key = str(options.get("apiKey", "") or "")
    if not endpoint:
        raise UserError("请先配置翻译服务端点。")
    _require_secure_endpoint(endpoint)
    if not model:
        raise UserError("请先配置翻译模型。")
    if not api_key and not _is_loopback_endpoint(endpoint):
        # 本地 loopback（Ollama / LM Studio）常无需密钥；云端必须提供。
        raise UserError("请先配置翻译服务 API Key。")
    template_name = str(options.get("template") or "generic")
    custom_template = None
    if template_name == _CUSTOM_TEMPLATE_KEY:
        custom_template = _custom_translation_template(options, prompt)
    elif template_name not in prompt.PRESET_TEMPLATES:
        raise UserError(f"未知的翻译模板：{template_name}。")
    source_lang = str(options.get("sourceLang") or "auto")
    target_lang = str(options.get("targetLang") or "zh")
    glossary_entries = None
    glossary_path = str(options.get("glossary", "") or "").strip()
    if glossary_path:
        try:
            glossary_entries = glossary_module.load_glossary(glossary_path)
        except glossary_module.GlossaryError as error:
            raise UserError(str(error)) from error
    cap = str(options.get("maxTokens", "") or "").strip()
    try:
        cost_cap = int(cap) if cap else None
    except ValueError:
        raise UserError("单任务 token 上限必须是整数。")

    started = time.monotonic()
    report = verify.DegradationReport(task_id=uuid.uuid4().hex[:12], total_pages=1)
    translated = ""
    # F16：划词同样做 RTL/竖排检测（规格 §2.4）；命中即不发送、保留原文并记降级，
    # 与整篇翻译 `_translate_blocks` 的处置一致（不静默按 LTR 回填）。
    unsupported = detect.classify_unsupported(text)
    if unsupported:
        report.add(1, verify.UNSUPPORTED_SCRIPT, {"script": unsupported})
        hits = []
    else:
        hits = prompt.scan_untrusted_instructions(text)
    if hits:
        # 命中已知注入模式：拒绝发送该段、保留原文，并记降级原因（决策 #18）。
        report.add(1, verify.SUSPICIOUS_INSTRUCTION, {"patterns": hits})
    elif not unsupported:
        try:
            client = llm.LLMClient(endpoint, api_key, model, cost_cap=cost_cap)
            pair = prompt.build_prompt([text], template_name=template_name,
                                       glossary=glossary_entries,
                                       custom_template=custom_template)[0]
            translated = _translate_one_segment(client, pair, text, 1, report,
                                                source_lang, target_lang)
        except llm.AbortedError as error:
            raise CancelledError(str(error)) from error
        except (llm.OverBudgetError, llm.AuthError,
                llm.NetworkError, llm.RateLimitError, llm.OverLimitError) as error:
            raise UserError(str(error)) from error
        except llm.LLMError as error:
            raise UserError(str(error)) from error

    payload = report.to_dict()
    return {"ok": True, "translation": translated,
            "degraded": not bool(translated),
            "reasons": payload.get("pages", [{}])[0].get("reasons", []) if payload.get("pages") else [],
            "elapsed": round(time.monotonic() - started, 2)}


def _bridge_module():
    """延迟导入 translate.bridge（仅在翻译分支需要，避免普通工具加载翻译栈）。"""
    from translate import bridge
    return bridge


def position_rect(page, options, width, height):
    bounds = page.rect
    p = options.get("position", "bottom-center")
    width, height = min(width, bounds.width-32), min(height, bounds.height-32)
    x = 16 if "left" in p else bounds.width-width-16 if "right" in p else (bounds.width-width)/2
    y = 16 if "top" in p else (bounds.height-height)/2 if p == "center" else bounds.height-height-16
    return fitz.Rect(x, y, x+width, y+height) * page.derotation_matrix


def load_image(path, preserve_alpha=False):
    try:
        im = Image.open(path)
        return ImageOps.exif_transpose(im).convert("RGBA" if preserve_alpha else "RGB")
    except Exception:
        if sys.platform == "win32" and Path(path).suffix.lower() in (".heic", ".heif"):
            raise UserError("Windows 引擎暂不能读取 HEIC 图片。请先将 HEIC 转换为 PNG 或 JPEG 后重试。")
        helper = vision_path()
        if not helper:
            raise UserError("无法读取图片。请转换为 PNG 或 JPEG。")
        with tempfile.TemporaryDirectory(prefix="aipdf-image-") as tmp:
            out = Path(tmp) / "image.png"
            run_process([helper, "image", path, out])
            return Image.open(out).convert("RGBA" if preserve_alpha else "RGB")


def images_pdf(files, options, dest, scan=False):
    doc = fitz.open()
    for index, path in enumerate(files):
        progress(f"正在导入图片 {index+1}/{len(files)}", index/len(files))
        im = load_image(path)
        if scan and flag(options, "perspective", True):
            helper = vision_path()
            if not helper:
                raise UserError("缺少 Apple Vision 扫描组件，请重新构建应用。")
            with tempfile.TemporaryDirectory(prefix="aipdf-scan-") as tmp:
                source, out = Path(tmp)/"in.png", Path(tmp)/"out.png"
                im.save(source)
                run_process([helper, "scan", source, out])
                im = Image.open(out).convert("RGB")
        if scan and options.get("scanMode") != "original":
            im = ImageOps.autocontrast(ImageOps.grayscale(im))
            if options.get("scanMode", "document") == "document":
                im = ImageEnhance.Contrast(im).enhance(1.5)
        page_size = options.get("pageSize", "A4")
        width, height = (im.width*.75, im.height*.75) if page_size == "image" else fitz.paper_size(page_size)
        if options.get("orientation") == "landscape":
            width, height = height, width
        margin = number(options, "margin", 24, 0)
        if margin*2 >= min(width, height):
            raise UserError("页边距太大，图片没有可用空间。")
        page = doc.new_page(width=width, height=height)
        stream = io.BytesIO()
        im.save(stream, format="PNG")
        page.insert_image(fitz.Rect(margin, margin, width-margin, height-margin), stream=stream.getvalue())
    save_pdf(doc, dest)
    doc.close()
    return [dest]


def office_convert(source, outdir, pdfa=None):
    executable = find_office()
    if not executable:
        if sys.platform == "win32":
            raise UserError("此转换需要 LibreOffice。请安装 LibreOffice 并确保 soffice.exe 可被找到，或用 AIPDF_OFFICE 环境变量指定路径后重试。")
        raise UserError("此转换需要 LibreOffice。请将 LibreOffice 安装到“应用程序”文件夹后重试。")
    with tempfile.TemporaryDirectory(prefix="aipdf-office-") as tmp:
        profile = Path(tmp) / "profile"
        target = Path(tmp) / "output"
        target.mkdir()
        fmt = "pdf"
        if pdfa:
            version = {"1b": 1, "2b": 2, "3b": 3}[pdfa]
            fmt = 'pdf:draw_pdf_Export:' + json.dumps({"SelectPdfVersion": {"type": "long", "value": str(version)}})
        run_process([executable, f"-env:UserInstallation={profile.as_uri()}", "--headless", "--nologo", "--nodefault", "--norestore",
                     "--convert-to", fmt, "--outdir", target, source], timeout=300)
        output = target / (Path(source).stem + ".pdf")
        if not output.is_file():
            raise UserError("Office 引擎没有生成 PDF。请检查文件是否加密、损坏或无法打开。")
        with fitz.open(output) as check:
            if not len(check):
                raise UserError("转换后的 PDF 为空。")
            if pdfa and f'part="{pdfa[0]}"' not in check.get_xml_metadata() and f'<pdfaid:part>{pdfa[0]}</pdfaid:part>' not in check.get_xml_metadata():
                raise UserError("引擎未输出要求的 PDF/A 标识，已停止导出。")
        shutil.copyfile(output, outdir)
    return [outdir]


def ocr_doc(doc, options):
    helper = vision_path()
    if not helper:
        raise UserError("缺少 Apple Vision 识别组件，请重新构建应用。")
    selected = parse_pages(options.get("pages", ""), len(doc))
    recognized = 0
    with tempfile.TemporaryDirectory(prefix="aipdf-ocr-") as tmp:
        for j, i in enumerate(selected):
            page = doc[i]
            progress(f"本机识别第 {i+1} 页", j/len(selected))
            if page.get_text().strip() and not flag(options, "force"):
                continue
            # Normalize rotated pages so image and OCR coordinates agree.
            if page.rotation:
                page.remove_rotation()
            image = Path(tmp)/"page.png"
            page.get_pixmap(dpi=216).save(image)
            boxes = json.loads(run_process([helper, "ocr", image], timeout=180))
            for item in boxes:
                text = item["text"]
                x, y, w, h = item["x"]*page.rect.width, item["y"]*page.rect.height, item["w"]*page.rect.width, item["h"]*page.rect.height
                font = fitz.Font("china-s" if any(ord(c)>255 for c in text) else "helv")
                size = min(h*.8, w / max(font.text_length(text, fontsize=1), .01))
                page.insert_text((x, y+h*.82), text, fontname=font.name if font.name == "helv" else ("china-s" if any(ord(c)>255 for c in text) else "helv"), fontsize=max(.5, size), render_mode=3)
                recognized += 1
    return recognized


def compress_doc(doc, options):
    quality = options.get("quality", "balanced")
    if quality == "lossless":
        return
    max_dpi, jpeg_quality = (144, 70) if quality == "balanced" else (96, 45)
    seen = set()
    for page in doc:
        for info in page.get_images(full=True):
            xref, smask = info[0], info[1]
            if xref in seen or smask:
                continue
            seen.add(xref)
            try:
                raw = doc.extract_image(xref)
                im = Image.open(io.BytesIO(raw["image"]))
                if im.width < 256 or im.height < 256:
                    continue
                rects = [r for p in doc for r in p.get_image_rects(xref)]
                if not rects:
                    continue
                max_w = max(max(1, int(r.width/72*max_dpi)) for r in rects)
                max_h = max(max(1, int(r.height/72*max_dpi)) for r in rects)
                im.thumbnail((max_w, max_h), Image.Resampling.LANCZOS)
                encoded = io.BytesIO()
                im.convert("RGB").save(encoded, format="JPEG", quality=jpeg_quality, optimize=True)
                if len(encoded.getvalue()) < len(raw["image"]):
                    page.replace_image(xref, stream=encoded.getvalue())
            except (ValueError, OSError):
                continue


def export_word(doc, options, dest):
    from docx import Document
    from docx.shared import Inches
    result = Document()
    visual = options.get("mode") == "visual"
    for index, page in enumerate(doc):
        if index:
            result.add_page_break()
        if visual:
            result.add_picture(io.BytesIO(page.get_pixmap(dpi=144).tobytes("png")), width=Inches(6.2))
            continue
        tables = page.find_tables().tables
        table_bounds = [fitz.Rect(t.bbox) for t in tables]
        events = []
        for block in page.get_text("dict", sort=True)["blocks"]:
            if any(fitz.Rect(block["bbox"]).intersects(r) for r in table_bounds):
                continue
            events.append((block["bbox"][1], "block", block))
        events += [(t.bbox[1], "table", t.extract()) for t in tables]
        for _, kind, item in sorted(events, key=lambda e:e[0]):
            if kind == "table":
                if not item:
                    continue
                table = result.add_table(rows=len(item), cols=max(len(row) for row in item))
                table.style = "Table Grid"
                for r, row in enumerate(item):
                    for c, cell in enumerate(row):
                        table.cell(r, c).text = str(cell or "")
            elif item["type"] == 1:
                result.add_picture(io.BytesIO(item["image"]), width=Inches(min(6.2, (item["bbox"][2]-item["bbox"][0])/72)))
            else:
                for line in item.get("lines", []):
                    p = result.add_paragraph()
                    for span in line["spans"]:
                        run = p.add_run(span["text"])
                        run.bold = bool(span["flags"] & 16)
                        run.italic = bool(span["flags"] & 2)
    result.save(dest)


def export_ppt(doc, options, dest):
    from pptx import Presentation
    from pptx.util import Pt
    presentation = Presentation()
    presentation.slide_width, presentation.slide_height = Pt(doc[0].rect.width), Pt(doc[0].rect.height)
    for page in doc:
        slide = presentation.slides.add_slide(presentation.slide_layouts[6])
        sx, sy = doc[0].rect.width/page.rect.width, doc[0].rect.height/page.rect.height
        if options.get("mode", "visual") == "visual":
            slide.shapes.add_picture(io.BytesIO(page.get_pixmap(dpi=144).tobytes("png")), 0, 0, presentation.slide_width, presentation.slide_height)
        else:
            for block in page.get_text("dict")["blocks"]:
                x0,y0,x1,y1 = block["bbox"]
                if block["type"] == 1:
                    slide.shapes.add_picture(io.BytesIO(block["image"]), Pt(x0*sx), Pt(y0*sy), Pt((x1-x0)*sx), Pt((y1-y0)*sy))
                else:
                    tf = slide.shapes.add_textbox(Pt(x0*sx), Pt(y0*sy), Pt((x1-x0)*sx+8), Pt((y1-y0)*sy+8)).text_frame
                    tf.margin_left = tf.margin_right = tf.margin_top = tf.margin_bottom = 0
                    for n,line in enumerate(block.get("lines", [])):
                        para = tf.paragraphs[0] if n == 0 else tf.add_paragraph()
                        for span in line["spans"]:
                            run = para.add_run()
                            run.text = span["text"]
                            run.font.size = Pt(max(1, span["size"]*sy))
                            run.font.bold = bool(span["flags"] & 16)
    presentation.save(dest)


def export_excel(doc, dest):
    from openpyxl import Workbook
    from openpyxl.styles import Font, PatternFill, Alignment
    book = Workbook()
    book.remove(book.active)
    for page in doc:
        tables = page.find_tables().tables
        data = [t.extract() for t in tables] or [[[line] for line in page.get_text(sort=True).splitlines()] or [["（本页没有可提取文字）"]]]
        for index, rows in enumerate(data):
            sheet = book.create_sheet(f"第{page.number+1}页-表{index+1}")
            for row in rows:
                # Explicit strings prevent spreadsheet formula injection from source documents.
                sheet.append([str(c or "") for c in row])
                for cell in sheet[sheet.max_row]:
                    cell.data_type = "s"
                    cell.alignment = Alignment(vertical="top", wrap_text=True)
            for cell in sheet[1]:
                cell.font = Font(bold=True, color="FFFFFF")
                cell.fill = PatternFill("solid", fgColor="314A72")
            sheet.freeze_panes = "A2"
            for column in sheet.columns:
                sheet.column_dimensions[column[0].column_letter].width = min(70, max(16, max(len(str(c.value or "")) for c in column)+2))
    book.save(dest)


def export_markdown(doc, options, dest):
    sections = []
    assets = dest.parent / (dest.stem + "_assets")
    for i in parse_pages(options.get("pages", ""), len(doc)):
        page = doc[i]
        sections.append(f"<!-- 第 {i+1} 页 -->\n")
        tables = page.find_tables().tables
        boxes = [fitz.Rect(t.bbox) for t in tables]
        events = [(t.bbox[1], "table", t.extract()) for t in tables]
        for block in page.get_text("dict", sort=True)["blocks"]:
            if not any(fitz.Rect(block["bbox"]).intersects(r) for r in boxes):
                events.append((block["bbox"][1], "block", block))
        for j, (_, kind, obj) in enumerate(sorted(events, key=lambda e:e[0])):
            if kind == "table" and obj:
                esc = lambda x: str(x or "").replace("|", "\\|").replace("\n", "<br>")
                sections.append("| " + " | ".join(esc(c) for c in obj[0]) + " |")
                sections.append("| " + " | ".join("---" for _ in obj[0]) + " |")
                sections.extend("| " + " | ".join(esc(c) for c in row) + " |" for row in obj[1:])
            elif kind == "block" and obj["type"] == 1:
                assets.mkdir(exist_ok=True)
                image = assets / f"page-{i+1}-{j}.{obj['ext']}"
                image.write_bytes(obj["image"])
                sections.append(f"![第 {i+1} 页图片]({assets.name}/{image.name})")
            elif kind == "block":
                for line in obj.get("lines", []):
                    text = "".join(s["text"] for s in line["spans"])
                    size = max((s["size"] for s in line["spans"]), default=11)
                    prefix = "# " if size >= 22 else "## " if size >= 17 else "### " if size >= 14 else ""
                    sections.append(prefix + text)
            sections.append("")
        for link in page.get_links():
            if link.get("uri", "").startswith(("https://", "http://", "mailto:")):
                title = page.get_textbox(link["from"]).strip() or "链接"
                sections.append(f"[{title}]({link['uri']})")
    dest.write_text("\n".join(sections), encoding="utf-8")


def compare_pdfs(files, options, out):
    if len(files) != 2:
        raise UserError("比较需要正好两份 PDF，请按旧版、新版的顺序添加。")
    left, right = (open_pdf(p, options.get("password", "")) for p in files)
    result = fitz.open()
    for i in range(max(len(left),len(right))):
        progress(f"正在比较第 {i+1} 页", i/max(len(left),len(right)))
        images = []
        for doc in (left, right):
            if i < len(doc):
                pix = doc[i].get_pixmap(dpi=96, colorspace=fitz.csRGB)
                im = Image.frombytes("RGB", (pix.width,pix.height), pix.samples)
            else:
                im = Image.new("RGB", (600,800), "white")
            im.thumbnail((900,1200))
            canvas = Image.new("RGB", (900,1200), "white")
            canvas.paste(im, (0,0))
            images.append(canvas)
        difference = ImageChops.difference(*images).convert("L").point(lambda v: 150 if v > 25 else 0)
        highlighted = Image.composite(Image.new("RGB",images[1].size,(255,90,95)),images[1],difference)
        page = result.new_page(width=960,height=690)
        page.insert_text((24,24),f"Before - page {i+1}",fontsize=12)
        page.insert_text((500,24),f"After - page {i+1} (changes in red)",fontsize=12)
        for x, im in ((20,images[0]),(500,highlighted)):
            stream=io.BytesIO(); im.save(stream,format="PNG")
            page.insert_image(fitz.Rect(x,40,x+440,670),stream=stream.getvalue())
    pdf = out/"版本对比.pdf"
    save_pdf(result,pdf)
    diff = difflib.HtmlDiff(wrapcolumn=80).make_file(
        "\n".join(p.get_text(sort=True) for p in left).splitlines(),
        "\n".join(p.get_text(sort=True) for p in right).splitlines(),
        fromdesc=html.escape(Path(files[0]).name), todesc=html.escape(Path(files[1]).name), charset="utf-8")
    report=out/"文字差异.html"; report.write_text(diff)
    left.close(); right.close(); result.close()
    return [pdf,report]


def form_update(doc, options):
    if options.get("formMode", "fill") == "fill":
        values = options.get("formValues", {})
        if not values:
            raise UserError("未找到可填写字段。请先读取表单，或选择创建字段。")
        found=set()
        for page in doc:
            for widget in page.widgets() or []:
                if widget.field_name in values:
                    value = values[widget.field_name]
                    if widget.field_type in (fitz.PDF_WIDGET_TYPE_CHECKBOX, fitz.PDF_WIDGET_TYPE_RADIOBUTTON):
                        on = widget.on_state()
                        value = on if str(value).lower() in ("true", "yes", "1", str(on).lower()) else "Off"
                    widget.field_value=value
                    widget.update()
                    found.add(widget.field_name)
        if set(values)-found:
            raise UserError("部分表单字段不存在，请重新读取文件。")
    else:
        pindex = parse_pages(options.get("pages","1"),len(doc))[0]
        page=doc[pindex]
        if page.rotation: page.remove_rotation()
        name=options.get("fieldName", "").strip()
        if not name:
            raise UserError("请输入字段名称。")
        if any(w.field_name==name for p in doc for w in (p.widgets() or [])):
            raise UserError("字段名称已经存在，请使用新名称。")
        kind=options.get("fieldType","text")
        if kind == "radio":
            # pypdf constructs the shared parent and kids for a true radio group.
            return "radio"
        widget=fitz.Widget()
        widget.field_name=name
        widget.field_type={"text":fitz.PDF_WIDGET_TYPE_TEXT,"checkbox":fitz.PDF_WIDGET_TYPE_CHECKBOX,"list":fitz.PDF_WIDGET_TYPE_LISTBOX}[kind]
        widget.rect=area(page,options)
        widget.field_value=options.get("fieldValue","")
        widget.text_fontsize=12
        widget.border_color=(.35,.4,.5)
        widget.border_width=1
        if kind=="checkbox":
            widget.field_value="Yes" if flag(options,"fieldValue") else "Off"
        if kind=="list":
            widget.choice_values=options.get("choices","").splitlines()
            if not widget.choice_values:
                raise UserError("请填写至少一个列表选项。")
        page.add_widget(widget)


def radio_group(dest, options):
    from pypdf import PdfReader, PdfWriter
    from pypdf.generic import DictionaryObject, NameObject, NumberObject, FloatObject, ArrayObject, TextStringObject, DecodedStreamObject
    reader=PdfReader(dest)
    writer=PdfWriter(); writer.clone_document_from_reader(reader)
    page_index=parse_pages(options.get("pages","1"),len(reader.pages))[0]
    page=writer.pages[page_index]
    if page.get("/Rotate",0):
        raise UserError("创建单选字段前请先把该页旋转归零。")
    choices=[c.strip() for c in options.get("choices", "").splitlines() if c.strip()]
    if len(choices)<2:
        raise UserError("单选组至少需要两个选项。")
    x=float(options.get("x",48)); y=float(options.get("y",72)); h=float(options.get("height",64))
    top=float(page.cropbox.top)-y
    left=float(page.cropbox.left)+x
    kids=ArrayObject()
    group=DictionaryObject({NameObject("/FT"):NameObject("/Btn"),NameObject("/T"):TextStringObject(options["fieldName"]),NameObject("/Ff"):NumberObject(1<<15),NameObject("/Kids"):kids,NameObject("/V"):NameObject("/Off")})
    group_ref=writer._add_object(group)
    for i,choice in enumerate(choices):
        side=min(16,h/len(choices))
        y1=top-i*h/len(choices)
        states=DictionaryObject()
        for state in ("Off",f"Option{i+1}"):
            stream=DecodedStreamObject()
            stream.set_data(("0.4 G 0.5 0.5 15 15 re S\n"+("0 g 4 4 8 8 re f" if state!="Off" else "")).encode())
            stream.update({NameObject("/Type"):NameObject("/XObject"),NameObject("/Subtype"):NameObject("/Form"),NameObject("/BBox"):ArrayObject([NumberObject(0),NumberObject(0),NumberObject(16),NumberObject(16)])})
            states[NameObject("/"+state)]=writer._add_object(stream)
        selected=choice==options.get("fieldValue","")
        widget=DictionaryObject({NameObject("/Type"):NameObject("/Annot"),NameObject("/Subtype"):NameObject("/Widget"),NameObject("/Parent"):group_ref,NameObject("/Rect"):ArrayObject([FloatObject(left),FloatObject(y1-side),FloatObject(left+side),FloatObject(y1)]),NameObject("/F"):NumberObject(4),NameObject("/TU"):TextStringObject(choice),NameObject("/AS"):NameObject(f"/Option{i+1}" if selected else "/Off"),NameObject("/AP"):DictionaryObject({NameObject("/N"):states})})
        ref=writer._add_object(widget); kids.append(ref)
        if "/Annots" not in page: page[NameObject("/Annots")]=ArrayObject()
        page["/Annots"].append(ref)
        if selected: group[NameObject("/V")]=NameObject(f"/Option{i+1}")
    if "/AcroForm" not in writer.root_object:
        writer.root_object[NameObject("/AcroForm")]=writer._add_object(DictionaryObject({NameObject("/Fields"):ArrayObject()}))
    writer.root_object["/AcroForm"]["/Fields"].append(group_ref)
    temporary=dest.with_suffix(".radio.pdf")
    writer.write(temporary)
    os.replace(temporary,dest)


def url_to_pdf(url,dest):
    """Windows URL rendering via Playwright, replacing the Swift WebHelper.

    Each task gets a fresh browser context (no storage_state, no cookies),
    matching the WebHelper's nonPersistent WKWebsiteDataStore. Bundled
    Chromium is the default; channel="msedge" is a documented fallback when
    the bundled browser is unavailable.
    """
    from playwright.sync_api import sync_playwright, TimeoutError as web_timeout
    try:
        with sync_playwright() as p:
            browser=p.chromium.launch(headless=True)
            try:
                context=browser.new_context()
                page=context.new_page()
                page.goto(url,wait_until="load",timeout=45000)
                page.pdf(path=str(dest),format="A4",print_background=True)
                context.close()
            finally:
                browser.close()
    except web_timeout:
        raise UserError("网页加载超时，请检查网址或稍后重试。")
    except Exception:
        raise UserError("网页加载失败，请检查网址是否可访问。")
    return [dest]


def single_pdf(operation, source, options, out, index):
    stem=f"{index:02d}-"+("网页" if source.startswith(("https://","http://")) else Path(source).stem)
    dest=out/f"{stem}-{operation}.pdf"
    if operation in ("word_to_pdf","ppt_to_pdf","excel_to_pdf"):
        return office_convert(source,dest)
    if operation=="html_to_pdf":
        if source.startswith(("https://","http://")):
            if sys.platform=="win32":
                # Windows: Playwright replaces the WebHelper binary; nothing persists.
                return url_to_pdf(source,dest)
            helper=web_path()
            if not helper: raise UserError("缺少本地网页渲染组件，请重新构建应用。")
            with tempfile.TemporaryDirectory(prefix="aipdf-web-") as temp:
                raw=Path(temp)/"web.pdf"
                run_process([helper,source,raw],timeout=90)
                with fitz.open(raw) as website:
                    output=fitz.open()
                    for source_page in website:
                        width=source_page.rect.width
                        page_height=width*842/595
                        y=0
                        while y<source_page.rect.height:
                            clip=fitz.Rect(0,y,width,min(y+page_height,source_page.rect.height))
                            page=output.new_page(width=595,height=842)
                            page.show_pdf_page(fitz.Rect(0,0,595,clip.height*595/width),website,source_page.number,clip=clip)
                            y+=page_height
                    save_pdf(output,dest);output.close()
            return [dest]
        markup=Path(source).read_text(encoding="utf-8")
        # Story has no browser/JavaScript/network runtime; archive resolves local assets.
        story=fitz.Story(html=markup,archive=fitz.Archive(str(Path(source).parent)))
        writer=fitz.DocumentWriter(str(dest)); more=1; pages=0
        while more:
            more,_=story.place(fitz.Rect(36,36,559,806)); pages+=1
            if pages>10000: raise UserError("HTML 文档页数超出限制。")
            device=writer.begin_page(fitz.Rect(0,0,595,842)); story.draw(device); writer.end_page()
        writer.close()
        return [dest]
    doc=open_pdf(source,options.get("password",""))
    try:
        selected=parse_pages(options.get("pages",""),len(doc),duplicates=operation=="organize")
        if operation=="split":
            ranges=options.get("ranges", "").replace("；",";")
            groups=[parse_pages(s,len(doc)) for s in ranges.split(";")] if ranges.strip() else [[i] for i in range(len(doc))]
            outputs=[]
            for n,group in enumerate(groups):
                part=fitz.open()
                for p in group: part.insert_pdf(doc,from_page=p,to_page=p)
                path=out/f"{stem}-part-{n+1:03d}.pdf"; save_pdf(part,path); part.close(); outputs.append(path)
            return outputs
        if operation in ("extract","organize","remove"):
            if operation=="remove":
                if not options.get("pages","").strip(): raise UserError("请输入需要删除的页面。")
                selected=[i for i in range(len(doc)) if i not in selected]
            if not selected: raise UserError("操作后没有剩余页面，请至少保留一页。")
            doc.select(selected)
        elif operation=="compress": compress_doc(doc,options)
        elif operation=="repair": pass  # MuPDF recovers broken xrefs on open; full save rebuilds structure.
        elif operation=="ocr":
            recognized=ocr_doc(doc,options)
            progress(f"识别完成，写入 {recognized} 行文字",.95)
        elif operation=="pdfa":
            with tempfile.TemporaryDirectory(prefix="aipdf-pdfa-") as temp:
                unlocked=Path(temp)/"source.pdf"; save_pdf(doc,unlocked)
                return office_convert(unlocked,dest,options.get("pdfaLevel","2b"))
        elif operation=="pdf_to_images":
            outputs=[]
            if options.get("mode")=="embedded":
                seen=set()
                for i in selected:
                    for info in doc[i].get_images(full=True):
                        xref=info[0]
                        if xref in seen: continue
                        seen.add(xref); raw=doc.extract_image(xref)
                        path=out/f"{stem}-image-{xref}.{raw['ext']}"; path.write_bytes(raw["image"]); outputs.append(path)
                if not outputs: raise UserError("所选页面没有可提取的嵌入图片。")
            else:
                for i in selected:
                    path=out/f"{stem}-page-{i+1:03d}.{options.get('format','jpg')}"
                    doc[i].get_pixmap(dpi=int(number(options,"dpi",144,36,600)),colorspace=fitz.csRGB).save(path)
                    outputs.append(path)
            return outputs
        elif operation=="pdf_to_word":
            dest=dest.with_suffix(".docx"); export_word(doc,options,dest); return [dest]
        elif operation=="pdf_to_ppt":
            dest=dest.with_suffix(".pptx"); export_ppt(doc,options,dest); return [dest]
        elif operation=="pdf_to_excel":
            dest=dest.with_suffix(".xlsx"); export_excel(doc,dest); return [dest]
        elif operation=="markdown":
            dest=dest.with_suffix(".md"); export_markdown(doc,options,dest); return [dest]
        elif operation=="protect":
            password=options.get("newPassword","")
            if not password: raise UserError("请设置非空的打开密码。")
            permissions=fitz.PDF_PERM_ACCESSIBILITY
            if flag(options,"allowPrint",True): permissions|=fitz.PDF_PERM_PRINT|fitz.PDF_PERM_PRINT_HQ
            if flag(options,"allowCopy"): permissions|=fitz.PDF_PERM_COPY
            doc.save(dest,garbage=4,deflate=True,encryption=fitz.PDF_ENCRYPT_AES_256,owner_pw=uuid.uuid4().hex,user_pw=password,permissions=permissions)
            with fitz.open(dest) as check:
                if not check.needs_pass or not check.authenticate(password): raise UserError("加密输出验证失败。")
            return [dest]
        elif operation=="unlock": pass
        elif operation=="forms":
            radio=form_update(doc,options)
            save_pdf(doc,dest)
            if radio=="radio": radio_group(dest,options)
            return [dest]
        else:
            hits=0
            for j,i in enumerate(selected):
                page=doc[i]
                if operation in ("edit","sign","numbers","watermark","redact") and page.rotation:
                    page.remove_rotation()
                if operation=="rotate": page.set_rotation((page.rotation+int(options.get("angle",90)))%360)
                elif operation=="crop":
                    rect=area(page,options)
                    offset=page.cropbox_position
                    rect=fitz.Rect(rect.x0+offset.x,rect.y0+offset.y,rect.x1+offset.x,rect.y1+offset.y)
                    page.set_cropbox(rect)
                elif operation=="numbers":
                    text=options.get("format","{n} / {total}").replace("{n}",str(int(number(options,"start",1))+j)).replace("{total}",str(len(selected)))
                    size=number(options,"fontsize",11,1,200)
                    insert_text(page,position_rect(page,options,240,size*2.2),text,size,align=1)
                elif operation=="watermark":
                    opacity=number(options,"opacity",.2,0,1)
                    if options.get("image"):
                        rect=position_rect(page,options,220,100)
                        watermark=load_image(options["image"],preserve_alpha=True)
                        watermark.putalpha(watermark.getchannel("A").point(lambda a:int(a*opacity)))
                        buf=io.BytesIO(); watermark.save(buf,format="PNG")
                        page.insert_image(rect,stream=buf.getvalue())
                    else:
                        size=number(options,"fontsize",42,1,200)
                        rect=position_rect(page,options,page.rect.width-48,size*2.2)
                        insert_text(page,rect,options.get("text",""),size,(.35,.38,.45),opacity,1)
                elif operation in ("edit","sign"):
                    rect=area(page,options)
                    mode=options.get("mode","text")
                    if (operation=="sign" or mode=="ink") and options.get("strokes"):
                        # Signature strokes are normalized to the signature canvas.
                        for stroke in options["strokes"]:
                            points=[fitz.Point(rect.x0+p[0]*rect.width,rect.y0+p[1]*rect.height) for p in stroke]
                            if len(points)>1: page.draw_polyline(points,color=(.1,.15,.25),width=1.6)
                    elif options.get("image") and (operation=="sign" or mode=="image"):
                        image=load_image(options["image"],preserve_alpha=True); buf=io.BytesIO(); image.save(buf,format="PNG")
                        page.insert_image(rect,stream=buf.getvalue())
                    elif mode=="image": raise UserError("请选择要添加的图片。")
                    elif mode=="rectangle": page.draw_rect(rect,color=rgb(options.get("color","#3159D9")),width=2)
                    elif mode=="highlight": page.draw_rect(rect,color=None,fill=(1,.84,.1),fill_opacity=.3)
                    else: insert_text(page,rect,options.get("text",""),number(options,"fontsize",18,1,200),rgb(options.get("color","#3159D9")))
                elif operation=="redact":
                    terms=[t.strip() for t in options.get("text","").splitlines() if t.strip()]
                    rects=[rect for term in terms for rect in page.search_for(term)] if terms else [area(page,options)]
                    for rect in rects: page.add_redact_annot(rect,fill=(0,0,0)); hits+=1
                    if rects: page.apply_redactions(images=2,graphics=2,text=0)
                else: raise UserError("未识别的工具。")
            if operation=="redact":
                if not hits: raise UserError("没有匹配到敏感文字，未生成输出。请检查关键词或改用区域遮盖。")
                doc.scrub(attached_files=True,clean_pages=True,embedded_files=True,hidden_text=True,javascript=True,metadata=True,redactions=True,remove_links=True,reset_fields=True,reset_responses=True,thumbnails=True,xml_metadata=True)
                for page in doc:
                    for annotation in list(page.annots() or []): page.delete_annot(annotation)
                    for widget in list(page.widgets() or []): page.delete_widget(widget)
                doc.set_toc([])
        save_pdf(doc,dest)
        if operation=="compress" and dest.stat().st_size>=Path(source).stat().st_size and not doc.needs_pass:
            shutil.copyfile(source,dest)
        return [dest]
    finally:
        doc.close()


def process(operation, files, options, out):
    if operation in ("images_to_pdf","scan"):
        return images_pdf(files,options,out/("扫描文档.pdf" if operation=="scan" else "图片文档.pdf"),scan=operation=="scan")
    if operation=="compare": return compare_pdfs(files,options,out)
    if operation=="merge":
        if len(files)<2: raise UserError("请至少添加两份 PDF。")
        merged=fitz.open()
        for i,source in enumerate(files):
            progress(f"正在合并 {i+1}/{len(files)}",i/len(files))
            with open_pdf(source,options.get("password","")) as doc:
                merged.insert_pdf(doc,join_duplicates=False)
        dest=out/"合并文档.pdf"; save_pdf(merged,dest); merged.close(); return [dest]
    outputs=[]
    for i,path in enumerate(files):
        progress(f"正在处理文件 {i+1}/{len(files)}",i/len(files))
        outputs.extend(single_pdf(operation,path,options,out,i+1))
    return outputs


def dispatch(request):
    operation=request.get("operation", "")
    if operation=="health": return health()
    if operation=="inspect": return inspect_file(request["files"][0],request.get("options",{}).get("password",""))
    # 翻译产出新任务目录且密钥只经 stdin 传入；它在 BY_ID 之前处理，自建
    # 文件/扩展名/输出目录/配置等价校验，与批注保存同属专用 dispatch。
    if operation=="translate": return translate_request(request)
    # 划词（选中即译）轻量 op：无文件、无输出目录，只翻一段文本回一个字符串。
    if operation=="translate_text": return translate_text_request(request)
    # 批注保存就地写回原文件，无输出目录且非 catalog 工具，必须在 BY_ID/outputDir
    # 校验之前处理；其内部自建 file/扩展名/multiple 等价校验（M-1）。
    if operation in ANNOTATION_OPERATIONS: return save_annotations_request(request,operation)
    if operation not in BY_ID: raise UserError("未知的工具。")
    files=[str(Path(p).expanduser().resolve()) for p in request.get("files",[])]
    url=request.get("options",{}).get("url","").strip() if operation=="html_to_pdf" else ""
    if not files and not url: raise UserError("请先选择文件。")
    if url and not re.match(r"^https?://[^\s]+$",url): raise UserError("网页地址必须以 https:// 或 http:// 开头。")
    for f in files:
        if not Path(f).is_file(): raise UserError(f"文件不存在：{Path(f).name}")
        if Path(f).suffix.lower().lstrip(".") not in BY_ID[operation]["extensions"]:
            raise UserError(f"{Path(f).name} 的格式不适用于此工具。")
    if not BY_ID[operation]["multiple"] and len(files)>1: raise UserError("此工具一次处理一份文件。")
    options={f["key"]:f["value"] for f in BY_ID[operation]["fields"]}
    options.update(request.get("options",{}))
    if url: files=[url]
    parent=Path(request.get("outputDir","")).expanduser()
    if not parent.is_absolute() or not parent.is_dir(): raise UserError("请选择有效的输出文件夹。")
    out=parent/("AIPDF-"+time.strftime("%Y%m%d-%H%M%S")+"-"+uuid.uuid4().hex[:6])
    out.mkdir(mode=0o700)
    started=time.monotonic()
    try:
        if operation=="workflow":
            steps=options.get("steps",[])
            if not steps: raise UserError("请至少添加一个工作流程步骤。")
            if len(steps)>20: raise UserError("每个流程最多 20 步。")
            current=files
            allowed={"merge","split","extract","remove","organize","compress","repair","ocr","rotate","numbers","watermark","crop","unlock","protect","redact"}
            for i,step in enumerate(steps):
                op=step.get("operation","")
                if op not in allowed: raise UserError("流程中包含不支持的步骤。")
                stepdir=out/f"{i+1:02d}-{op}"; stepdir.mkdir()
                opts={f["key"]:f["value"] for f in BY_ID[op]["fields"]}
                opts.update(step.get("options",{}))
                opts.setdefault("password",options.get("password",""))
                progress(f"步骤 {i+1}/{len(steps)}：{BY_ID[op]['name']}",i/len(steps))
                current=[str(p) for p in process(op,current,opts,stepdir)]
                if op=="protect": options["password"]=opts.get("newPassword","")
            outputs=[Path(p) for p in current]
        else:
            outputs=process(operation,files,options,out)
        if not outputs or any(not p.is_file() or not p.stat().st_size for p in outputs): raise UserError("未生成有效文件。")
        return {"ok":True,"outputs":[str(p) for p in outputs],"outputDir":str(out),
                "inputBytes":sum(Path(p).stat().st_size for p in files if not p.startswith(("http://","https://"))),"outputBytes":sum(p.stat().st_size for p in outputs),
                "elapsed":round(time.monotonic()-started,2)}
    except Exception as error:
        # Retain any completed results, but do not label the job successful.
        raise UserError(f"{str(error) if isinstance(error,UserError) else '处理失败，请检查文件格式和参数。'}\n本次输出目录：{out}") from error


def _failure_result(error):
    """把异常转成 EngineResult（ok/error + 可选 errorCode）形状，供 WPF 桥解析。"""
    result={"ok":False,"error":str(error) if isinstance(error,UserError) else "请求格式不正确。"}
    code=getattr(error,"code",None)
    if isinstance(error,UserError) and code:
        result["errorCode"]=code
    return result


def main():
    signal.signal(signal.SIGTERM,stop)
    signal.signal(signal.SIGINT,stop)
    try:
        # 每次引擎启动时清扫历史崩溃残留的就地保存 temp（M-4）。
        cleanup_temp_dirs()
    except Exception:
        pass
    try:
        request=json.load(sys.stdin)
        # Third-party parsers may print notices; keep stdout strictly machine-readable.
        with redirect_stdout(sys.stderr):
            result=dispatch(request)
    except (UserError,KeyError,ValueError,TypeError) as error:
        result=_failure_result(error)
    except Exception:
        result={"ok":False,"error":"处理引擎发生错误。请检查文件是否正常并重新尝试。"}
    print(json.dumps(result,ensure_ascii=False),flush=True)
    return 0 if result.get("ok") else 1


if __name__=="__main__":
    sys.exit(main())
