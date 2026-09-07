# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""Windows URL-to-PDF acceptance tests against Playwright; no external network is used."""
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT=Path(__file__).resolve().parent.parent
sys.path.insert(0,str(ROOT/"backend"))
import engine
import pymupdf as fitz


@unittest.skipUnless(sys.platform=="win32","Playwright branch is win32-gated; macOS renders URLs via WebHelper")
class Url2PdfWindowsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (ROOT/"tmp").mkdir(exist_ok=True)
        cls.temp=tempfile.TemporaryDirectory(prefix="aipdf-url2pdf-",dir=ROOT/"tmp")
        cls.root=Path(cls.temp.name)
        cls.page=cls.root/"中文页面.html"
        cls.page.write_text("<html><head><meta charset=\"utf-8\"></head>"
                            "<body><h1>AIPDF URL Test</h1><p>中文网页渲染测试</p></body></html>",encoding="utf-8")

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def test_local_file_page_to_pdf(self):
        # file:// avoids any network; Playwright renders the local page directly.
        dest=self.root/"file-render.pdf"
        outputs=engine.url_to_pdf(self.page.as_uri(),dest)
        self.assertEqual(len(outputs),1)
        path=Path(outputs[0])
        self.assertTrue(path.is_file())
        self.assertGreater(path.stat().st_size,0)
        with fitz.open(path) as doc:
            self.assertGreaterEqual(len(doc),1)
            text=doc[0].get_text()
        self.assertIn("AIPDF URL Test",text)
        self.assertIn("中文网页渲染测试",text)

    def test_dispatch_renders_local_web_server(self):
        # Same loopback flow as the upstream test_web_url, through the win32 gate.
        import http.server
        import threading
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200);self.send_header("Content-Type","text/html; charset=utf-8");self.end_headers()
                self.wfile.write("<html><head><meta charset=\"utf-8\"></head><body><h1>Local Web Test</h1><p>浏览器渲染中文</p></body></html>".encode("utf-8"))
            def log_message(self,*args):pass
        server=http.server.HTTPServer(("127.0.0.1",0),Handler)
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        try:
            result=engine.dispatch({"operation":"html_to_pdf","files":[],"options":{"url":f"http://127.0.0.1:{server.server_port}/"},"outputDir":str(self.root)})
            with fitz.open(result["outputs"][0]) as doc:
                self.assertGreaterEqual(len(doc),1)
                text=doc[0].get_text()
            self.assertIn("Local Web Test",text)
            self.assertIn("浏览器渲染中文",text)
        finally:
            server.shutdown();server.server_close();thread.join()

    def test_invalid_url_reports_error_json(self):
        # Occupy a port, close the server, then target it: connection is refused deterministically.
        import socket
        probe=socket.socket();probe.bind(("127.0.0.1",0));port=probe.getsockname()[1];probe.close()
        request=json.dumps({"operation":"html_to_pdf","files":[],"options":{"url":f"http://127.0.0.1:{port}/"},
                            "outputDir":str(self.root)},ensure_ascii=False).encode("utf-8")
        result=subprocess.run([sys.executable,str(ROOT/"backend/engine.py")],input=request,capture_output=True,timeout=180)
        self.assertNotEqual(result.returncode,0)
        payload=json.loads(result.stdout.decode("utf-8"))
        self.assertFalse(payload["ok"])
        self.assertIn("error",payload)
        self.assertTrue(payload["error"])


if __name__=="__main__":
    unittest.main(verbosity=2)
