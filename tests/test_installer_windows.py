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
  Windows 专属（上游无对应测试）：test_08 卸载清理、test_09 前置缺失指引文案、
  test_14 卸载翻译凭据清理（隔离前缀 + 合成凭据，绝不触碰真实凭据）

沙箱来源布局是合成的：app/helper 为哑文件，engine/backend 复制仓库真实
engine.py/catalog.py/catalog.json，因此引擎冒烟（test_06）无需 dist 构建产物。

模式参数化（Task 7）：基类 InstallerWindowsTests 用框架依赖夹具（无
engine\python\python.exe，前置检查强制系统 Python 与 .NET Desktop Runtime）；
子类 InstallerSelfContainedWindowsTests 把夹具覆写为自包含布局（内置
engine\python\python.exe、app\hostfxr.dll、engine\site-packages\fitz），
同一套安装语义在两种模式下重复验证。缺 Python 拒装（test_09）仅适用
框架依赖模式；自包含模式在无系统 Python 时 -Check 与安装照常通过。
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


def run_installer(script, args, env=None, input_data=None):
    """参数数组调用 powershell，不经过 shell；输出按 UTF-8 解码（脚本已设
    Console::OutputEncoding=UTF8，重定向两路字节均为 UTF-8，见 T21 验证）。

    input_data 非 None 时以管道喂给子进程 stdin（应答卸载的凭据确认提示）；
    默认 None 不重定向 stdin，与既有用例行为一致。"""
    cmd = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script), *args]
    proc = subprocess.run(cmd, capture_output=True, timeout=INSTALL_TIMEOUT, env=env, input=input_data)
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
    # False = 框架依赖夹具（无 engine\python\python.exe）；自包含子类覆写为 True。
    # 用类属性而非 setUp 赋值：子类覆写不会被基类 setUp 顺序覆盖。
    self_contained = False

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
    def make_source(self, src, complete=True, self_contained=None):
        # self_contained=None 跟随当前测试类的模式（框架依赖 / 自包含）。
        if self_contained is None:
            self_contained = getattr(self, "self_contained", False)
        (src / "app").mkdir(parents=True)
        (src / "app" / "AIPDF.Windows.exe").write_bytes(b"MZ\x90\x00")
        (src / "app" / "AIPDF.Windows.dll").write_bytes(b"")
        if self_contained:
            # 自包含标记：.NET 运行时载体（hostfxr）随包内置。
            (src / "app" / "hostfxr.dll").write_bytes(b"")
        shutil.copyfile(REPO / "backend" / "catalog.json", src / "app" / "catalog.json")
        (src / "engine" / "backend").mkdir(parents=True)
        for name in ("engine.py", "catalog.py", "catalog.json"):
            shutil.copyfile(REPO / "backend" / name, src / "engine" / "backend" / name)
        if self_contained:
            # 内置嵌入式解释器与离线 site-packages（哑文件，布局断言用）。
            (src / "engine" / "python").mkdir(parents=True)
            (src / "engine" / "python" / "python.exe").write_bytes(b"MZ\x90\x00")
            (src / "engine" / "site-packages" / "fitz").mkdir(parents=True)
            (src / "engine" / "site-packages" / "fitz" / "__init__.py").write_text("", encoding="utf-8")
        (src / "helper").mkdir()
        if complete:
            (src / "helper" / "WindowsOcrHelper.exe").write_bytes(b"")
        (src / "README-Windows.txt").write_text(SOURCE_README, encoding="utf-8")
        shutil.copyfile(INSTALL_PS1, src / "Install.ps1")
        return src

    def install(self, source=None, target=None, extra=(), env=None, association=None):
        # 默认 -NoAssociation：常规安装语义用例绝不触碰真实注册表；
        # 关联用例显式传 association=<临时 ProgId> 才登记（AC-31.1 修正）。
        script = (source or self.source) / "Install.ps1"
        args = ["-InstallDir", str(target or self.target), "-ShortcutDir", str(self.shortcuts)]
        args += self._association_args(association)
        return run_installer(script, [*args, *extra], env=env)

    def uninstall(self, target, extra=(), association=None, skip_credentials=True, input_data=None):
        # 常规卸载用例默认 -SkipCredentialCleanup：绝不枚举/删除本机真实翻译凭据；
        # 凭据清理用例显式传 skip_credentials=False + 隔离的 -CredentialPrefix。
        args = ["-Uninstall", "-InstallDir", str(target), "-ShortcutDir", str(self.shortcuts)]
        args += self._association_args(association)
        if skip_credentials:
            args.append("-SkipCredentialCleanup")
        return run_installer(self.source / "Install.ps1", [*args, *extra], input_data=input_data)

    @staticmethod
    def _association_args(association):
        # association=None → 关闭关联写入；否则指定 ProgId（测试一律传临时 ProgId）。
        return ["-NoAssociation"] if association is None else ["-AssociationProgId", association]

    # ------------------------------------------------------------------
    # 断言辅助
    # ------------------------------------------------------------------
    def assert_layout(self, root):
        for rel in LAYOUT_FILES:
            self.assertTrue((root / rel).is_file(), f"安装后缺少关键文件：{rel}")
        if self.self_contained:
            # 自包含模式追加 3 项：内置解释器、.NET 运行时载体、离线 site-packages。
            self.assertTrue((root / "engine/python/python.exe").is_file(), "自包含安装缺少内置 Python")
            self.assertTrue((root / "app/hostfxr.dll").is_file(), "自包含安装缺少 .NET 运行时载体")
            self.assertTrue((root / "engine/site-packages/fitz").is_dir(), "自包含安装缺少 site-packages/fitz")
        info = root / "install-info.json"
        self.assertTrue(info.is_file(), "缺少 install-info.json")
        data = json.loads(info.read_text(encoding="utf-8"))
        self.assertEqual(data.get("layout"), "aipdf-windows")
        self.assertEqual(
            data.get("mode"),
            "self-contained" if self.self_contained else "framework-dependent",
            "install-info.json 应记录安装模式",
        )

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
        if self.self_contained:
            return self._selfcontained_prerequisite_guidance()

        # 框架依赖夹具专用脚本（无 engine\python\python.exe，前置检查强制）。
        # 不用仓库根 Install.ps1：本机 dist 可能是自包含产物，会跳过系统前置。
        fw_source = self.make_source(
            self.root / "fw source with spaces" / "aipdf-dist",
            complete=True, self_contained=False,
        )
        fw_script = fw_source / "Install.ps1"

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
        rc, out, err = run_installer(fw_script, ["-Check"], env=scrubbed)
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
        rc, out, err = run_installer(fw_script, ["-Check"], env=scrubbed2)
        self.assertEqual(rc, 1, "缺 .NET Desktop Runtime 时 -Check 应失败")
        self.assertIn(".NET 8 Desktop Runtime", err)
        self.assertIn("dotnet.microsoft.com", err)

        # 子测试 C（正例）：本机前置齐全时 -Check 通过（仓库根脚本）。
        rc, out, err = run_installer(INSTALL_PS1, ["-Check"])
        self.assertEqual(rc, 0, f"本机 -Check 应通过：{err}")
        self.assertIn("环境检查通过", out)

    def _selfcontained_prerequisite_guidance(self):
        """自包含模式：无系统 Python（清洗 PATH）时 -Check 仍通过。"""
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
        rc, out, err = run_installer(self.source / "Install.ps1", ["-Check"], env=scrubbed)
        self.assertEqual(rc, 0, f"自包含 -Check 不应依赖系统 Python：{err}")
        self.assertIn("安装模式：自包含", out)
        self.assertIn("环境检查通过", out)
        # 正例：正常环境同样通过。
        rc, out, err = run_installer(self.source / "Install.ps1", ["-Check"])
        self.assertEqual(rc, 0, f"自包含 -Check 应通过：{err}")
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

    # ------------------------------------------------------------------
    # ⑬ 文件关联注册/卸载（FR-31 / AC-31.1 修正）
    # 临时 ProgId 写入 HKCU\Software\Classes，断言 shell\open\command 命令串，
    # 并验证 -Uninstall 清理无残留；全程不写 .pdf 默认值、不碰 UserChoice。
    # 无论断言是否失败都 addCleanup 清理，绝不污染真实用户关联。
    # ------------------------------------------------------------------
    def test_13_association_register_and_cleanup(self):
        import uuid

        progid = "AIPDF.assoc.test." + uuid.uuid4().hex[:12]
        self.addCleanup(self._cleanup_association, progid)
        before_pdf_default = self._read_reg_default_or_none(r"Software\Classes\.pdf")

        rc, out, err = self.install(association=progid)
        self.assertEqual(rc, 0, f"安装失败：{err}")
        self.assertIn(".pdf 打开方式", out)

        # AC-31.1：命令串指向安装目录内启动器，且以 "%1" 占位文件参数。
        command = self._read_reg_default(rf"Software\Classes\{progid}\shell\open\command")
        expected_exe = str(self.target / "app" / "AIPDF.Windows.exe")
        self.assertEqual(command, f'"{expected_exe}" "%1"')

        # 非破坏式登记：仅加入 .pdf 的「打开方式」候选，不改写 .pdf 默认值。
        # OpenWithProgids 的值是空 REG_BINARY，winreg 读回 None，故断言存在性而非字节。
        self.assertTrue(self._openwith_value_exists(progid), "未登记 .pdf 打开方式候选")
        self.assertEqual(
            self._read_reg_default_or_none(r"Software\Classes\.pdf"),
            before_pdf_default,
            "安装不得改写 .pdf 默认值（不覆盖用户默认程序）",
        )

        # -Uninstall 清理：ProgId 树与打开方式候选均无残留。
        rc, out, err = self.uninstall(self.target, association=progid)
        self.assertEqual(rc, 0, f"卸载失败：{err}")
        self.assertFalse(self._reg_key_exists(rf"Software\Classes\{progid}"), "卸载后 ProgId 残留")
        self.assertFalse(self._openwith_value_exists(progid), "卸载后 OpenWithProgids 残留")

    # ------------------------------------------------------------------
    # ⑭ -Uninstall 翻译凭据清理（v1.1 决策「凭据命名与卸载清理」）
    # 用隔离前缀 + cmdkey 合成凭据（合成口令，非真实密钥），验证：
    #   未确认（stdin 无应答，等价自动化默认）→ 保留；
    #   确认 y → 删除匹配前缀的全部凭据并报告数量；
    #   再次卸载 → 零条时优雅提示。
    # 断言输出只列目标名、绝不含凭据口令；前缀之外的同名凭据（诱饵）不受影响；
    # 真实 AIPDF-Translate-* 全程不触碰（显式 -CredentialPrefix + 默认跳过开关）。
    # ------------------------------------------------------------------
    def test_14_uninstall_translation_credential_cleanup(self):
        import uuid

        prefix = "AIPDF.CT." + uuid.uuid4().hex[:10] + "-"
        targets = [prefix + "alpha-11111111", prefix + "beta-22222222"]
        decoy = "AIPDF.CT-other-" + uuid.uuid4().hex[:8]
        fake_secret = "test-only-fake-secret"
        for target in [*targets, decoy]:
            self.addCleanup(self._delete_credential, target)
            subprocess.run(
                ["cmdkey", f"/generic:{target}", "/user:test", f"/pass:{fake_secret}"],
                capture_output=True,
                check=True,
            )

        self.assertEqual(self.install()[0], 0)

        # 1) stdin 无应答 → 默认不删除；诱饵保留；输出不得泄漏凭据口令。
        rc, out, err = self.uninstall(
            self.target, extra=["-CredentialPrefix", prefix], skip_credentials=False, input_data=b"",
        )
        self.assertEqual(rc, 0, f"卸载失败：{err}")
        self.assertIn("已保留翻译凭据", out)
        self.assertTrue(self._credential_exists(targets[0]), "未确认时不得删除凭据")
        self.assertTrue(self._credential_exists(decoy), "非匹配前缀的凭据不得被删除")
        self.assertNotIn(fake_secret, out + err, "输出不得包含凭据内容")

        # 2) 确认 y → 删除全部匹配前缀的凭据并报告数量；诱饵保留。
        rc, out, err = self.uninstall(
            self.target, extra=["-CredentialPrefix", prefix], skip_credentials=False, input_data=b"y\n",
        )
        self.assertEqual(rc, 0, f"卸载失败：{err}")
        self.assertIn("发现 2 条", out, "确认前应先列出数量")
        self.assertIn(targets[0], out, "确认前应列出将删除的凭据名")
        self.assertIn("已删除 2/2 条", out)
        self.assertFalse(self._credential_exists(targets[0]), "确认后应删除凭据")
        self.assertFalse(self._credential_exists(targets[1]), "确认后应删除凭据")
        self.assertTrue(self._credential_exists(decoy), "非匹配前缀的凭据不得被删除")
        self.assertNotIn(fake_secret, out + err, "输出不得包含凭据内容")

        # 3) 零条匹配 → 优雅提示，退出码 0。
        rc, out, err = self.uninstall(
            self.target, extra=["-CredentialPrefix", prefix], skip_credentials=False, input_data=b"",
        )
        self.assertEqual(rc, 0, f"卸载失败：{err}")
        self.assertIn("无需清理", out)

    @staticmethod
    def _credential_exists(target):
        """cmdkey /list 原始字节是否含目标名。目标名为 ASCII，不受输出代码页影响。"""
        proc = subprocess.run(["cmdkey", "/list"], capture_output=True)
        return target.encode("ascii") in proc.stdout

    @staticmethod
    def _delete_credential(target):
        """幂等清理合成凭据（存在与否都不失败）。"""
        subprocess.run(["cmdkey", f"/delete:{target}"], capture_output=True)

    # ------------------------------------------------------------------
    # 注册表断言辅助（winreg 直连 HKCU；仅用于临时 ProgId）
    # ------------------------------------------------------------------
    def _read_reg_default(self, subkey):
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, subkey) as key:
            return winreg.QueryValueEx(key, None)[0]

    def _read_reg_default_or_none(self, subkey):
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, subkey) as key:
                return winreg.QueryValueEx(key, None)[0]
        except FileNotFoundError:
            return None

    def _read_reg_value(self, subkey, name):
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, subkey) as key:
            return winreg.QueryValueEx(key, name)[0]

    def _reg_key_exists(self, subkey):
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, subkey):
                return True
        except FileNotFoundError:
            return False

    def _openwith_value_exists(self, progid):
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Classes\.pdf\OpenWithProgids") as key:
                winreg.QueryValueEx(key, progid)
                return True
        except (FileNotFoundError, OSError):
            return False

    def _cleanup_association(self, progid):
        """测试后清理临时 ProgId 与打开方式候选（幂等，供 addCleanup 兜底）。"""
        import winreg
        try:
            with winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r"Software\Classes\.pdf\OpenWithProgids",
                0,
                winreg.KEY_SET_VALUE,
            ) as key:
                winreg.DeleteValue(key, progid)
        except (FileNotFoundError, OSError):
            pass
        self._delete_reg_tree(winreg.HKEY_CURRENT_USER, rf"Software\Classes\{progid}")

    @staticmethod
    def _delete_reg_tree(root, subkey):
        import winreg
        try:
            with winreg.OpenKey(root, subkey, 0, winreg.KEY_ALL_ACCESS) as key:
                while True:
                    try:
                        child = winreg.EnumKey(key, 0)
                    except OSError:
                        break
                    InstallerWindowsTests._delete_reg_tree(root, subkey + "\\" + child)
        except (FileNotFoundError, OSError):
            return
        try:
            winreg.DeleteKey(root, subkey)
        except OSError:
            pass


