"""Read-only benchmark preparation; only the explicitly authorized five pages render.

No network, no execution of PDF content, no learner database access.
"""
import argparse
import hashlib
import json
import subprocess
import time
from pathlib import Path
from pypdf import PdfReader

parser = argparse.ArgumentParser()
parser.add_argument("source", type=Path)
parser.add_argument("output", type=Path)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
start = time.monotonic()

def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

before = digest(args.source)
reader = PdfReader(args.source)
pages = []
for number, page in enumerate(reader.pages, 1):
    try:
        text = page.extract_text() or ""
        resources = page.get("/Resources", {})
        resources = resources.get_object() if hasattr(resources, "get_object") else resources
        objects = resources.get("/XObject", {})
        objects = objects.get_object() if hasattr(objects, "get_object") else objects
        images = sum(obj.get_object().get("/Subtype") == "/Image" for obj in objects.values())
        pages.append(dict(page=number, characters=len(text), imageObjects=images,
                          state="text_candidate" if text.strip() else "needs_visual_review"))
    except Exception as error:
        pages.append(dict(page=number, state="failed", error=type(error).__name__))
    if number % 100 == 0:
        print(f"Inventoried {number} pages", flush=True)
inventory_seconds = time.monotonic() - start
renders = []
for number in (11, 51, 101, 201, 401):
    render_start = time.monotonic()
    prefix = args.output / f"ocr-{number}"
    subprocess.run(["pdftoppm", "-f", str(number), "-l", str(number), "-singlefile",
                    "-scale-to", "2000", "-png", str(args.source), str(prefix)],
                   check=True, timeout=60, capture_output=True)
    renders.append(dict(page=number, elapsedSeconds=time.monotonic() - render_start,
                        bytes=prefix.with_suffix(".png").stat().st_size,
                        sha256=digest(prefix.with_suffix(".png"))))
after = digest(args.source)
report = dict(schemaVersion=1, sourceBytes=args.source.stat().st_size, sourceSha256=before,
              sourceUnchanged=before == after, pages=pages, pageCount=len(pages),
              inventorySeconds=inventory_seconds, renders=renders,
              totalSeconds=time.monotonic() - start, ocrPerformed=False)
(args.output / "inventory.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps({k: v for k, v in report.items() if k not in ("pages", "renders")}), flush=True)
if before != after:
    raise RuntimeError("Source changed")
