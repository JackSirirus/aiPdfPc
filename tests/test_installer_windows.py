"""Windows 安装器测试：镜像上游 tests/test_installer.py 的 9 项安装语义。

上游测试驱动 scripts/install.py 的 deploy_app()；本文件驱动 Install.ps1（T21 产物），
通过 subprocess 参数数组调用 powershell -File，不拼接 shell。全部安装走
-InstallDir/-ShortcutDir 沙箱，绝不触碰真实 %LOCALAPPDATA% 或开始菜单。

与上游 9 项的映射（每个测试方法的 docstring 首行标注）：
  上游 test_new_install                                → test_01 / test_05 / test_06
  上游 test_upgrade_keeps_backup                       → test_02 / test_03
  上游 test_failed_verification_preserves_old_app      → test_04（子测试 A：布局校验失败）
  上游 test_failed_replacement_rolls_back              → test_04（子测试 B：换入后失败回滚）
  上游 test_unrelated_app_is_not_overwritten           → test_10
  上游 test_symlink_target_is_not_followed             → test_11
  上游 test_runtime_changes_when_python_or_lock_changes→ test_07（安装信息不含绝对路径）
  上游 test_incomplete_runtime_is_not_reused           → test_02（残缺安装被整体替换）
  上游 test_bundle_cannot_reference_source_or_escape   → test_07（安装树无源路径泄漏）
  Windows 专属（上游无对应测试）：test_08 卸载清理、test_09 前置缺失指引文案

沙箱来源布局是合成的：app/helper 为哑文件，engine/backend 复制仓库真实
engine.py/catalog.py/catalog.json，因此引擎冒烟（test_06）无需 dist 构建产物。
"""
# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
INSTALL_PS1 = REPO / "Install.ps1"
VENV_PYTHON = REPO / ".venv" / "Scripts" / "python.exe"
INSTALL_TIMEOUT = 180

# 与 Install.ps1「校验安装布局」段一致的 8 项关键文件（相对安装根）。
LAYOUT_FILES = (
    "app/AIPDF.Windows.exe",
    "app/AIPDF.Windows.dll",
    "app/catalog.json",
    "engine/backend/engine.py",
    "engine/backend/catalog.py",
    "engine/backend/catalog.json",
    "helper/WindowsOcrHelper.exe",
    "README-Windows.txt",
)

SOURCE_README = "AIPDF windows test dist\n"
OLD_MARKER = "OLD-INSTALL-MARKER\n"


def run_installer(script, args, env=None):
    """参数数组调用 powershell，不经过 shell；输出按 UTF-8 解码（脚本已设
    Console::OutputEncoding=UTF8，重定向两路字节均为 UTF-8，见 T21 验证）。"""
    cmd = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script), *args]
    proc = subprocess.run(cmd, capture_output=True, timeout=INSTALL_TIMEOUT, env=env)
    return proc.returncode, proc.stdout.decode("utf-8", "replace"), proc.stderr.decode("utf-8", "replace")


def run_engine(python_exe, engine_py, request):
    """引擎子进程冒烟：stdin 单行 JSON 请求 → stdout 单行 JSON 结果。"""
    proc = subprocess.run(
        [str(python_exe), str(engine_py)],
        input=json.dumps(request).encode("utf-8"),
        capture_output=True,
        timeout=INSTALL_TIMEOUT,
    )
    lines = [line for line in proc.stdout.decode("utf-8", "replace").splitlines() if line.strip()]
    return proc.returncode, (json.loads(lines[-1]) if lines else {})


