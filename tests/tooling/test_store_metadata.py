from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest

from store_release_metadata import release_metadata


class StoreMetadataTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='store-metadata-')
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / 'catalog.json'
        self.metadata = dict(locales={'en-US': {'displayName': 'Alpha'}}, createdAt=None, updatedAt=None)
        self.manifest = dict(extensionId='alpha', artifactName='Alpha', packageVersion='0.5.0', metadata=self.metadata)
        self.package = dict(artifact=dict(sha256='a' * 64, sizeBytes=20), manifestSha256='b' * 64)
        self.entry = dict(name='alpha', artifactName='Alpha', version='0.5.0', sha256='a' * 64,
                          sizeBytes=20, manifestSha256='b' * 64, metadata=deepcopy(self.metadata))

    def read(self):
        self.path.write_text(json.dumps(dict(schemaVersion=2, repository='FlappiBakuse/NexusPipeline-Plugins',
                                            plugins=[self.entry])), encoding='utf-8')
        return release_metadata(self.path, [(self.package, self.manifest, None)], 'production')

    def test_applies_dates(self):
        self.entry['metadata'].update(createdAt='2026-10-01T00:00:00Z', updatedAt='2026-11-05T00:00:00Z')
        result = self.read()['alpha']
        self.assertEqual(self.entry['metadata'], result)
        self.assertIsNone(self.metadata['createdAt'])

    def test_preserves_dates(self):
        result = self.read()['alpha']
        self.assertIsNone(result['createdAt'])
        self.assertIsNone(result['updatedAt'])

    def test_binds_package(self):
        for key, value in [('sha256', 'c' * 64), ('sizeBytes', 21), ('version', '0.5.1'), ('artifactName', 'Beta')]:
            original = deepcopy(self.entry)
            self.entry[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.read()
            self.entry = original

    def test_binds_metadata(self):
        self.entry['metadata']['locales']['en-US']['displayName'] = 'Foreign'
        with self.assertRaises(ValueError):
            self.read()

    def test_requires_catalog(self):
        with self.assertRaises(ValueError):
            release_metadata(None, [], 'production')
        self.assertEqual({}, release_metadata(None, [], 'test'))
