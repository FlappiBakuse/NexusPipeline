import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tooling"))
from sdk_workspace import synchronize


class SdkWorkspaceTests(unittest.TestCase):
    def setUp(self):
        self.base = Path(os.environ["NEXUS_TEST_ROOT"]).resolve(strict=True)
        self.scope = Path(tempfile.mkdtemp(prefix="sdk-workspace-", dir=self.base)).resolve()
        self.source = self.scope / "source"
        self.target = self.scope / "workspace"
        self.ownership = self.scope / "ownership.json"
        self.source.mkdir()
        (self.source / "consumer.cs").write_bytes(b"first-source")

    def tearDown(self):
        if self.scope.parent != self.base or self.scope.is_symlink() or self.scope.is_junction():
            raise ValueError("SDK fixture cleanup ownership mismatch")
        shutil.rmtree(self.scope)

    def test_reuses_bytes(self):
        synchronize(self.source, self.target, self.ownership)
        (self.source / "consumer.cs").unlink()
        (self.source / "replacement.cs").write_bytes(b"replacement-source")
        result = synchronize(self.source, self.target, self.ownership)
        self.assertFalse((self.target / "consumer.cs").exists())
        self.assertEqual(b"replacement-source", (self.target / "replacement.cs").read_bytes())
        self.assertEqual(result, json.loads(self.ownership.read_bytes())["records"])

    def test_preserves_changes(self):
        synchronize(self.source, self.target, self.ownership)
        (self.target / "consumer.cs").write_bytes(b"unknown-local-edit")
        (self.source / "consumer.cs").write_bytes(b"next-source")
        with self.assertRaises(ValueError):
            synchronize(self.source, self.target, self.ownership)
        self.assertEqual(b"unknown-local-edit", (self.target / "consumer.cs").read_bytes())

    def test_preserves_extras(self):
        synchronize(self.source, self.target, self.ownership)
        (self.target / "unowned.cs").write_bytes(b"unowned")
        with self.assertRaises(ValueError):
            synchronize(self.source, self.target, self.ownership)
        self.assertEqual(b"unowned", (self.target / "unowned.cs").read_bytes())

    def test_adopts_exact(self):
        self.target.mkdir()
        (self.target / "consumer.cs").write_bytes(b"changed")
        with self.assertRaises(ValueError):
            synchronize(self.source, self.target, self.ownership)
        self.assertFalse(self.ownership.exists())
        (self.target / "consumer.cs").write_bytes(b"first-source")
        synchronize(self.source, self.target, self.ownership)
        self.assertEqual(str(self.target), json.loads(self.ownership.read_bytes())["target"])

    def test_rejects_overlap(self):
        with self.assertRaises(ValueError):
            synchronize(self.source, self.source / "output", self.ownership)
        self.assertEqual(b"first-source", (self.source / "consumer.cs").read_bytes())
        self.assertFalse(self.ownership.exists())
