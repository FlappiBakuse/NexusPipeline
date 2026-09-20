from __future__ import annotations

import tempfile
import unittest
import zipfile
import json
import hashlib
from unittest.mock import Mock, patch
from pathlib import Path

from tools.host_release import HostReleaseError, archive_production, normalized_tag, parse_version, verify_received_package
from tools import host_release


class HostReleaseTests(unittest.TestCase):
    def test_writer_rejects_unsafe_layout_before_reading_pe(self) -> None:
        cases = [ ['wwwroot', 'wwwroot/index.html'], ['wwwroot', 'wwwroot/sub/'],
                  ['CON.txt'], ['wwwroot/bad?.js'], ['C:/file'], ['file:ads'],
                  ['a', 'A'], ['plugin.json', 'plugin.json'], ['other.exe'], ['logs/private.txt'] ]
        with tempfile.TemporaryDirectory(prefix='nxp-host-layout-') as temporary:
            root = Path(temporary)
            for entries in cases:
                package = root / 'candidate.zip'
                with zipfile.ZipFile(package, 'w') as archive:
                    archive.writestr('nexus-pipeline.exe', b'not-read')
                    for entry in entries:
                        archive.writestr(entry, b'' if entry.endswith('/') else b'data')
                metadata = root / 'metadata.json'
                metadata.write_text(json.dumps({'schemaVersion':1, 'sourceSha':'a'*40, 'tag':'v1.2.3', 'version':'1.2.3', 'mode':'production', 'sha256':hashlib.sha256(package.read_bytes()).hexdigest(), 'sizeBytes':package.stat().st_size}), encoding='utf-8')
                verifier = Mock()
                with self.subTest(entries=entries), self.assertRaises(HostReleaseError):
                    verify_received_package(package, metadata, expected_source_sha='a'*40, expected_tag='v1.2.3', manifest_verifier=verifier)
                verifier.assert_not_called()

    def test_writer_enforces_types_and_expansion_limits(self) -> None:
        for mode in (0o120777, 0o010644, 0o060644):
            info = zipfile.ZipInfo('nexus-pipeline.exe')
            info.external_attr = mode << 16
            with self.subTest(mode=mode), self.assertRaises(HostReleaseError):
                host_release._validate_package_layout([info])
        info = zipfile.ZipInfo('nexus-pipeline.exe')
        info.file_size = host_release.MAX_PACKAGE_UNCOMPRESSED_BYTES
        host_release._validate_package_layout([info])
        info.file_size += 1
        with self.assertRaises(HostReleaseError): host_release._validate_package_layout([info])
        with patch.object(host_release, 'MAX_PACKAGE_ENTRIES', 0), self.assertRaises(HostReleaseError):
            host_release._validate_package_layout([info])

    def test_semver_and_tag_normalization(self) -> None:
        self.assertEqual(normalized_tag("1.2.3"), "v1.2.3")
        self.assertEqual(normalized_tag("v0.1.0-beta.2"), "v0.1.0-beta.2")
        with self.assertRaises(HostReleaseError):
            parse_version("v1.2")

    def test_archive_is_deterministic_and_excludes_runtime_data(self) -> None:
        with tempfile.TemporaryDirectory(prefix="nxp-host-release-test-") as temporary:
            root = Path(temporary)
            (root / "src").mkdir()
            (root / "src" / "app.manifest").write_text(
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
                archive_production(production, output, "v1.2.3", source_sha="a" * 40, manifest_path=root / "src" / "app.manifest")
            (production / "config" / "secret.json").unlink()
            (production / "config").rmdir()
            first = archive_production(production, output, "v1.2.3", source_sha="a" * 40, manifest_path=root / "src" / "app.manifest")
            first_bytes = Path(first["zip"]).read_bytes()
            second = archive_production(production, output, "v1.2.3", source_sha="a" * 40, manifest_path=root / "src" / "app.manifest")
            self.assertEqual(first_bytes, Path(second["zip"]).read_bytes())
            with zipfile.ZipFile(second["zip"]) as archive:
                self.assertEqual(archive.namelist(), ["nexus-pipeline.exe", "wwwroot/index.html"])
                self.assertEqual(archive.read("wwwroot/index.html"), b"<html>\n</html>")

            verified = verify_received_package(
                Path(second["zip"]),
                output / "build-metadata.json",
                expected_source_sha="a" * 40,
                expected_tag="v1.2.3",
                manifest_verifier=lambda _path, _level: None,
            )
            self.assertEqual(verified["sha256"], second["metadata"]["sha256"])

            corrupt = output / "corrupt.zip"
            corrupt.write_bytes(Path(second["zip"]).read_bytes() + b"corrupt")
            with self.assertRaisesRegex(HostReleaseError, "SHA256"):
                verify_received_package(
                    corrupt,
                    output / "build-metadata.json",
                    expected_source_sha="a" * 40,
                    expected_tag="v1.2.3",
                    manifest_verifier=lambda _path, _level: None,
                )


if __name__ == "__main__":
    unittest.main()
