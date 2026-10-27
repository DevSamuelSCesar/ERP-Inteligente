"""Convert README.md (including Mermaid diagrams) to PDF with headless Chrome/Chromium.

Usage, from the repository root:
    python3 tools/readme_to_pdf.py                      # README.md -> README.pdf
    python3 tools/readme_to_pdf.py -i README.md -o /tmp/entrega.pdf
    CHROME_BIN=/path/to/chrome python3 tools/readme_to_pdf.py

The Markdown is rendered in the browser by marked and the diagrams by Mermaid, both loaded from
jsDelivr at pinned versions with Subresource Integrity hashes. Relative links point to the GitHub
repository so they keep working inside the PDF. Generated PDFs are ignored by git.
"""

import argparse
import glob
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile

REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
REPO_URL = "https://github.com/NatanCorreiaS/erp-inteligente/blob/main/"

MARKED_URL = "https://cdn.jsdelivr.net/npm/marked@12.0.2/marked.min.js"
MARKED_SRI = "sha384-/TQbtLCAerC3jgaim+N78RZSDYV7ryeoBCVqTuzRrFec2akfBkHS7ACQ3PQhvMVi"
MERMAID_URL = "https://cdn.jsdelivr.net/npm/mermaid@11.4.1/dist/mermaid.min.js"
MERMAID_SRI = "sha384-rbtjAdnIQE/aQJGEgXrVUlMibdfTSa4PQju4HDhN3sR2PmaKFzhEafuePsl9H/9I"

CHROME_NAMES = (
    "chromium", "chromium-browser", "google-chrome", "google-chrome-stable",
    "chrome", "chrome-headless-shell", "microsoft-edge",
)
PLAYWRIGHT_GLOBS = (
    "~/.cache/ms-playwright/chromium-*/chrome-linux*/chrome",
    "~/.cache/ms-playwright/chromium_headless_shell-*/chrome-headless-shell-linux*/chrome-headless-shell",
)

RENDER_BUDGET_MS = 30000

PAGE_CSS = """
@page { size: A4; margin: 16mm 14mm; }
body { font-family: "Segoe UI", "Noto Sans", "DejaVu Sans", Arial, sans-serif;
       font-size: 10.5pt; line-height: 1.45; color: #1f2328; max-width: 100%; }
h1 { font-size: 20pt; border-bottom: 2px solid #d0d7de; padding-bottom: 4px; }
h2 { font-size: 15pt; border-bottom: 1px solid #d0d7de; padding-bottom: 3px; margin-top: 22px; }
h3 { font-size: 12.5pt; margin-top: 16px; }
h2, h3 { break-after: avoid; }
table { border-collapse: collapse; width: 100%; margin: 8px 0; font-size: 9.5pt; }
th, td { border: 1px solid #d0d7de; padding: 4px 6px; vertical-align: top; text-align: left; }
th { background: #f6f8fa; }
tr, img, svg { break-inside: avoid; }
code { font-family: "DejaVu Sans Mono", Consolas, monospace; font-size: 9pt;
       background: #f6f8fa; padding: 1px 3px; border-radius: 3px; }
pre { background: #f6f8fa; padding: 8px; border-radius: 4px; white-space: pre-wrap; }
pre code { padding: 0; }
a { color: #0969da; text-decoration: none; }
.mermaid { background: none; text-align: center; margin: 10px 0; }
.mermaid svg { max-width: 100% !important; max-height: 205mm; height: auto; }
.figure { break-inside: avoid; }
@page wide { size: A4 landscape; margin: 10mm; }
.figure.wide { page: wide; break-before: page; break-after: page; }
.figure.wide .mermaid svg { max-height: 150mm; width: 100%; }
"""

# Diagrams wider than this (width / height) get their own landscape page.
WIDE_ASPECT_RATIO = 1.3

