import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tooling"))
from build_product import update_export
from source_snapshot import export_source


class SourceSnapshotTests(unittest.TestCase):
    def setUp(self):
        configured = os.environ.get("NEXUS_TEST_ROOT")
        if not configured:
            self.fail("NEXUS_TEST_ROOT must specify an external, owned test root")
        self.base = Path(configured).resolve(strict=True)
        # Nested runner paths plus immutable SHA filenames exceed Win32 MAX_PATH.
        if os.name == "nt" and not str(self.base).startswith("\\\\?\\"):
            self.base = Path("\\\\?\\" + str(self.base))
        self.scope = Path(tempfile.mkdtemp(prefix="source-snapshot-", dir=self.base)).resolve()
        self.repo = self.scope / "repository"
        self.repo.mkdir()
        subprocess.run(["git", "init", "--initial-branch=develop", str(self.repo)],
                       check=True, capture_output=True)
        self.payload = b"UTF-8 original bytes: \xe4\xb8\xad\xe6\x96\x87\r\n"
        (self.repo / "source.txt").write_bytes(self.payload)

    def tearDown(self):
        scope = self.scope.resolve(strict=True)
        if scope.parent != self.base or scope.is_symlink() or scope.is_junction():
            raise ValueError("Test cleanup ownership mismatch")
        for path in (self.repo / ".git/objects").rglob("*"):
            if path.is_file():
                path.chmod(0o600)
        shutil.rmtree(scope)

    def test_preserves_bytes(self):
        """export preserves source bytes and has no invented commit."""
        receipt = export_source(self.repo, self.scope / "export")
        self.assertEqual(self.payload, (Path(receipt["exportRoot"]) / "source.txt").read_bytes())
        self.assertEqual(self.payload, (self.repo / "source.txt").read_bytes())
        self.assertIsNone(receipt["sourceCommit"])
        self.assertIsNone(receipt["sourceTree"])
        self.assertEqual("local-uncommitted", receipt["sourceMode"])

    def test_matches_exports(self):
        """independent exports have the same byte manifest identity."""
        first = export_source(self.repo, self.scope / "first")
        second = export_source(self.repo, self.scope / "second")
        self.assertEqual(first["sourceSnapshotHash"], second["sourceSnapshotHash"])
        self.assertEqual(Path(first["sourceManifestPath"]).read_bytes(),
                         Path(second["sourceManifestPath"]).read_bytes())

    def test_preserves_outputs(self):
        """existing output and containing source are preserved."""
        output = self.scope / "occupied"
        output.mkdir()
        sentinel = output / "user-data.bin"
        sentinel.write_bytes(b"preserve")
        with self.assertRaises(ValueError):
            export_source(self.repo, output)
        self.assertEqual(b"preserve", sentinel.read_bytes())
        for forbidden in (self.repo, self.repo / "nested", self.scope):
            with self.assertRaises(ValueError):
                export_source(self.repo, forbidden)

    def test_updates_identity(self):
        """changed bytes change the snapshot without touching git index."""
        index = self.repo / ".git/index"
        self.assertFalse(index.exists())
        first = export_source(self.repo, self.scope / "first")
        (self.repo / "source.txt").write_bytes(b"edited\n")
        second = export_source(self.repo, self.scope / "second")
        self.assertNotEqual(first["sourceSnapshotHash"], second["sourceSnapshotHash"])
        self.assertFalse(index.exists())

    def test_rejects_unowned(self):
        """incremental export refuses changed or unowned files."""
        output = self.scope / "export"
        export_source(self.repo, output)
        source = output / "source/source.txt"
        source.write_bytes(b"unexplained")
        with self.assertRaises(ValueError):
            update_export(self.repo, output)
        self.assertEqual(b"unexplained", source.read_bytes())
        source.write_bytes(self.payload)
        unknown = output / "source/user.txt"
        unknown.write_bytes(b"user")
        with self.assertRaises(ValueError):
            update_export(self.repo, output)
        self.assertEqual(b"user", unknown.read_bytes())

    def test_updates_export(self):
        """incremental export updates only owned inputs and retains manifest."""
        output = self.scope / "export"
        subprocess.run(["git", "-C", str(self.repo), "add", "source.txt"], check=True, capture_output=True)
        index = (self.repo / ".git/index").read_bytes()
        before = export_source(self.repo, output)
        old_manifest = Path(before["sourceManifestPath"]).read_bytes()
        build = output / "build/keep.bin"
        build.parent.mkdir()
        build.write_bytes(b"compiled")
        (self.repo / "source.txt").unlink()
        (self.repo / "new.txt").write_bytes(b"new\n")
        after = update_export(self.repo, output)
        self.assertEqual(index, (self.repo / ".git/index").read_bytes())
        self.assertFalse((output / "source/source.txt").exists())
        self.assertEqual(b"new\n", (output / "source/new.txt").read_bytes())
        self.assertEqual(b"compiled", build.read_bytes())
        archived = output / "source-manifests" / (before["sourceSnapshotHash"] + ".json")
        self.assertEqual(old_manifest, archived.read_bytes())
        old = json.loads(archived.read_bytes())
        restored = {record["path"]: (output / "source-blobs" / record["sha256"]).read_bytes() for record in old["files"]}
        self.assertEqual({"source.txt": self.payload}, restored)
        self.assertEqual(archived, Path(before["sourceManifestPath"]))
        self.assertNotEqual(before["sourceSnapshotHash"], after["sourceSnapshotHash"])

    def test_rejects_corruption(self):
        """corrupted immutable source bytes cannot be overwritten or used to replace the export."""
        output = self.scope / "export"
        original = export_source(self.repo, output)
        blob = output / "source-blobs" / hashlib.sha256(self.payload).hexdigest()
        blob.write_bytes(b"unexplained evidence")
        (self.repo / "source.txt").write_bytes(b"next revision\n")
        with self.assertRaises(ValueError):
            update_export(self.repo, output)
        self.assertEqual(b"unexplained evidence", blob.read_bytes())
        self.assertEqual(self.payload, (output / "source/source.txt").read_bytes())
        self.assertEqual(original["sourceSnapshotHash"], json.loads((output / "source-receipt.json").read_bytes())["sourceSnapshotHash"])

    def test_hashes_manifest(self):
        """manifest digest covers exact manifest bytes."""
        receipt = export_source(self.repo, self.scope / "export")
        data = Path(receipt["sourceManifestPath"]).read_bytes()
        self.assertEqual(hashlib.sha256(data).hexdigest(), receipt["sourceSnapshotHash"])
        manifest = json.loads(data)
        self.assertNotIn("sourceSnapshotHash", manifest)
        self.assertEqual(hashlib.sha256(self.payload).hexdigest(), manifest["files"][0]["sha256"])


if __name__ == "__main__":
    unittest.main()
