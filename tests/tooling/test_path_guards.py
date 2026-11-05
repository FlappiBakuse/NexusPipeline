import os
import subprocess
from unittest.mock import patch

from ci_fixture import GitFixture
from source_snapshot import export_source
from build_product import build_product
from test_runner import run_tests, prepare_output
from sdk_packages import build_sdk


class PathGuardTests(GitFixture):
    def linked_output(self):
        target = self.scope / "owned-target"
        target.mkdir()
        (target / "preserve.txt").write_bytes(b"original bytes")
        link = self.scope / "alias"
        if os.name == "nt":
            subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)], check=True, capture_output=True)
        else:
            link.symlink_to(target, target_is_directory=True)
        return link, target

    def test_refuses_export(self):
        link, target = self.linked_output()
        try:
            with self.assertRaisesRegex(ValueError, "[Ll]ink|reparse"):
                export_source(self.repo, link / "export")
            self.assertEqual(["preserve.txt"], [path.name for path in target.iterdir()])
            self.assertEqual(b"original bytes", (target / "preserve.txt").read_bytes())
        finally:
            link.rmdir() if os.name == "nt" else link.unlink()

    def test_refuses_build(self):
        link, target = self.linked_output()
        try:
            with patch("build_product.update_export", side_effect=AssertionError("Output guard must precede source export")):
                with self.assertRaisesRegex(ValueError, "[Ll]ink|reparse"):
                    build_product(self.repo, link / "build", "test")
            self.assertEqual(["preserve.txt"], [path.name for path in target.iterdir()])
        finally:
            link.rmdir() if os.name == "nt" else link.unlink()

    def test_refuses_tests(self):
        link, target = self.linked_output()
        try:
            with patch("test_runner.source_files", side_effect=AssertionError("Output guard must precede source reads")):
                for build, output in ((link / "build", self.scope / "results"), (self.scope / "build", link / "results")):
                    with self.subTest(build=str(build), output=str(output)), self.assertRaisesRegex(ValueError, "[Ll]ink|reparse"):
                        run_tests(self.repo, build, output, "tooling")
            self.assertEqual(["preserve.txt"], [path.name for path in target.iterdir()])
        finally:
            link.rmdir() if os.name == "nt" else link.unlink()

    def test_refuses_foreign(self):
        target = self.scope / "foreign-output"
        target.mkdir()
        sentinel = target / "preserve.txt"
        sentinel.write_bytes(b"original bytes")
        with self.assertRaisesRegex(ValueError, "not owned"):
            prepare_output(self.repo, self.scope / "build", target)
        self.assertEqual([sentinel], list(target.iterdir()))
        owned = self.scope / "owned-output"
        prepare_output(self.repo, self.scope / "build", owned)
        prepare_output(self.repo, self.scope / "build", owned)
        with self.assertRaisesRegex(ValueError, "not owned"):
            prepare_output(self.repo, self.scope / "other-build", owned)

    def test_refuses_sdk(self):
        link, target = self.linked_output()
        consumers = self.scope / "consumers"
        consumers.mkdir()
        arguments = dict(root=self.repo, build=self.scope / "build", consumers=consumers,
                         output=self.scope / "sdk", cache=self.scope / "cache")
        try:
            with patch("sdk_packages.require_build", side_effect=AssertionError("Path guard must precede SDK evidence reads")):
                for name in ("build", "consumers", "output", "cache"):
                    with self.subTest(name=name), self.assertRaisesRegex(ValueError, "[Ll]ink|reparse"):
                        build_sdk(**{**arguments, name: link / name})
            self.assertEqual(["preserve.txt"], [path.name for path in target.iterdir()])
        finally:
            link.rmdir() if os.name == "nt" else link.unlink()
