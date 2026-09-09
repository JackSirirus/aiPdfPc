# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""Windows path resolution tests; filesystem probes are mocked, no real installs needed."""
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parent.parent
sys.path.insert(0,str(ROOT/"backend"))
import engine

SOFFICE_PF=r"C:\Program Files\LibreOffice\program\soffice.exe"
SOFFICE_PF_X86=r"C:\Program Files (x86)\LibreOffice\program\soffice.exe"


class WindowsPathTests(unittest.TestCase):
    def setUp(self):
        (ROOT/"tmp").mkdir(exist_ok=True)

    def probe(self,*existing):
        """Patch Path.is_file with a recording function; returns (patcher, queried paths)."""
        hits={str(Path(p)) for p in existing}
        queries=[]
        def fake_is_file(path):
            queries.append(str(path))
            return str(path) in hits
        return patch.object(Path,"is_file",fake_is_file),queries

    def registry(self,values):
        """Patch winreg so (hive, subkey) reads come from `values`; absent keys raise OSError.
        Returns (patcher, read keys) — mirrors how a real absent key behaves."""
        import winreg
        reads=[]
        class FakeKey:
            def __init__(self,ident): self.ident=ident
            def __enter__(self): return self
            def __exit__(self,*exc): return False
        def fake_open(view,subkey):
            if (view,subkey) not in values:
                raise OSError(2,"not found")
            reads.append((view,subkey))
            return FakeKey((view,subkey))
        def fake_query(key,name):
            return values[key.ident],1
        return patch.multiple(winreg,OpenKey=fake_open,QueryValueEx=fake_query),reads

    def test_find_office_priority_order(self):
        env_office=str(ROOT/"tmp"/"fake-env-soffice.exe")
        which_office=r"D:\fake\path\soffice.exe"
        for expected in (env_office,SOFFICE_PF,SOFFICE_PF_X86,which_office):
            with self.subTest(hit=expected):
                probe,_=self.probe(expected)
                with probe,patch.dict(os.environ,{"AIPDF_OFFICE":env_office}),\
                     patch.object(engine,"_windows_office_registry_candidates",return_value=[]),\
                     patch.object(shutil,"which",return_value=which_office),patch.object(sys,"platform","win32"):
                    self.assertEqual(engine.find_office(),expected)
        probe,queries=self.probe()
        with probe,patch.dict(os.environ,{"AIPDF_OFFICE":env_office}),\
             patch.object(engine,"_windows_office_registry_candidates",return_value=[]),\
             patch.object(shutil,"which",return_value=None),patch.object(sys,"platform","win32"):
            self.assertIsNone(engine.find_office())
        self.assertEqual(queries,[env_office,SOFFICE_PF,SOFFICE_PF_X86])

    def test_find_office_without_libreoffice_returns_none(self):
        probe,_=self.probe()
        with probe,patch.dict(os.environ,{"AIPDF_OFFICE":""}),\
             patch.object(shutil,"which",return_value=None),patch.object(sys,"platform","win32"):
            self.assertIsNone(engine.find_office())
        result=engine.find_office()
        self.assertTrue(result is None or Path(result).is_file())

    def test_find_office_registry_candidate_discovered(self):
        """注册表键指向的目录存在 soffice.exe 时被采用，且优先于硬编码 C: 路径。"""
        import winreg
        reg_dir=r"D:\Custom LibreOffice\program"
        reg_soffice=str(Path(reg_dir)/"soffice.exe")
        values={(winreg.HKEY_LOCAL_MACHINE,r"SOFTWARE\LibreOffice\UNO\InstallPath"):reg_dir}
        reg_patch,reads=self.registry(values)
        probe,queries=self.probe(reg_soffice,SOFFICE_PF)
        with probe,reg_patch,patch.dict(os.environ,{"AIPDF_OFFICE":""}),\
             patch.object(shutil,"which",return_value=None),patch.object(sys,"platform","win32"):
            self.assertEqual(engine.find_office(),reg_soffice)
        self.assertEqual(queries,[reg_soffice])  # C: 路径未被查询：注册表命中即返回
        self.assertEqual(reads,[(winreg.HKEY_LOCAL_MACHINE,r"SOFTWARE\LibreOffice\UNO\InstallPath")])

    def test_find_office_registry_dir_without_soffice_skipped(self):
        """注册表目录缺少 soffice.exe 时跳过，继续探测硬编码路径与 PATH。"""
        import winreg
        reg_dir=r"D:\Empty\program"
        reg_soffice=str(Path(reg_dir)/"soffice.exe")
        values={(winreg.HKEY_LOCAL_MACHINE,r"SOFTWARE\LibreOffice\UNO\InstallPath"):reg_dir}
        reg_patch,_=self.registry(values)
        which_office=r"D:\fake\path\soffice.exe"
        probe,queries=self.probe(SOFFICE_PF,which_office)
        with probe,reg_patch,patch.dict(os.environ,{"AIPDF_OFFICE":""}),\
             patch.object(shutil,"which",return_value=which_office),patch.object(sys,"platform","win32"):
            self.assertEqual(engine.find_office(),SOFFICE_PF)
        self.assertEqual(queries,[reg_soffice,SOFFICE_PF])

    def test_find_office_registry_absent_no_crash(self):
        """注册表键全部缺失时按原逻辑返回 None，不抛异常。"""
        import winreg
        reg_patch,reads=self.registry({})
        probe,queries=self.probe()
        with probe,reg_patch,patch.dict(os.environ,{"AIPDF_OFFICE":""}),\
             patch.object(shutil,"which",return_value=None),patch.object(sys,"platform","win32"):
            self.assertIsNone(engine.find_office())
        self.assertEqual(reads,[])  # 四个键全部缺失，无成功读取
        self.assertEqual(queries,[SOFFICE_PF,SOFFICE_PF_X86])

    def test_find_office_registry_hkcu_only(self):
        """仅 HKCU 存在键值时同样能发现安装目录。"""
        import winreg
        reg_dir=r"C:\Users\someone\AppData\CustomLO\program"
        reg_soffice=str(Path(reg_dir)/"soffice.exe")
        values={(winreg.HKEY_CURRENT_USER,r"SOFTWARE\LibreOffice\UNO\InstallPath"):reg_dir}
        reg_patch,reads=self.registry(values)
        probe,_=self.probe(reg_soffice)
        with probe,reg_patch,patch.dict(os.environ,{"AIPDF_OFFICE":""}),\
             patch.object(shutil,"which",return_value=None),patch.object(sys,"platform","win32"):
            self.assertEqual(engine.find_office(),reg_soffice)
        self.assertIn((winreg.HKEY_CURRENT_USER,r"SOFTWARE\LibreOffice\UNO\InstallPath"),reads)

    def test_helper_paths_windows(self):
        with patch.object(sys,"platform","win32"):
            vp = engine.vision_path()
            # vision_path returns a valid path if helper was built, else None
            self.assertTrue(vp is None or Path(vp).is_file())
            self.assertIsNone(engine.web_path())

    def test_mac_office_branch_preserved(self):
        mac_soffice="/Applications/LibreOffice.app/Contents/MacOS/soffice"
        probe,_=self.probe(mac_soffice)
        with probe,patch.dict(os.environ,{"AIPDF_OFFICE":""}),\
             patch.object(shutil,"which",return_value=None),patch.object(sys,"platform","darwin"):
            self.assertEqual(engine.find_office(),mac_soffice)

    def test_vision_path_install_layout(self):
        """vision_path() 在安装布局下能找到 <install>/helper/WindowsOcrHelper.exe"""
        try:
            with tempfile.TemporaryDirectory(prefix="aipdf-win-install-", dir=ROOT/"tmp") as tmp:
                install_root = Path(tmp)
                helper_dir = install_root / "helper"
                helper_dir.mkdir(parents=True)
                helper_exe = helper_dir / "WindowsOcrHelper.exe"
                helper_exe.write_bytes(b"\x00")  # 伪造可执行文件

                # engine.py 位于 <install>/engine/backend/engine.py
                fake_engine_file = str(install_root / "engine" / "backend" / "engine.py")
                with patch.object(engine, "__file__", fake_engine_file), \
                     patch.object(sys, "platform", "win32"):
                    # 开发布局探测会失败（没有 Sources/WindowsOcrHelper），应降级到安装布局
                    result = engine.vision_path()
                    self.assertEqual(result, str(helper_exe))
        finally:
            pass  # TemporaryDirectory 已自动清理

    def test_heic_image_rejected_with_clear_message(self):
        for suffix in (".heic",".HEIF"):
            with self.subTest(suffix=suffix):
                with tempfile.TemporaryDirectory(prefix="aipdf-win-tests-",dir=ROOT/"tmp") as tmp:
                    heic=Path(tmp)/f"photo{suffix}"
                    heic.write_bytes(b"\x00\x00\x00\x20ftypheic NOT-A-REAL-IMAGE")
                    with patch.object(sys,"platform","win32"):
                        with self.assertRaises(engine.UserError) as ctx:
                            engine.load_image(heic)
                message=str(ctx.exception)
                self.assertIn("HEIC",message)
                self.assertIn("Windows",message)


if __name__=="__main__":
    unittest.main(verbosity=2)
