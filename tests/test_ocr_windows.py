# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""Windows.Media.Ocr helper coverage: engine.vision_path() resolves the built
C# helper, and the helper OCRs a synthetic large-text PNG back to "TEST".

Two layers:
- Path resolution: engine.vision_path() on win32 points at the built
  WindowsOcrHelper.exe (Release preferred, Debug fallback), never None when
  the helper is built.
- Real OCR integration: skipped unless the helper executable actually exists
  and runs; generates a PNG with large "AIPDF OCR TEST" text via PIL, runs the
  helper `ocr` mode, and asserts the merged text contains "TEST".

No user documents are used; the PNG is synthetic.
"""
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))
import engine

# Probed once at import; real-integration tests skip with this reason.
HELPER = engine.vision_path()


def make_text_png(path, text="AIPDF OCR TEST", size=72):
    from PIL import Image, ImageDraw, ImageFont
    font = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", size)
    im = Image.new("RGB", (900, 220), "white")
    ImageDraw.Draw(im).text((30, 50), text, fill="black", font=font)
    im.save(path)


class OcrWindowsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (ROOT / "tmp").mkdir(exist_ok=True)
        cls.temp = tempfile.TemporaryDirectory(prefix="aipdf-ocr-win-", dir=ROOT / "tmp")
        cls.root = Path(cls.temp.name)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def test_vision_path_resolves_built_helper(self):
        """win32 vision_path() returns the built helper exe, not None."""
        if sys.platform != "win32":
            self.skipTest("Windows-only helper path resolution")
        self.assertIsNotNone(HELPER, "WindowsOcrHelper.exe not built; run dotnet build Sources\\WindowsOcrHelper")
        self.assertTrue(Path(HELPER).is_file())
        self.assertTrue(HELPER.lower().endswith("windowsocrhelper.exe"))

    @unittest.skipUnless(HELPER, "WindowsOcrHelper.exe not built (vision_path() returned None)")
    def test_list_languages_json(self):
        """--list-languages emits a JSON array of {tag,name} objects."""
        out = subprocess.run([HELPER, "--list-languages"], capture_output=True, text=True, timeout=60)
        self.assertEqual(out.returncode, 0, out.stderr)
        langs = json.loads(out.stdout)
        self.assertIsInstance(langs, list)
        self.assertTrue(langs, "no OCR languages reported")
        for lang in langs:
            self.assertIn("tag", lang)
            self.assertIn("name", lang)

    @unittest.skipUnless(HELPER, "WindowsOcrHelper.exe not built (vision_path() returned None)")
    def test_selftest_json(self):
        """--selftest OCRs the embedded image and reports ok:true with text."""
        out = subprocess.run([HELPER, "--selftest"], capture_output=True, text=True, timeout=60)
        self.assertEqual(out.returncode, 0, out.stderr)
        result = json.loads(out.stdout)
        self.assertTrue(result.get("ok"))
        self.assertIn("TEST", result.get("text", ""))

    @unittest.skipUnless(HELPER, "WindowsOcrHelper.exe not built (vision_path() returned None)")
    def test_ocr_synthetic_png_contains_test(self):
        """PIL large-text PNG → helper `ocr` → merged text contains TEST."""
        png = self.root / "ocr.png"
        make_text_png(png)
        out = subprocess.run([HELPER, "ocr", str(png)], capture_output=True, text=True, timeout=60)
        self.assertEqual(out.returncode, 0, out.stderr)
        boxes = json.loads(out.stdout)
        self.assertIsInstance(boxes, list)
        merged = " ".join(b["text"] for b in boxes)
        self.assertIn("TEST", merged)

    @unittest.skipUnless(HELPER, "WindowsOcrHelper.exe not built (vision_path() returned None)")
    def test_ocr_boxes_normalized_coordinates(self):
        """Each box carries normalized x/y/w/h in [0,1] with non-empty text."""
        png = self.root / "ocr2.png"
        make_text_png(png)
        out = subprocess.run([HELPER, "ocr", str(png)], capture_output=True, text=True, timeout=60)
        self.assertEqual(out.returncode, 0, out.stderr)
        boxes = json.loads(out.stdout)
        self.assertTrue(boxes)
        for b in boxes:
            self.assertTrue(b["text"].strip())
            for key in ("x", "y", "w", "h"):
                self.assertGreaterEqual(b[key], 0.0)
                self.assertLessEqual(b[key], 1.0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
