"""Tests for tools/readme_to_pdf.py.

Run from the repository root:  python3 -m unittest discover -s tools/tests -v
The end-to-end test is skipped when no Chrome/Chromium is available.
"""

import json
import pathlib
import re
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))

import readme_to_pdf  # noqa: E402


class BuildHtmlTests(unittest.TestCase):
    def test_markdown_is_embedded_as_json(self):
        html = readme_to_pdf.build_html("# Título\n\n```mermaid\nflowchart TD\n  A --> B\n```\n")
        payload = re.search(r'id="readme-source">(.*?)</script>', html, re.S).group(1)
        self.assertEqual(json.loads(payload.replace("<\\/", "</")), "# Título\n\n```mermaid\nflowchart TD\n  A --> B\n```\n")

    def test_script_tag_in_markdown_cannot_break_out(self):
        html = readme_to_pdf.build_html("text </script><script>alert(1)</script>")
        self.assertNotIn("</script><script>alert(1)", html)
        self.assertEqual(html.count("</script>"), 4)

    def test_cdn_scripts_are_pinned_with_integrity(self):
        html = readme_to_pdf.build_html("x")
        for url, sri in ((readme_to_pdf.MARKED_URL, readme_to_pdf.MARKED_SRI),
                         (readme_to_pdf.MERMAID_URL, readme_to_pdf.MERMAID_SRI)):
            self.assertRegex(url, r"@\d+\.\d+\.\d+/")
            self.assertIn(f'src="{url}" integrity="{sri}"', html)

    def test_relative_links_resolve_to_repository(self):
        self.assertIn(f'<base href="{readme_to_pdf.REPO_URL}">', readme_to_pdf.build_html("x"))


class FindChromeTests(unittest.TestCase):
    def test_explicit_chrome_bin_is_used(self):
        with tempfile.NamedTemporaryFile() as fake:
            self.assertEqual(readme_to_pdf.find_chrome({"CHROME_BIN": fake.name}), fake.name)

    def test_invalid_chrome_bin_raises(self):
        with self.assertRaises(FileNotFoundError):
            readme_to_pdf.find_chrome({"CHROME_BIN": "/nonexistent/chrome"})


class ConvertTests(unittest.TestCase):
    def setUp(self):
        try:
            self.chrome = readme_to_pdf.find_chrome()
        except FileNotFoundError:
            self.skipTest("no Chrome/Chromium available")

    def test_readme_becomes_pdf(self):
        with tempfile.TemporaryDirectory() as tmp:
            pdf = readme_to_pdf.convert(readme_to_pdf.REPO_ROOT / "README.md", pathlib.Path(tmp) / "out.pdf", self.chrome)
            data = pdf.read_bytes()
            self.assertTrue(data.startswith(b"%PDF-"))
            self.assertGreater(len(data), 50_000)


if __name__ == "__main__":
    unittest.main()
