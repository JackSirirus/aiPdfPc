# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""UTF-8 stdio hardening: CJK filenames and a cp936 console stay mojibake-free.

The engine speaks one JSON request on stdin and one JSON result on stdout, both
UTF-8 on every platform. On Windows the legacy ANSI code page (cp936 on zh-CN)
would otherwise corrupt the protocol; these tests pin that contract down and
run on any platform (the cp936 codec itself is not Windows-specific).
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT=Path(__file__).resolve().parent.parent
sys.path.insert(0,str(ROOT/"backend"))
import engine
import pymupdf as fitz

CJK_NAME="中文文档"
CJK_TEXT="本地 PDF 工具测试"


def make_pdf(path,text):
    doc=fitz.open()
    page=doc.new_page(width=595,height=842)
    page.insert_text((50,60),text,fontname="china-s",fontsize=18)
    doc.save(path);doc.close()


class Utf8ProtocolTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (ROOT/"tmp").mkdir(exist_ok=True)
        cls.temp=tempfile.TemporaryDirectory(prefix="aipdf-utf8-",dir=ROOT/"tmp")
        cls.root=Path(cls.temp.name)
        cls.source=cls.root/f"{CJK_NAME}.pdf"
        make_pdf(cls.source,CJK_TEXT)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def request(self):
        return {"operation":"rotate","files":[str(self.source)],"options":{"angle":"90"},"outputDir":str(self.root)}

    def run_engine_subprocess(self,env=None):
        # Bytes in, bytes out: the test decodes explicitly as UTF-8 so any
        # code-page leakage surfaces as a strict-decode failure, not mojibake.
        return subprocess.run([sys.executable,str(ROOT/"backend"/"engine.py")],
                              input=json.dumps(self.request()).encode("utf-8"),
                              capture_output=True,env=env,timeout=120)

    def test_cjk_filename_dispatch(self):
        """① Chinese filename through the engine keeps CJK intact, no U+FFFD."""
        result=engine.dispatch(self.request())
        self.assertTrue(result["ok"])
        self.assertIn(CJK_NAME,Path(result["outputs"][0]).name)
        self.assertNotIn("\ufffd",json.dumps(result,ensure_ascii=False))
        with fitz.open(result["outputs"][0]) as doc:
            self.assertIn(CJK_TEXT,doc[0].get_text())

    def test_cjk_protocol_subprocess_utf8(self):
        """① Full subprocess round trip: stdout JSON is valid UTF-8 with CJK, no U+FFFD."""
        proc=self.run_engine_subprocess()
        self.assertEqual(proc.returncode,0,proc.stderr.decode("utf-8",errors="replace"))
        payload=proc.stdout.decode("utf-8")  # strict: raises on any code-page leakage
        result=json.loads(payload)
        self.assertTrue(result["ok"])
        self.assertIn(CJK_NAME,payload)
        self.assertNotIn("\ufffd",payload)
        # Progress diagnostics on stderr stay UTF-8 too.
        first_line=proc.stderr.splitlines()[0].decode("utf-8")
        self.assertIn("message",json.loads(first_line))

    def test_cp936_console_env_still_utf8(self):
        """② PYTHONIOENCODING=cp936 must not leak into the protocol: reconfigure wins."""
        env={**os.environ,"PYTHONIOENCODING":"cp936"}
        proc=self.run_engine_subprocess(env=env)
        self.assertEqual(proc.returncode,0,proc.stderr.decode("utf-8",errors="replace"))
        payload=proc.stdout.decode("utf-8")  # strict
        result=json.loads(payload)
        self.assertTrue(result["ok"])
        self.assertIn(CJK_NAME,payload)
        self.assertNotIn("\ufffd",payload)


if __name__=="__main__":
    unittest.main(verbosity=2)
