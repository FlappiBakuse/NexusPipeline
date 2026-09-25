from __future__ import annotations

import tempfile
import unittest
import zipfile
import json
import hashlib
from unittest.mock import Mock, patch
from pathlib import Path

from tools.host_release import HostReleaseError, archive_production, extract_candidate_artifact, inspect_candidate_identity, normalized_tag, parse_version, validate_candidate_data, verify_received_installer, verify_received_package, write_candidate_manifest
from tools import host_release


class HostReleaseTests(unittest.TestCase):
    def test_candidate_identity_separates_source_from_manual_controller(self) -> None:
        with tempfile.TemporaryDirectory(prefix='nxp-host-candidate-identity-') as temporary:
            output = Path(temporary)
            candidate = {
                'sourceSha': 'a' * 40,
                'partnerSha': 'b' * 40,
                'producer': {'workflowPath': '.github/workflows/release.yml',
                             'workflowSha': 'c' * 40, 'runId': 12,
                             'runAttempt': 2, 'jobName': 'candidate'},
            }
            (output / 'candidate.json').write_text(json.dumps(candidate), encoding='utf-8')
            self.assertEqual(inspect_candidate_identity(output, workflow_sha='c' * 40,
                                                        run_id=12, run_attempt=2),
                             {'sourceSha': 'a' * 40, 'partnerSha': 'b' * 40})
            with self.assertRaisesRegex(HostReleaseError, 'producer'):
                inspect_candidate_identity(output, workflow_sha='d' * 40,
                                           run_id=12, run_attempt=2)
    def test_artifact_digest_and_paths_are_checked_before_extraction(self) -> None:
        with tempfile.TemporaryDirectory(prefix='nxp-host-artifact-test-') as temporary:
            root = Path(temporary)
            archive = root / 'artifact.zip'
            names = ['candidate.json', 'build-metadata.json', 'installer-build-metadata.json',
                     'NexusPipeline-v1.2.3-win-x64.zip', 'NexusPipeline-v1.2.3-win-x64.zip.sha256',
                     'NexusPipeline-v1.2.3-win-x64-setup.exe', 'NexusPipeline-v1.2.3-win-x64-setup.exe.sha256']
            with zipfile.ZipFile(archive, 'w') as stream:
                for name in names:
                    stream.writestr(name, b'data')
            digest = 'sha256:' + hashlib.sha256(archive.read_bytes()).hexdigest()
            with self.assertRaisesRegex(HostReleaseError, 'SHA256'):
                extract_candidate_artifact(archive, root / 'wrong', expected_digest='sha256:' + '0' * 64)
            self.assertFalse((root / 'wrong').exists())
            extract_candidate_artifact(archive, root / 'valid', expected_digest=digest)
            self.assertEqual({item.name for item in (root / 'valid').iterdir()}, set(names))
            with zipfile.ZipFile(archive, 'w') as stream:
                for name in [*names[:3], '../escape.zip', *names[4:]]:
                    stream.writestr(name, b'data')
            hostile_digest = 'sha256:' + hashlib.sha256(archive.read_bytes()).hexdigest()
            with self.assertRaisesRegex(HostReleaseError, '路径无效'):
                extract_candidate_artifact(archive, root / 'hostile', expected_digest=hostile_digest)
            self.assertFalse((root / 'hostile').exists())

    def test_candidate_inventory_binds_original_producer_and_rejects_extra_files(self) -> None:
        with tempfile.TemporaryDirectory(prefix='nxp-host-candidate-test-') as temporary:
            root = Path(temporary)
            (root / 'src').mkdir()
            (root / 'src' / 'NexusPipeline.csproj').write_text('<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>', encoding='utf-8')
            output = root / 'candidate'
            output.mkdir()
            package = output / 'NexusPipeline-v1.2.3-win-x64.zip'
            package.write_bytes(b'package')
            digest = hashlib.sha256(package.read_bytes()).hexdigest()
            (output / f'{package.name}.sha256').write_text(digest, encoding='ascii')
            (output / 'build-metadata.json').write_text('{}', encoding='utf-8')
            setup = output / 'NexusPipeline-v1.2.3-win-x64-setup.exe'
            setup.write_bytes(b'installer')
            (output / f'{setup.name}.sha256').write_text(hashlib.sha256(setup.read_bytes()).hexdigest(), encoding='ascii')
            (output / 'installer-build-metadata.json').write_text('{}', encoding='utf-8')
            def git_result(_root, *args):
                if args == ('rev-parse', 'HEAD'): return 'a' * 40
                if args == ('status', '--porcelain=v1', '--untracked-files=all'): return ''
                if args == ('rev-parse', f"{'a' * 40}^{{tree}}"): return 'b' * 40
                raise AssertionError(args)
            with patch.object(host_release, 'git_output', side_effect=git_result), \
                 patch.object(host_release, 'verify_received_package', return_value={'sha256': digest}), \
                 patch.object(host_release, 'verify_received_installer'):
                candidate = write_candidate_manifest(root, output, source_sha='a' * 40,
                    partner_sha='c' * 40, workflow_sha='d' * 40, run_id=12, run_attempt=2)
                self.assertEqual(candidate['producer']['runAttempt'], 2)
                self.assertEqual(candidate['sourceTreeSha'], 'b' * 40)
                self.assertEqual({item['path'] for item in candidate['files']},
                                 {package.name, package.name + '.sha256', setup.name, setup.name + '.sha256',
                                  'build-metadata.json', 'installer-build-metadata.json'})
                self.assertNotIn('candidate.json', {item['path'] for item in candidate['files']})
                with patch.object(host_release, 'project_version', return_value='1.2.3'):
                    self.assertEqual(validate_candidate_data(root, output, expected_source_sha='a' * 40,
                        expected_producer=candidate['producer'], expected_partner_sha='c' * 40), candidate)
                    with self.assertRaisesRegex(HostReleaseError, 'producer'):
                        validate_candidate_data(root, output, expected_source_sha='a' * 40,
                            expected_producer={**candidate['producer'], 'runAttempt': 3}, expected_partner_sha='c' * 40)
                    with self.assertRaisesRegex(HostReleaseError, 'partner'):
                        validate_candidate_data(root, output, expected_source_sha='a' * 40,
                            expected_producer=candidate['producer'], expected_partner_sha=None)
                (output / 'candidate.json').unlink()
                (output / 'unexpected.txt').write_text('extra', encoding='utf-8')
                with self.assertRaisesRegex(HostReleaseError, '文件集合无效'):
                    write_candidate_manifest(root, output, source_sha='a' * 40,
                        partner_sha='c' * 40, workflow_sha='d' * 40, run_id=12, run_attempt=2)

    def test_installer_metadata_binds_zip_payload_dependencies_and_remote_bytes(self) -> None:
        with tempfile.TemporaryDirectory(prefix='nxp-installer-candidate-') as temporary:
            root = Path(temporary)
            output = root / 'candidate'
            output.mkdir()
            tools_dir = root / 'tools'
            tools_dir.mkdir()
            dependency = {'framework': 'Microsoft.WindowsDesktop.App', 'version': '8.0.31',
                          'rid': 'win-x64', 'url': 'https://builds.dotnet.microsoft.com/dotnet/one.exe',
                          'sha256': 'a' * 64}
            other = {**dependency, 'framework': 'Microsoft.AspNetCore.App'}
            (tools_dir / 'runtime-dependencies.json').write_text(
                json.dumps({'schemaVersion': 1, 'dependencies': [dependency, other]}), encoding='utf-8')
            setup = output / 'NexusPipeline-v1.2.3-win-x64-setup.exe'
            setup.write_bytes(b'installer')
            digest = hashlib.sha256(setup.read_bytes()).hexdigest()
            (output / f'{setup.name}.sha256').write_text(digest, encoding='ascii')
            payload = [{'path': 'README.md', 'sha256': 'b' * 64, 'sizeBytes': 1}]
            from tools.host_installer import INNO_COMPILER_SHA256, INNO_DISTRIBUTION_SHA256
            data = {'schemaVersion': 1, 'sourceSha': 'a' * 40, 'tag': 'v1.2.3',
                    'version': '1.2.3', 'zipSha256': 'c' * 64, 'setupSha256': digest,
                    'setupSizeBytes': setup.stat().st_size, 'payloadFiles': payload,
                    'compilerSha256': INNO_COMPILER_SHA256,
                    'compilerDistributionSha256': INNO_DISTRIBUTION_SHA256,
                    'dependencies': [dependency, other]}
            manifest = output / 'installer-build-metadata.json'
            manifest.write_text(json.dumps(data), encoding='utf-8')
            zip_metadata = {'sha256': 'c' * 64, 'payloadFiles': payload}
            self.assertEqual(verify_received_installer(output, root, zip_metadata,
                             expected_source_sha='a' * 40, expected_tag='v1.2.3'), data)
            received = root / 'remote-download.exe'
            received.write_bytes(b'changed')
            with self.assertRaisesRegex(HostReleaseError, '摘要或大小'):
                verify_received_installer(output, root, zip_metadata,
                    expected_source_sha='a' * 40, expected_tag='v1.2.3', setup_override=received)
            data['payloadFiles'] = []
            manifest.write_text(json.dumps(data), encoding='utf-8')
            with self.assertRaisesRegex(HostReleaseError, '应用载荷'):
                verify_received_installer(output, root, zip_metadata,
                    expected_source_sha='a' * 40, expected_tag='v1.2.3')

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
        info.file_size = host_release.MAX_SINGLE_ENTRY_BYTES
        info.compress_size = info.file_size
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
            (production / "README.md").write_bytes(b"README\n")
            for artifact in ("EmulatorSupport", "LiveScreenshot"):
                plugin = production / "plugins" / artifact
                plugin.mkdir(parents=True)
                (plugin / "plugin.json").write_bytes(b"{\"name\":\"fixture\"}\r\n")
            (production / "config").mkdir()
            (production / "config" / "secret.json").write_text("secret", encoding="utf-8")
            output = root / "out"
            with self.assertRaises(HostReleaseError):
                archive_production(production, output, "v1.2.3", source_sha="a" * 40, manifest_path=root / "src" / "app.manifest")
            (production / "config" / "secret.json").unlink()
            (production / "config").rmdir()
            first = archive_production(production, output, "v1.2.3", source_sha="a" * 40, manifest_path=root / "src" / "app.manifest")
            self.assertEqual(Path(first["zip"]).name, "NexusPipeline-v1.2.3-win-x64.zip")
            self.assertEqual(Path(first["sha"]).name, "NexusPipeline-v1.2.3-win-x64.zip.sha256")
            self.assertEqual(Path(first["sha"]).read_bytes(), first["metadata"]["sha256"].encode("ascii"))
            first_bytes = Path(first["zip"]).read_bytes()
            second = archive_production(production, output, "v1.2.3", source_sha="a" * 40, manifest_path=root / "src" / "app.manifest")
            self.assertEqual(first_bytes, Path(second["zip"]).read_bytes())
            with zipfile.ZipFile(second["zip"]) as archive:
                self.assertEqual(archive.namelist(), ["nexus-pipeline.exe", "plugins/EmulatorSupport/plugin.json", "plugins/LiveScreenshot/plugin.json", "README.md", "wwwroot/index.html"])
                self.assertEqual(archive.read("wwwroot/index.html"), b"<html>\r\n</html>")
                self.assertEqual(archive.read("plugins/EmulatorSupport/plugin.json"), b"{\"name\":\"fixture\"}\r\n")

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
