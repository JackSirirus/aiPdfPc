# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""LibreOffice conversion coverage for engine.office_convert on Windows.

Two layers:
- Mock level: patch find_office() and run_process() so the soffice command
  array construction is pinned down (flags order, profile URI, --convert-to
  filter, --outdir, source, timeout) without any LibreOffice installation.
- Real integration: skipped unless engine.find_office() actually locates a
  LibreOffice executable; converts synthetic .docx, .xlsx and .pptx files
  (plain and Chinese-named paths) to PDF and asserts the output exists and
  is valid.

The xlsx/pptx fixtures are hand-crafted minimal OOXML zip packages, so no
python-docx/openpyxl/python-pptx dependencies are introduced.

No real soffice call happens at mock level; no user documents are used.
"""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parent.parent
sys.path.insert(0,str(ROOT/"backend"))
import engine
import pymupdf as fitz

CJK_NAME="中文文档"
CJK_TEXT="本地 Office 转换测试"
FAKE_SOFFICE=r"C:\Program Files\LibreOffice\program\soffice.exe"
# Probed once at import; real-integration tests skip with this reason.
SOFFICE=engine.find_office()


def make_docx(path,text):
    from docx import Document
    document=Document()
    document.add_paragraph(text)
    document.save(path)


def zip_ooxml(path,members):
    """Write a minimal Office Open XML package (hand-crafted, no deps)."""
    import zipfile
    with zipfile.ZipFile(path,"w",zipfile.ZIP_DEFLATED) as z:
        for name,data in members.items():
            z.writestr(name,data)


def make_xlsx(path,text):
    """Minimal xlsx: one sheet, one cell holding an inline string."""
    zip_ooxml(path,{
        "[Content_Types].xml":
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
            '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
            '<Default Extension="xml" ContentType="application/xml"/>'
            '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
            '<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
            '</Types>',
        "_rels/.rels":
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>'
            '</Relationships>',
        "xl/workbook.xml":
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"'
            ' xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
            '<sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>',
        "xl/_rels/workbook.xml.rels":
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>'
            '</Relationships>',
        "xl/worksheets/sheet1.xml":
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
            '<sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>'+text+'</t></is></c></row></sheetData>'
            '</worksheet>',
    })


def make_pptx(path,text):
    """Minimal pptx: one slide with one text box holding the marker text."""
    zip_ooxml(path,{
        "[Content_Types].xml":
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
            '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
            '<Default Extension="xml" ContentType="application/xml"/>'
            '<Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>'
            '<Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>'
            '</Types>',
        "_rels/.rels":
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>'
            '</Relationships>',
        "ppt/presentation.xml":
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<presentation xmlns="http://schemas.openxmlformats.org/presentationml/2006/main"'
            ' xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
            '<sldIdLst><sldId id="256" r:id="rId1"/></sldIdLst>'
            '<sldSz cx="12192000" cy="6858000"/></presentation>',
        "ppt/_rels/presentation.xml.rels":
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>'
            '</Relationships>',
        "ppt/slides/slide1.xml":
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<sld xmlns="http://schemas.openxmlformats.org/presentationml/2006/main"'
            ' xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">'
            '<cSld><spTree>'
            '<nvGrpSpPr><cNvPr id="1" name=""/><cNvGrpSpPr/><nvPr/></nvGrpSpPr><grpSpPr/>'
            '<sp><nvSpPr><cNvPr id="2" name="TextBox 1"/><cNvSpPr txBox="1"/><nvPr/></nvSpPr>'
            '<spPr><a:xfrm><a:off x="914400" y="914400"/><a:ext cx="9144000" cy="914400"/></a:xfrm>'
            '<a:prstGeom prst="rect"><a:avLst/></a:prstGeom></spPr>'
            '<txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang="zh-CN"/>'
            '<a:t>'+text+'</a:t></a:r></a:p></txBody></sp>'
            '</spTree></cSld></sld>',
    })


def make_pdf(path,part=None,text=CJK_TEXT):
    """Minimal 1-page PDF; optional XMP metadata claiming a PDF/A part."""
    doc=fitz.open()
    page=doc.new_page(width=595,height=842)
    fontname="china-s" if any(ord(c)>255 for c in text) else "helv"
    page.insert_text((50,60),text,fontname=fontname,fontsize=14)
    if part:
        doc.set_xml_metadata(
            '<x:xmpmeta xmlns:x="adobe:ns:meta/">'
            '<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
            '<rdf:Description xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/">'
            f'<pdfaid:part>{part}</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>'
            '</rdf:Description></rdf:RDF></x:xmpmeta>')
    doc.save(path);doc.close()


class OfficeWindowsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (ROOT/"tmp").mkdir(exist_ok=True)
        cls.temp=tempfile.TemporaryDirectory(prefix="aipdf-office-win-",dir=ROOT/"tmp")
        cls.root=Path(cls.temp.name)
        cls.docx=cls.root/"report.docx"
        make_docx(cls.docx,"AIPDF office fixture")
        cls.cjk_docx=cls.root/f"{CJK_NAME}.docx"
        make_docx(cls.cjk_docx,CJK_TEXT)
        cls.xlsx=cls.root/"report.xlsx"
        make_xlsx(cls.xlsx,CJK_TEXT)
        cls.pptx=cls.root/"report.pptx"
        make_pptx(cls.pptx,CJK_TEXT)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def fake_run_process(self,calls):
        """Recording run_process double: writes the PDF soffice would emit."""
        def run(args,timeout=180):
            calls.append((list(args),timeout))
            target=Path(args[-2]);source=Path(args[-1])
            fmt=str(args[args.index("--convert-to")+1])
            part=self.pdfa_part if "draw_pdf_Export" in fmt else None
            make_pdf(target/f"{source.stem}.pdf",part=part)
            return ""
        return run

    def convert_with_mock_office(self,source,dest,pdfa=None,pdfa_part=None):
        calls=[]
        self.pdfa_part=pdfa_part
        with patch.object(engine,"find_office",return_value=FAKE_SOFFICE),\
             patch.object(engine,"run_process",side_effect=self.fake_run_process(calls)):
            result=engine.office_convert(str(source),str(dest),pdfa)
        return calls,result

    def test_mock_command_array_construction(self):
        """① Mocked soffice: engine builds the expected argument array."""
        dest=self.root/"out"/"report.pdf"
        dest.parent.mkdir()
        calls,result=self.convert_with_mock_office(self.docx,dest)
        args,timeout=calls[0]
        self.assertEqual(timeout,300)
        self.assertEqual(args[0],FAKE_SOFFICE)
        self.assertTrue(args[1].startswith("-env:UserInstallation=file:///"))
        self.assertEqual(args[2:6],["--headless","--nologo","--nodefault","--norestore"])
        self.assertEqual(args[6],"--convert-to")
        self.assertEqual(args[7],"pdf")
        self.assertEqual(args[8],"--outdir")
        self.assertTrue(Path(args[9]).is_absolute())
        self.assertEqual(Path(args[10]),self.docx)
        self.assertEqual(result,[str(dest)])
        self.assertTrue(dest.is_file())
        with fitz.open(dest) as doc:self.assertEqual(len(doc),1)

    def test_mock_pdfa_filter_values(self):
        """① PDF/A export: --convert-to filter string per selected level."""
        for level,version in [("1b","1"),("2b","2"),("3b","3")]:
            with self.subTest(level=level):
                self.pdfa_part=level[0]
                dest=self.root/f"pdfa-{level}.pdf"
                calls,_=self.convert_with_mock_office(self.docx,dest,pdfa=level,pdfa_part=level[0])
                args,_=calls[0]
                fmt=str(args[args.index("--convert-to")+1])
                family,fil,filter_json=fmt.split(":",2)
                self.assertEqual(family,"pdf")
                self.assertEqual(fil,"draw_pdf_Export")
                spec=json.loads(filter_json)
                self.assertEqual(spec["SelectPdfVersion"]["type"],"long")
                self.assertEqual(spec["SelectPdfVersion"]["value"],version)

    def test_mock_chinese_source_stem_becomes_output_name(self):
        """③ Chinese path: <stem>.pdf mapping survives CJK filenames."""
        dest=self.root/"输出"/f"{CJK_NAME}.pdf"
        dest.parent.mkdir()
        calls,result=self.convert_with_mock_office(self.cjk_docx,dest)
        args,_=calls[0]
        self.assertEqual(Path(args[10]),self.cjk_docx)
        self.assertEqual(result,[str(dest)])
        self.assertTrue(dest.is_file())
        with fitz.open(dest) as doc:self.assertIn(CJK_TEXT,doc[0].get_text())

    def test_missing_office_raises_user_error(self):
        """No LibreOffice found: a clear UserError, not a crash."""
        with patch.object(engine,"find_office",return_value=None):
            with self.assertRaises(engine.UserError) as ctx:
                engine.office_convert(str(self.docx),str(self.root/"never.pdf"))
        self.assertIn("LibreOffice",str(ctx.exception))

    def convert_for_real(self,source,dest,text=CJK_TEXT):
        result=engine.office_convert(str(source),str(dest))
        self.assertTrue(dest.is_file(),"转换后输出文件不存在")
        with fitz.open(dest) as doc:
            self.assertGreaterEqual(len(doc),1)
            self.assertIn(text,doc[0].get_text())
        return result

    @unittest.skipUnless(SOFFICE,"LibreOffice not installed (find_office() returned None): real docx→PDF integration skipped")
    def test_real_docx_to_pdf(self):
        """② Synthetic docx → PDF exists with ≥1 page and the marker text."""
        self.convert_for_real(self.docx,self.root/"real-report.pdf",text="AIPDF office fixture")

    @unittest.skipUnless(SOFFICE,"LibreOffice not installed (find_office() returned None): Chinese-path docx→PDF integration skipped")
    def test_real_chinese_path_docx_to_pdf(self):
        """③ Chinese-named source and output directory survive real soffice."""
        dest=self.root/"输出目录"/f"{CJK_NAME}.pdf"
        dest.parent.mkdir()
        self.convert_for_real(self.cjk_docx,dest)

    @unittest.skipUnless(SOFFICE,"LibreOffice not installed (find_office() returned None): real xlsx→PDF integration skipped")
    def test_real_xlsx_to_pdf(self):
        """② Hand-crafted xlsx → PDF exists with the marker text."""
        self.convert_for_real(self.xlsx,self.root/"real-report-xlsx.pdf")

    @unittest.skipUnless(SOFFICE,"LibreOffice not installed (find_office() returned None): real pptx→PDF integration skipped")
    def test_real_pptx_to_pdf(self):
        """② Hand-crafted pptx → PDF exists with the marker text."""
        self.convert_for_real(self.pptx,self.root/"real-report-pptx.pdf")


if __name__=="__main__":
    unittest.main(verbosity=2)
