import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

from tools.embed_frontend import generate


class EmbeddedFrontendTests(unittest.TestCase):
    def test_final_bytes_and_unique_resources_are_preserved(self):
        with tempfile.TemporaryDirectory(prefix="embedded-frontend-") as directory:
            root = Path(directory)
            source = root / "dist"
            (source / "assets").mkdir(parents=True)
            (source / ".vite").mkdir()
            raw = b"\xef\xbb\xbf<html>\r\n"
            (source / "index.html").write_bytes(raw)
            (source / "assets/index-abcdefgh.js").write_bytes(b"export const ready = true;")
            (source / "assets/copy-abcdefgh.js").write_bytes(b"export const ready = true;")
            (source / ".vite/manifest.json").write_text("{}")
            result = generate(source, root / "embedding")
            self.assertEqual(result["frontendHash"], hashlib.sha256(raw).hexdigest())
            self.assertEqual(len(result["files"]), 3)
            self.assertEqual(json.loads((root / "embedding/frontend-index.json").read_bytes()), result)
            declarations = ET.parse(root / "embedding/embedded-frontend.props").findall("./ItemGroup/EmbeddedResource")
            self.assertEqual(len(declarations), 3)
            self.assertEqual(sum(file["immutable"] for file in result["files"]), 2)
            for file in result["files"]:
                self.assertEqual(file["sha256"], hashlib.sha256((source / file["path"]).read_bytes()).hexdigest())

    def test_missing_entry_and_unsafe_asset_paths_are_rejected(self):
        with tempfile.TemporaryDirectory(prefix="embedded-frontend-") as directory:
            source = Path(directory) / "dist"
            source.mkdir()
            with self.assertRaises(ValueError):
                generate(source, Path(directory) / "embedding")
            (source / "index.html").write_text("entry")
            (source / "bad%20name.js").write_text("unsafe")
            with self.assertRaises(ValueError):
                generate(source, Path(directory) / "embedding")