@unittest.skipUnless(sys.platform == "win32", "Windows installer (Install.ps1) only")
class InstallerSelfContainedWindowsTests(InstallerWindowsTests):
    """同一套安装语义在自包含安装包（内置 engine\python\python.exe）下的重复验证。

    基类全部用例（test_01..test_11）继承运行：布局/备份/回滚/快捷方式/卸载/
    防覆盖/防链接语义与模式无关，应同时成立；assert_layout 追加自包含 3 项并
    校验 install-info.json 的 mode=self-contained。test_09 在本类切换为
    "无系统 Python 时 -Check 通过"（缺 Python 拒装仅适用框架依赖模式）。
    """

    self_contained = True

    # ------------------------------------------------------------------
    # 自包含专属：系统无 Python/dotnet（清洗 PATH）时安装照常完成。
    # ------------------------------------------------------------------
    def test_12_installs_without_system_python(self):
        scrubbed = os.environ.copy()
        scrubbed["PATH"] = str(Path(os.environ["SystemRoot"]) / "System32")
        rc, out, err = self.install(env=scrubbed)
        self.assertEqual(rc, 0, f"自包含安装不应依赖系统 Python：{err}")
        self.assertIn("安装模式：自包含", out)
        self.assertIn("无需系统 Python", out)
        self.assert_layout(self.target)
        self.assertEqual(self.backups(), [])
        self.assert_no_staging()


if __name__ == "__main__":
    unittest.main(verbosity=2)