RENDER_SCRIPT = """
(async () => {
  const source = JSON.parse(document.getElementById("readme-source").textContent);
  const content = document.getElementById("content");
  content.innerHTML = marked.parse(source, { gfm: true });
  for (const code of content.querySelectorAll("pre > code.language-mermaid")) {
    const diagram = document.createElement("div");
    diagram.className = "mermaid";
    diagram.textContent = code.textContent;
    code.parentElement.replaceWith(diagram);
  }
  mermaid.initialize({ startOnLoad: false, securityLevel: "strict", theme: "default",
                       flowchart: { useMaxWidth: true }, sequence: { useMaxWidth: true } });
  try {
    await mermaid.run({ querySelector: ".mermaid" });
    // Keep each diagram on the same page as the headings and paragraphs that introduce it.
    for (const diagram of content.querySelectorAll(".mermaid")) {
      const figure = document.createElement("section");
      figure.className = "figure";
      const lead = [];
      let node = diagram.previousElementSibling;
      while (node && /^(H[1-6]|P)$/.test(node.tagName) && !node.classList.contains("mermaid")) {
        lead.unshift(node);
        if (/^H[1-6]$/.test(node.tagName) && !/^H[1-6]$/.test(node.previousElementSibling?.tagName || "")) break;
        node = node.previousElementSibling;
      }
      diagram.replaceWith(figure);
      figure.append(...lead, diagram);
      const box = diagram.querySelector("svg")?.viewBox.baseVal;
      if (box && box.height > 0 && box.width / box.height > WIDE_ASPECT_RATIO) {
        figure.classList.add("wide");
      }
    }
  } catch (error) {
    document.body.insertAdjacentHTML("afterbegin",
      "<p style='color:#b00'>Mermaid render error: " + String(error).replace(/</g, "&lt;") + "</p>");
  }
  document.title = "rendered";
})();
"""


def build_html(markdown_text, title="README"):
    """Return a self-rendering HTML page for the given Markdown text."""
    # JSON inside <script> must not contain "</" or it could close the tag early.
    payload = json.dumps(markdown_text).replace("</", "<\\/")
    return f"""<!doctype html>
<html lang="pt-BR">
<head>
<meta charset="utf-8">
<base href="{REPO_URL}">
<title>{title}</title>
<style>{PAGE_CSS}</style>
<script src="{MARKED_URL}" integrity="{MARKED_SRI}" crossorigin="anonymous"></script>
<script src="{MERMAID_URL}" integrity="{MERMAID_SRI}" crossorigin="anonymous"></script>
</head>
<body>
<main id="content"></main>
<script type="application/json" id="readme-source">{payload}</script>
<script>const WIDE_ASPECT_RATIO = {WIDE_ASPECT_RATIO};{RENDER_SCRIPT}</script>
</body>
</html>
"""


def find_chrome(environ=None):
    """Locate a Chrome/Chromium binary: $CHROME_BIN, then PATH, then the Playwright cache."""
    environ = os.environ if environ is None else environ
    explicit = environ.get("CHROME_BIN")
    if explicit:
        if pathlib.Path(explicit).is_file():
            return explicit
        raise FileNotFoundError(f"CHROME_BIN does not point to a file: {explicit}")
    for name in CHROME_NAMES:
        path = shutil.which(name)
        if path:
            return path
    for pattern in PLAYWRIGHT_GLOBS:
        matches = sorted(glob.glob(os.path.expanduser(pattern)))
        if matches:
            return matches[-1]
    raise FileNotFoundError("Chrome/Chromium not found; install one or set CHROME_BIN")


def chrome_command(chrome, html_path, pdf_path):
    return [
        chrome,
        "--headless",
        "--disable-gpu",
        "--no-sandbox" if os.geteuid() == 0 else "--no-first-run",
        "--no-pdf-header-footer",
        f"--virtual-time-budget={RENDER_BUDGET_MS}",
        f"--print-to-pdf={pdf_path}",
        pathlib.Path(html_path).resolve().as_uri(),
    ]


def convert(input_path, output_path, chrome=None):
    input_path = pathlib.Path(input_path)
    output_path = pathlib.Path(output_path).resolve()
    chrome = chrome or find_chrome()
    html = build_html(input_path.read_text(encoding="utf-8"), title=input_path.stem)
    with tempfile.TemporaryDirectory() as tmp:
        html_path = pathlib.Path(tmp) / "readme.html"
        html_path.write_text(html, encoding="utf-8")
        output_path.unlink(missing_ok=True)
        result = subprocess.run(chrome_command(chrome, html_path, output_path),
                                capture_output=True, text=True, timeout=180, check=False)
    if result.returncode != 0 or not output_path.is_file():
        raise RuntimeError(f"Chrome failed ({result.returncode}): {result.stderr.strip()[-2000:]}")
    return output_path


def main(argv=None):
    parser = argparse.ArgumentParser(description="Convert README.md (with Mermaid) to PDF.")
    parser.add_argument("-i", "--input", default=REPO_ROOT / "README.md", type=pathlib.Path)
    parser.add_argument("-o", "--output", default=REPO_ROOT / "README.pdf", type=pathlib.Path)
    parser.add_argument("--chrome", help="Chrome/Chromium binary (default: auto-detect)")
    args = parser.parse_args(argv)
    try:
        pdf = convert(args.input, args.output, args.chrome)
    except (FileNotFoundError, RuntimeError, subprocess.TimeoutExpired) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    print(pdf)
    return 0


if __name__ == "__main__":
    sys.exit(main())
