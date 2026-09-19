from __future__ import annotations

import tempfile
import unittest
import zipfile
from pathlib import Path

from tools.host_release import HostReleaseError, archive_production, normalized_tag, parse_version


class HostReleaseTests(unittest.TestCase):
    def test_semver_and_tag_normalization(self) -> None:
        self.assertEqual(normalized_tag("1.2.3"), "v1.2.3")
        self.assertEqual(normalized_tag("v0.1.0-beta.2"), "v0.1.0-beta.2")
        with self.assertRaises(HostReleaseError):
            parse_version("v1.2")

    def test_archive_is_deterministic_and_excludes_runtime_data(self) -> None:
        with tempfile.TemporaryDirectory(prefix="nxp-host-release-test-") as temporary:
            root = Path(temporary)
            (root / "app.manifest").write_text(
                '<assembly xmlns="urn:schemas-microsoft-com:asm.v1"><trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="requireAdministrator" uiAccess="false" /></requestedPrivileges></security></trustInfo></assembly>',
                encoding="utf-8",
            )
            production = root / "production"
            production.mkdir()
            (production / "nexus-pipeline.exe").write_bytes(b"binary")
            (production / "wwwroot").mkdir()
            (production / "wwwroot" / "index.html").write_bytes(b"<html>\r\n</html>")
            (production / "config").mkdir()
            (production / "config" / "secret.json").write_text("secret", encoding="utf-8")
            output = root / "out"
            with self.assertRaises(HostReleaseError):
                archive_production(production, output, "v1.2.3", source_sha="a" * 40, manifest_root=root)
            (production / "config" / "secret.json").unlink()
            (production / "config").rmdir()
            first = archive_production(production, output, "v1.2.3", source_sha="a" * 40, manifest_root=root)
            first_bytes = Path(first["zip"]).read_bytes()
            second = archive_production(production, output, "v1.2.3", source_sha="a" * 40, manifest_root=root)
            self.assertEqual(first_bytes, Path(second["zip"]).read_bytes())
            with zipfile.ZipFile(second["zip"]) as archive:
                self.assertEqual(archive.namelist(), ["nexus-pipeline.exe", "wwwroot/index.html"])
                self.assertEqual(archive.read("wwwroot/index.html"), b"<html>\n</html>")


if __name__ == "__main__":
    unittest.main()
