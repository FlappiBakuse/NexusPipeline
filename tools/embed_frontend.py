"""Index final frontend bytes and emit explicit .NET resource declarations."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

MIME = {".html": "text/html; charset=utf-8", ".js": "application/javascript; charset=utf-8",
        ".css": "text/css; charset=utf-8", ".json": "application/json; charset=utf-8",
        ".svg": "image/svg+xml", ".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg",
        ".webp": "image/webp", ".gif": "image/gif", ".ico": "image/x-icon", ".woff": "font/woff",
        ".woff2": "font/woff2", ".ttf": "font/ttf", ".wasm": "application/wasm"}


def generate(source: Path, output: Path) -> dict:
    source = source.resolve(strict=True)
    if not (source / "index.html").is_file() or source.is_symlink():
        raise ValueError("A final frontend build with index.html is required")
    output.mkdir(parents=True, exist_ok=True)
    files, resources = [], {}
    for file in sorted(source.rglob("*")):
        if file.is_symlink():
            raise ValueError("Linked frontend assets are forbidden")
        if not file.is_file() or ".vite" in file.relative_to(source).parts:
            continue
        relative = file.relative_to(source).as_posix()
        if any(part in {"", ".", ".."} or part.startswith(".") or part.endswith((".", " "))
               for part in relative.split("/")) or re.search(r'[\\:%*?"<>|\x00-\x1f]', relative):
            raise ValueError(f"Invalid frontend asset path: {relative}")
        data = file.read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        name = "NexusPipeline.Frontend.Asset." + digest
        resources.setdefault(name, file)
        files.append({"path": relative, "resourceName": name, "sizeBytes": len(data), "sha256": digest,
                      "contentType": MIME.get(file.suffix.lower(), "application/octet-stream"),
                      "immutable": relative.startswith("assets/") and bool(re.search(r'-[\w-]{8,}\.[a-z0-9]+$', relative))})
    if len(files) > 5000 or len({file["path"].casefold() for file in files}) != len(files):
        raise ValueError("Frontend index exceeds its limit or has colliding paths")
    index = {"schemaVersion": 1, "frontendHash": hashlib.sha256((source / "index.html").read_bytes()).hexdigest(), "files": files}
    index_path = output / "frontend-index.json"
    index_path.write_bytes(json.dumps(index, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))
    project = ET.Element("Project")
    group = ET.SubElement(project, "ItemGroup")
    resources["NexusPipeline.Frontend.Index"] = index_path
    for name, file in sorted(resources.items()):
        element = ET.SubElement(group, "EmbeddedResource", Include=str(file))
        ET.SubElement(element, "LogicalName").text = name
    ET.ElementTree(project).write(output / "embedded-frontend.props", encoding="utf-8", xml_declaration=True)
    return index


def main() -> None:
    parser = argparse.ArgumentParser(allow_abbrev=False)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        index = generate(args.source, args.output)
    except (ValueError, OSError) as error:
        parser.exit(1, f"Frontend embedding failed: {error}\n")
    print(f"Embedded frontend: {len(index['files'])} files, {index['frontendHash']}")


if __name__ == "__main__":
    main()
