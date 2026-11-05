import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tooling"))
from sdk_packages import require_build
from source_snapshot import digest, source_files, write_json


class SdkPackageTests(unittest.TestCase):
    def setUp(self):
        self.base = Path(os.environ["NEXUS_TEST_ROOT"]).resolve(strict=True)
        self.scope = Path(tempfile.mkdtemp(prefix="sdk-producer-", dir=self.base)).resolve()
        self.source, self.build, self.frozen = [self.scope / name for name in ("source", "build", "frozen")]
        for path in (self.source, self.build, self.frozen):
            path.mkdir()
        subprocess.run(["git", "init", "--quiet", str(self.source)], check=True, capture_output=True)
        for path in (self.source, self.frozen):
            (path / "sdk.cs").write_bytes(b"public sealed class Exported {}")
        self.manifest = self.scope / "source-manifest.json"
        write_json(self.manifest, {"files": source_files(self.source)})
        self.receipt = {"profile": "test", "exitCode": 0, "sdkApi": {"exitCode": 0},
            "componentInputs": {"projectNames": ["NexusPipeline.Extensibility", "NexusPipeline.Boundaries"]},
            "source": {"sourceManifestPath": str(self.manifest), "sourceSnapshotHash": digest(self.manifest.read_bytes()),
                       "exportRoot": str(self.frozen)}}

    def tearDown(self):
        if self.scope.parent != self.base or self.scope.is_symlink() or self.scope.is_junction():
            raise ValueError("SDK producer fixture cleanup ownership mismatch")
        shutil.rmtree(self.scope)

    def read(self):
        write_json(self.build / "build-receipt.json", self.receipt)
        return require_build(self.source, self.build)

    def test_sdk_only(self):
        receipt, frozen, files = self.read()
        self.assertEqual(self.receipt, receipt)
        self.assertEqual(self.frozen, frozen)
        self.assertEqual(source_files(self.source), files)

    def test_api_verification(self):
        for value in ({}, {"exitCode": 1}):
            with self.subTest(value=value):
                self.receipt["sdkApi"] = value
                with self.assertRaises(ValueError):
                    self.read()

    def test_sdk_projects(self):
        for missing in ("NexusPipeline.Extensibility", "NexusPipeline.Boundaries"):
            with self.subTest(missing=missing):
                self.receipt["componentInputs"]["projectNames"] = [name for name in
                    ("NexusPipeline.Extensibility", "NexusPipeline.Boundaries") if name != missing]
                with self.assertRaises(ValueError):
                    self.read()

    def test_source_drift(self):
        (self.source / "sdk.cs").write_bytes(b"changed current source")
        with self.assertRaisesRegex(ValueError, "Current Host sources differ"):
            self.read()

    def test_export_drift(self):
        (self.frozen / "sdk.cs").write_bytes(b"changed export")
        with self.assertRaisesRegex(ValueError, "producer export changed"):
            self.read()

    def test_failed_build(self):
        self.receipt["exitCode"] = 1
        with self.assertRaises(ValueError):
            self.read()
