from __future__ import annotations

import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

from tools.host_release import HostReleaseError, stage_bundled_plugins, build_bundled_phase, consume_bundled_phase
from unittest.mock import patch


class BundledPluginTests(unittest.TestCase):
    def fixture(self, root, *, version='1.2.3', canonical=True, bad_entry=None):
        host, partner = root/'host', root/'partner'
        policy = host/'src/Modules/Plugins/Repository/BundledPlugins.json'
        policy.parent.mkdir(parents=True, exist_ok=True)
        (host/'src/NexusPipeline.csproj').write_text('<Project><PropertyGroup><Version>0.17.5</Version></PropertyGroup></Project>')
        (host/'src/NexusPipeline.Plugin.Abstractions').mkdir(exist_ok=True)
        (host/'src/NexusPipeline.Plugin.Abstractions/PluginApi.cs').write_text('public static class PluginApiVersion { public const int Major = 2; public const int Minor = 2; }')
        policy.write_text(json.dumps({'schemaVersion': 1, 'repository': 'FlappiBakuse/NexusPipeline-Plugins',
                                     'plugins': [{'artifactName': a} for a in ('EmulatorSupport', 'LiveScreenshot')]}))
        entries = []
        for artifact in ('EmulatorSupport', 'LiveScreenshot'):
            plugin = {'schemaVersion': 2, 'name': 'emulator-support' if artifact == 'EmulatorSupport' else 'live-screenshot',
                      'artifactName': artifact, 'version': version, 'kind': 'managed-code', 'apiVersion': '2.2', 'minHostVersion': '0.17.2'}
            package = partner/'packages'/artifact/f'{artifact}-{version}.zip'
            package.parent.mkdir(parents=True, exist_ok=True)
            with zipfile.ZipFile(package, 'w') as archive:
                archive.writestr('plugin.json', json.dumps(plugin))
                if bad_entry:
                    info = zipfile.ZipInfo(bad_entry)
                    info.filename = bad_entry
                    if bad_entry == 'linked':
                        info.external_attr = 0o120777 << 16
                    archive.writestr(info, 'payload')
            entries.append({**plugin, 'sha256': hashlib.sha256(package.read_bytes()).hexdigest(), 'sizeBytes': package.stat().st_size,
                            'packageUrl': f'https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/{artifact}/{artifact}-{version}.zip'})
        catalog = partner/'catalog.json'
        catalog.write_text(json.dumps({'schemaVersion': 2, 'repository': 'FlappiBakuse/NexusPipeline-Plugins', 'plugins': entries}))
        if canonical:
            target = partner/'metadata/catalog/stable.json'
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(catalog.read_bytes())
        return host, partner, policy

    def test_dynamic_versions_and_exact_resolved_file_identity(self):
        for canonical in (False, True):
            with self.subTest(canonical=canonical), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                for index, version in enumerate(('1.2.3', '1.2.4', '1.2.5-beta.1')):
                    host, partner, policy = self.fixture(root, version=version, canonical=canonical)
                    resolved = stage_bundled_plugins(root/f'production-{index}', partner, policy)
                    self.assertNotIn('version', json.loads(policy.read_bytes())['plugins'][0])
                    for plugin in resolved['plugins']:
                        self.assertEqual(plugin['version'], version)
                        for file in plugin['files']:
                            data = (root/f'production-{index}'/'plugins'/plugin['artifactName']/file['path']).read_bytes()
                            self.assertEqual(file['sizeBytes'], len(data))
                            self.assertEqual(file['sha256'], hashlib.sha256(data).hexdigest())

    def test_catalog_and_package_drift_fail_before_staging(self):
        for mode in ('projection', 'package', 'missing', 'duplicate', 'manifest', 'minimum', 'api', 'url', 'version'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                host, partner, policy = self.fixture(root)
                path = partner/'catalog.json'
                catalog = json.loads(path.read_bytes())
                package = next((partner/'packages/EmulatorSupport').glob('*.zip'))
                if mode == 'projection':
                    path.write_text('{}')
                elif mode == 'package':
                    package.write_bytes(package.read_bytes()+b'drift')
                else:
                    if mode == 'missing': catalog['plugins'].pop()
                    if mode == 'duplicate': catalog['plugins'].append(catalog['plugins'][0])
                    if mode == 'manifest': catalog['plugins'][0]['name'] = 'different-name'
                    if mode == 'minimum': catalog['plugins'][0]['minHostVersion'] = '0.17.6'
                    if mode == 'api': catalog['plugins'][0]['apiVersion'] = '2.3'
                    if mode == 'url': catalog['plugins'][0]['packageUrl'] = 'https://example.org/package.zip'
                    if mode == 'version': catalog['plugins'][0]['version'] = '../escape'
                    path.write_text(json.dumps(catalog))
                    (partner/'metadata/catalog/stable.json').write_bytes(path.read_bytes())
                with self.assertRaises(HostReleaseError):
                    stage_bundled_plugins(root/'production', partner, policy)
                self.assertFalse((root/'production/plugins').exists())

    def test_unsafe_zip_entries_fail_before_staging(self):
        for entry in ('../escape', 'PLUGIN.JSON', 'linked', 'dir\\file', 'NUL.txt'):
            with self.subTest(entry=entry), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                _, partner, policy = self.fixture(root, bad_entry=entry)
                with self.assertRaises(HostReleaseError):
                    stage_bundled_plugins(root/'production', partner, policy)
                self.assertFalse((root/'production/plugins').exists())

    def test_phase_preserves_resolved_manifest_and_rejects_tampering(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            host, partner, policy = self.fixture(root)
            phase = root/'phase'
            with patch('tools.host_release.git_output', return_value='a'*40):
                build_bundled_phase(host, partner, phase, source_sha='a'*40, partner_sha='a'*40)
            consume_bundled_phase(host, phase, root/'production', source_sha='a'*40, partner_sha='a'*40)
            self.assertEqual((root/'production/plugins/EmulatorSupport/plugin.json').read_bytes(),
                             (phase/'plugins/EmulatorSupport/plugin.json').read_bytes())
            (phase/'BundledPlugins.json').write_text('{}')
            with self.assertRaises(HostReleaseError):
                consume_bundled_phase(host, phase, root/'rejected', source_sha='a'*40, partner_sha='a'*40)
