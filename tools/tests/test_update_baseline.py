from __future__ import annotations

import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import io
import zipfile

from tools import update_baseline


class UpdateBaselineTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='nxp-baseline-test-')
        self.addCleanup(temporary.cleanup)
        self.package = Path(temporary.name) / 'baseline.zip'
        self.manifest = {'schemaVersion': 1, 'product': 'NexusPipeline', 'productVersion': '0.17.3',
                         'installationGeneration': 'g0170', 'rid': 'win-x64', 'buildId': 'a' * 64,
                         'frontendHash': 'b' * 64, 'files': [{'path': 'NexusPipeline.exe', 'sizeBytes': 4,
                         'sha256': hashlib.sha256(b'host').hexdigest(), 'kind': 'host'}]}
        self.write()

    def write(self, *, host=b'host', extra=None):
        with zipfile.ZipFile(self.package, 'w') as archive:
            archive.writestr('NexusPipeline.exe', host)
            archive.writestr('resources/payload-manifest.json', json.dumps(self.manifest))
            if extra:
                archive.writestr(extra, b'unknown')
        name = 'NexusPipeline-v0.17.3-win-x64.zip'
        self.release = {'tag_name': 'v0.17.3', 'id': 5, 'draft': False, 'published_at': '2026-10-14T00:00:00Z',
                        'assets': [{'name': name, 'id': 6, 'state': 'uploaded', 'size': self.package.stat().st_size,
                                    'digest': 'sha256:' + hashlib.sha256(self.package.read_bytes()).hexdigest(),
                                    'browser_download_url': 'https://github.com/FlappiBakuse/NexusPipeline/releases/download/v0.17.3/' + name}]}

    def read(self, release=None):
        def reader(root, version):
            self.assertEqual(version, '0.17.3')
            self.assertEqual((root / 'NexusPipeline.exe').read_bytes(), b'host')
            self.assertEqual({file.relative_to(root).as_posix() for file in root.rglob('*') if file.is_file()},
                             {'NexusPipeline.exe', 'resources/payload-manifest.json'})
            return self.manifest
        return update_baseline.read(self.package, release or self.release, target_version='0.17.4', application_reader=reader)

    def test_freezes_release_and_verified_application_inventory(self):
        self.write(extra='plugins/LiveScreenshot/plugin.json')
        base, files = self.read()
        self.assertEqual((base['releaseId'], base['assetId']), (5, 6))
        self.assertEqual({file['path'] for file in files}, {'NexusPipeline.exe', 'resources/payload-manifest.json'})
        self.assertEqual(base['payloadManifestSha256'], next(file['sha256'] for file in files if file['path'].endswith('.json')))

    def test_rejects_unpublished_or_changed_official_asset(self):
        for field, value in (('draft', True), ('published_at', None), ('tag_name', 'v0.17.2')):
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.read({**self.release, field: value})
        for field, value in (('digest', 'sha256:' + '0' * 64), ('state', 'new'), ('id', 0), ('size', 1),
                             ('browser_download_url', 'https://example.com/payload.zip')):
            release = copy.deepcopy(self.release)
            release['assets'][0][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.read(release)

    def test_rejects_unknown_files_or_inconsistent_payload_even_with_matching_zip_digest(self):
        for name in ('config/user.json', 'resources/unowned', '../escape'):
            self.write(extra=name)
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.read()
        self.write(host=b'edit')
        with self.assertRaisesRegex(ValueError, 'file digest'):
            self.read()

    def test_assets_are_paginated_and_duplicate_identities_fail_closed(self):
        first = [{'id': index + 1, 'name': f'asset-{index}'} for index in range(100)]
        second = [{'id': 101, 'name': 'last'}]
        with patch.object(update_baseline.urllib.request, 'urlopen', side_effect=[
                io.BytesIO(json.dumps(first).encode()), io.BytesIO(json.dumps(second).encode())]) as fetch:
            self.assertEqual(update_baseline.release_assets('https://api.github.com/repos/FlappiBakuse/NexusPipeline/releases', 5), first + second)
            self.assertTrue(fetch.call_args.args[0].full_url.endswith('/5/assets?per_page=100&page=2'))
        with patch.object(update_baseline.urllib.request, 'urlopen', side_effect=[
                io.BytesIO(json.dumps(first).encode()), io.BytesIO(json.dumps([first[0]]).encode())]):
            with self.assertRaisesRegex(ValueError, 'collision'):
                update_baseline.release_assets('https://api.github.com/repos/FlappiBakuse/NexusPipeline/releases', 5)


if __name__ == '__main__':
    unittest.main()
