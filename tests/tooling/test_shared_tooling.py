"""Detect unsynchronized source and modified consumers through the real producer."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("sync_shared_tooling", ROOT / "tooling/sync_shared_tooling.py")
producer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(producer)


class SharedToolingTests(unittest.TestCase):
    def test_detects_sync_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            owner, consumer = (Path(temporary) / name for name in ("host", "plugins"))
            for root in (owner, consumer):
                (root / "tooling").mkdir(parents=True)
            for name in producer.FILES:
                (owner / "tooling" / name).write_bytes(b"original\n")
            identity = dict(repository=producer.REPOSITORY, sourceMode="local-uncommitted", sourceCommit=None, referenceCommit=None)
            with patch.object(producer, "provenance", return_value=identity):
                producer.sync(owner, consumer)
                producer.sync(owner, consumer, check=True)
                (owner / "tooling/git_source.py").write_bytes(b"changed owner\n")
                with self.assertRaisesRegex(ValueError, "not synchronized"):
                    producer.sync(owner, consumer, check=True)
                producer.sync(owner, consumer)
                (consumer / "tooling/release_github.py").write_bytes(b"changed consumer\n")
                with self.assertRaisesRegex(ValueError, "not synchronized"):
                    producer.sync(owner, consumer, check=True)


if __name__ == "__main__":
    unittest.main()