@unittest.skipUnless(sys.platform == "win32", "Windows installer (Install.ps1) only")
class InstallerWindowsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="aipdf-install-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        # 路径带空格，镜像上游 "Applications with spaces" 的空白容错意图。
        self.parent = self.root / "install parent with spaces"
        self.parent.mkdir()
        self.target = self.parent / "AIPDF"
        self.shortcuts = self.root / "start menu with spaces"
        self.shortcuts.mkdir()
        self.source = self.root / "source with spaces" / "aipdf-dist"
        self.make_source(self.source, complete=True)

    # ------------------------------------------------------------------
    # 沙箱来源布局（合成 dist）：哑二进制 + 真实引擎三件套 + Install.ps1 副本。
    # Install.ps1 的 Resolve-SourceLayout 候选 1（$PSScriptRoot）命中该目录。
    # ------------------------------------------------------------------
    def make_source(self, src, complete=True):
        (src / "app").mkdir(parents=True)
        (src / "app" / "AIPDF.Windows.exe").write_bytes(b"MZ\x90\x00")
        (src / "app" / "AIPDF.Windows.dll").write_bytes(b"")
        shutil.copyfile(REPO / "backend" / "catalog.json", src / "app" / "catalog.json")
        (src / "engine" / "backend").mkdir(parents=True)
        for name in ("engine.py", "catalog.py", "catalog.json"):
            shutil.copyfile(REPO / "backend" / name, src / "engine" / "backend" / name)
        (src / "helper").mkdir()
        if complete:
            (src / "helper" / "WindowsOcrHelper.exe").write_bytes(b"")
        (src / "README-Windows.txt").write_text(SOURCE_README, encoding="utf-8")
        shutil.copyfile(INSTALL_PS1, src / "Install.ps1")
        return src

    def install(self, source=None, target=None, extra=()):
        script = (source or self.source) / "Install.ps1"
        args = ["-InstallDir", str(target or self.target), "-ShortcutDir", str(self.shortcuts)]
        return run_installer(script, [*args, *extra])

    def uninstall(self, target, extra=()):
        args = ["-Uninstall", "-InstallDir", str(target), "-ShortcutDir", str(self.shortcuts)]
        return run_installer(self.source / "Install.ps1", [*args, *extra])

    # ------------------------------------------------------------------
    # 断言辅助
    # ------------------------------------------------------------------
    def assert_layout(self, root):
        for rel in LAYOUT_FILES:
            self.assertTrue((root / rel).is_file(), f"安装后缺少关键文件：{rel}")
        info = root / "install-info.json"
        self.assertTrue(info.is_file(), "缺少 install-info.json")
        data = json.loads(info.read_text(encoding="utf-8"))
        self.assertEqual(data.get("layout"), "aipdf-windows")

    def backups(self, parent=None):
        parent = parent or self.parent
        return sorted(p for p in parent.iterdir() if p.is_dir() and p.name.startswith("AIPDF.bak-"))

    def assert_no_staging(self, parent=None):
        parent = parent or self.parent
        residue = [p.name for p in parent.iterdir() if p.name.startswith(".aipdf-install-")]
        self.assertEqual(residue, [], "暂存目录残留")

    def read_target_readme(self):
        return (self.target / "README-Windows.txt").read_bytes()

    # ------------------------------------------------------------------
    # ① 布局断言（安装后关键文件）
    # 上游映射：test_new_install —— 全新安装成功、目标内容就位、来源保持原样。
    # ------------------------------------------------------------------
    def test_01_new_install_layout(self):
        rc, out, err = self.install()
        self.assertEqual(rc, 0, f"安装失败：{err}")
        self.assert_layout(self.target)
        # 上游断言 source 仍在：安装只读来源，不移动/删除。
        self.assertTrue((self.source / "README-Windows.txt").read_text(encoding="utf-8") == SOURCE_README)
        self.assertTrue((self.source / "Install.ps1").is_file())
        # 全新安装：无备份、无暂存残留。
        self.assertEqual(self.backups(), [])
        self.assert_no_staging()

    # ------------------------------------------------------------------
    # ② 幂等/重复安装（含残缺安装被整体替换）
    # 上游映射：test_upgrade_keeps_backup（重复安装成功）+
    #           test_incomplete_runtime_is_not_reused（残缺安装不被复用，整体换新）。
    # ------------------------------------------------------------------
    def test_02_reinstall_replaces_incomplete_install(self):
        self.assertEqual(self.install()[0], 0)
        # 破坏成"残缺安装"：缺关键文件但 install-info.json 仍在（Assert-TargetSafe 认得它）。
        (self.target / "app" / "AIPDF.Windows.exe").unlink()
        rc, out, err = self.install()
        self.assertEqual(rc, 0, f"重复安装失败：{err}")
        self.assert_layout(self.target)  # 残缺被整体替换，8 项关键文件恢复
        self.assertEqual(len(self.backups()), 1, "残缺旧安装应恰好产生 1 个备份")
        self.assert_no_staging()
        # 幂等：再装一次仍成功，备份线性增长。
        rc, out, err = self.install()
        self.assertEqual(rc, 0, f"第三次安装失败：{err}")
        self.assert_layout(self.target)
        self.assertEqual(len(self.backups()), 2)
        self.assert_no_staging()

    # ------------------------------------------------------------------
    # ③ 备份生成与恢复（备份内容 = 旧版）
    # 上游映射：test_upgrade_keeps_backup —— backup 内容为 "old"，目标为 "new"。
    # ------------------------------------------------------------------
    def test_03_backup_preserves_old_content(self):
        self.assertEqual(self.install()[0], 0)
        readme_path = self.target / "README-Windows.txt"
        # write_bytes 而非 write_text：Windows 文本模式会把 \n 翻译成 \r\n，
        # 破坏下面的字节级断言。
        readme_path.write_bytes((SOURCE_README + OLD_MARKER).encode("utf-8"))
        rc, out, err = self.install()
        self.assertEqual(rc, 0, f"升级安装失败：{err}")
        backups = self.backups()
        self.assertEqual(len(backups), 1)
        self.assertRegex(backups[0].name, r"^AIPDF\.bak-\d{8}-\d{6}(-\d+)?$")
        # 备份里是旧内容（含标记），目标是新内容（无标记）。
        self.assertIn(
            OLD_MARKER.encode("utf-8"),
            (backups[0] / "README-Windows.txt").read_bytes(),
        )
        self.assertNotIn(OLD_MARKER.encode("utf-8"), self.read_target_readme())
        self.assertTrue((backups[0] / "install-info.json").is_file(), "备份应保留旧安装信息")
        self.assert_no_staging()

    # ------------------------------------------------------------------
    # ④ 回滚（故障注入）
    # 上游映射：test_failed_verification_preserves_old_app（子测试 A：布局校验
    # 失败发生在触碰目标之前，旧安装原样）+ test_failed_replacement_rolls_back
    # （子测试 B：换入之后失败——快捷方式写入被 icacls 拒绝——自动回滚，旧安装
    # 逐字节还原）。故障注入用真实 ACL，不改被测脚本。
    # ------------------------------------------------------------------
    def test_04_rollback_on_failure(self):
        # 子测试 A：来源缺 helper\WindowsOcrHelper.exe → 布局校验失败（换入前）。
        self.assertEqual(self.install()[0], 0)
        v1_readme = self.read_target_readme()
        bad_source = self.root / "bad source with spaces" / "aipdf-dist"
        self.make_source(bad_source, complete=False)
        rc, out, err = self.install(source=bad_source)
        self.assertEqual(rc, 1, "残缺来源的安装应失败")
        self.assertIn("安装布局不完整", err)
        self.assertEqual(self.read_target_readme(), v1_readme, "校验失败不得触碰旧安装")
        self.assertEqual(self.backups(), [], "校验失败不应产生备份")
        self.assert_no_staging()

        # 子测试 B：换入后失败（快捷方式目录拒绝写入）→ 回滚还原旧安装。
        user = os.environ.get("USERNAME") or os.environ.get("USER") or "user"
        deny = subprocess.run(
            ["icacls", str(self.shortcuts), "/deny", f"{user}:(OI)(CI)W"],
            capture_output=True,
        )
        if deny.returncode != 0:
            self.skipTest(f"icacls 注入失败，无法模拟换入后故障：{deny.stderr.decode('utf-8', 'replace')}")
        try:
            v1_info = (self.target / "install-info.json").read_bytes()
            rc, out, err = self.install()
            self.assertEqual(rc, 1, "快捷方式写入被拒时安装应失败")
            self.assertIn("安装未完成", err)
            # 旧安装逐字节还原（README 与 install-info 均为换入前内容）。
            self.assertEqual(self.read_target_readme(), v1_readme)
            self.assertEqual((self.target / "install-info.json").read_bytes(), v1_info)
            self.assert_layout(self.target)
            self.assertEqual(self.backups(), [], "回滚应把备份还原为安装目录")
            self.assert_no_staging()
        finally:
            subprocess.run(["icacls", str(self.shortcuts), "/remove:d", user], capture_output=True)

    # ------------------------------------------------------------------
    # ⑤ 快捷方式存在
    # 上游映射：test_new_install 的安装语义面（install.py 同样创建快捷方式）。
    # 快捷方式落在 -ShortcutDir 沙箱，绝不写真实开始菜单。
    # ------------------------------------------------------------------
    def test_05_shortcut_created(self):
        rc, out, err = self.install()
        self.assertEqual(rc, 0, f"安装失败：{err}")
        lnk = self.shortcuts / "AIPDF.lnk"
        self.assertTrue(lnk.is_file(), "未创建快捷方式")
        probe = (
            "(New-Object -ComObject WScript.Shell)"
            f".CreateShortcut('{lnk}').TargetPath"
        )
        proc = subprocess.run(["powershell", "-NoProfile", "-Command", probe], capture_output=True)
        target_path = proc.stdout.decode("utf-8", "replace").strip()
        expected = str(self.target / "app" / "AIPDF.Windows.exe")
        self.assertEqual(
            os.path.normcase(target_path),
            os.path.normcase(expected),
            "快捷方式目标应指向安装目录内的启动器",
        )

    # ------------------------------------------------------------------
    # ⑥ 启动器可执行（引擎导入冒烟）
    # 上游映射：test_new_install 的功能面。安装树内的 engine.py 用项目 venv
    # 解释器跑一次 health 请求，证明安装布局里的引擎可独立导入运行。
    # （WPF 启动器进程冒烟依赖本机 DOTNET_ROOT/图形环境，已由 T21 harness
    # 单独验证；此处按任务定义聚焦引擎导入冒烟。）
    # ------------------------------------------------------------------
    def test_06_engine_smoke_from_installed_tree(self):
        if not VENV_PYTHON.is_file():
            self.skipTest(f"缺少项目解释器：{VENV_PYTHON}")
        rc, out, err = self.install()
        self.assertEqual(rc, 0, f"安装失败：{err}")
        code, data = run_engine(VENV_PYTHON, self.target / "engine" / "backend" / "engine.py", {"operation": "health"})
        self.assertEqual(code, 0, f"引擎 health 退出码异常：{err}")
        self.assertTrue(data.get("ok"), f"引擎 health 未通过：{data}")
        self.assertIn("engine", data, "health 应报告引擎版本（证明真实导入）")

    # ------------------------------------------------------------------
    # ⑦ 无绝对路径泄漏（安装树 grep）
    # 上游映射：test_bundle_cannot_reference_source_or_escape_runtime（安装产物
    # 不得引用来源路径）+ test_runtime_changes_when_python_or_lock_changes 的
    # assertNotIn("/")（安装标识不含路径，保证可整体搬移）。
    # ------------------------------------------------------------------
    def test_07_no_absolute_path_leak(self):
        rc, out, err = self.install()
        self.assertEqual(rc, 0, f"安装失败：{err}")
        needles = [os.path.normcase(str(p)) for p in (REPO, self.source, self.root)]
        text_suffixes = {".py", ".json", ".txt", ".ps1", ".cmd", ".bat", ".md"}
        for path in self.target.rglob("*"):
            if path.is_file() and path.suffix.lower() in text_suffixes:
                text = path.read_bytes().decode("utf-8", "replace").lower()
                for needle in needles:
                    self.assertNotIn(needle, text, f"{path.relative_to(self.target)} 泄漏了绝对路径")
        # install-info.json 只含布局标记与时间，不含任何盘符路径。
        info_text = (self.target / "install-info.json").read_text(encoding="utf-8")
        self.assertIsNone(re.search(r"[a-z]:[\\/]", info_text.lower()), "install-info.json 含盘符路径")

    # ------------------------------------------------------------------
    # ⑧ -Uninstall 清理
    # Windows 专属：上游 9 项无卸载测试；语义对应 install.py/Install.ps1 的
    # 卸载路径（删安装目录 + 同级备份 + 快捷方式，非 AIPDF 目录拒绝删除）。
    # ------------------------------------------------------------------
    def test_08_uninstall_cleanup(self):
        self.assertEqual(self.install()[0], 0)
        self.assertEqual(self.install()[0], 0)  # 制造 1 个备份
        self.assertEqual(len(self.backups()), 1)
        rc, out, err = self.uninstall(self.target)
        self.assertEqual(rc, 0, f"卸载失败：{err}")
        self.assertFalse(self.target.exists(), "卸载后安装目录应删除")
        self.assertEqual(self.backups(), [], "卸载应清理同级备份")
        self.assertFalse((self.shortcuts / "AIPDF.lnk").exists(), "卸载应清理快捷方式")
        # 负例：非 AIPDF 目录拒绝卸载，内容原样。
        stranger = self.parent / "NotAIPDF"
        stranger.mkdir()
        precious = stranger / "precious.txt"
        precious.write_text("keep me", encoding="utf-8")
        rc, out, err = self.uninstall(stranger)
        self.assertEqual(rc, 1, "非 AIPDF 目录的卸载应被拒绝")
        self.assertIn("非 AIPDF", err)
        self.assertEqual(precious.read_text(encoding="utf-8"), "keep me")

    # ------------------------------------------------------------------
    # ⑨ 前置缺失的指引文案
    # Windows 专属：上游 install.py 前置检查缺失时给官方下载指引；Install.ps1
    # 镜像该语义。用清洗过的环境变量模拟缺失，不改被测脚本；环境无法模拟时
    # 打印原因跳过（不静默）。
    # ------------------------------------------------------------------
    def test_09_prerequisite_guidance(self):
        # 子测试 A：PATH 只留 System32 → Python 缺失 → 指引文案。
        scrubbed = os.environ.copy()
        scrubbed["PATH"] = str(Path(os.environ["SystemRoot"]) / "System32")
        probe = subprocess.run(
            ["powershell", "-NoProfile", "-Command",
             "@('py','python','python3') | ForEach-Object { $c = Get-Command $_ -ErrorAction SilentlyContinue; if ($c) { $c.Source } }"],
            capture_output=True, env=scrubbed,
        )
        found = probe.stdout.decode("utf-8", "replace").strip()
        if found:
            self.skipTest(f"清洗 PATH 后仍能找到 Python，无法模拟缺失：{found}")
        rc, out, err = run_installer(INSTALL_PS1, ["-Check"], env=scrubbed)
        self.assertEqual(rc, 1, "缺 Python 时 -Check 应失败")
        self.assertIn("Python 3.12", err)
        self.assertIn("python.org", err)

        # 子测试 B：PATH 只留 venv（有 Python 无 dotnet）+ DOTNET_ROOT 移除 +
        # LOCALAPPDATA 重定向 → .NET Desktop Runtime 缺失 → 指引文案。
        if os.path.isfile(r"C:\Program Files\dotnet\dotnet.exe"):
            self.skipTest("本机存在机器级 dotnet，无法用环境变量模拟缺失")
        if not VENV_PYTHON.is_file():
            self.skipTest(f"缺少项目解释器：{VENV_PYTHON}")
        scrubbed2 = os.environ.copy()
        scrubbed2["PATH"] = str(VENV_PYTHON.parent)
        scrubbed2.pop("DOTNET_ROOT", None)
        fake_lad = self.root / "empty LocalAppData"
        fake_lad.mkdir()
        scrubbed2["LOCALAPPDATA"] = str(fake_lad)
        rc, out, err = run_installer(INSTALL_PS1, ["-Check"], env=scrubbed2)
        self.assertEqual(rc, 1, "缺 .NET Desktop Runtime 时 -Check 应失败")
        self.assertIn(".NET 8 Desktop Runtime", err)
        self.assertIn("dotnet.microsoft.com", err)

        # 子测试 C（正例）：本机前置齐全时 -Check 通过。
        rc, out, err = run_installer(INSTALL_PS1, ["-Check"])
        self.assertEqual(rc, 0, f"本机 -Check 应通过：{err}")
        self.assertIn("环境检查通过", out)

    # ------------------------------------------------------------------
    # 上游映射：test_unrelated_app_is_not_overwritten —— 目标是无关内容时
    # 拒绝安装（"其他应用"语义 → "非 AIPDF 目录"），目标原样。
    # ------------------------------------------------------------------
    def test_10_unrelated_dir_rejected(self):
        self.target.mkdir()
        precious = self.target / "precious.txt"
        precious.write_text("keep me", encoding="utf-8")
        rc, out, err = self.install()
        self.assertEqual(rc, 1, "非 AIPDF 目标应拒绝安装")
        self.assertIn("非 AIPDF", err)
        self.assertEqual(precious.read_text(encoding="utf-8"), "keep me", "无关目录内容不得被覆盖")
        self.assertEqual(self.backups(), [])
        self.assert_no_staging()

    # ------------------------------------------------------------------
    # 上游映射：test_symlink_target_is_not_followed —— 目标是链接时拒绝安装，
    # 且不穿透链接改动其指向（junction 指向来源目录，来源内容必须原样）。
    # ------------------------------------------------------------------
    def test_11_junction_target_rejected(self):
        import _winapi

        _winapi.CreateJunction(str(self.source), str(self.target))
        try:
            rc, out, err = self.install()
            self.assertEqual(rc, 1, "链接目标应拒绝安装")
            self.assertIn("链接", err)
            # 链接未被穿透：来源内容原样（上游断言 source 数据仍为 "new"）。
            self.assertEqual(
                (self.source / "README-Windows.txt").read_text(encoding="utf-8"),
                SOURCE_README,
            )
            self.assertEqual(self.backups(), [])
            self.assert_no_staging()
        finally:
            if self.target.exists():
                try:
                    os.rmdir(self.target)  # 只移除 junction 重解析点，不动指向内容
                except OSError:
                    pass


if __name__ == "__main__":
    unittest.main(verbosity=2)
