from __future__ import annotations

import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

from tools import update_recipe as recipe


class UpdateRecipeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='nxp-recipe-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.production = self.root / 'production'
        self.production.mkdir()
        self.files = {'NexusPipeline.exe': b'new host', 'NexusPipeline.update.exe': b'dedicated updater',
                      'README.md': b'guide', 'resources/desktop/runtime.dll': b'unchanged runtime',
                      recipe.ENTRY: b'desktop entry', recipe.NATIVE: b'native binding',
                      recipe.ASAR: b'new desktop app'}
        for name, raw in self.files.items():
            path = self.production / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(raw)
        self.application = {'schemaVersion': 2, 'product': 'NexusPipeline', 'productVersion': '0.17.4',
                            'rid': 'win-x64', 'installationGeneration': 'g0170', 'buildId': 'a' * 64,
                            'frontendHash': 'b' * 64, 'files': [self.entry(name, raw) for name, raw in self.files.items()]}
        self.manifest = json.dumps(self.application).encode()
        (self.production / recipe.MANIFEST_PATH).write_bytes(self.manifest)
        self.base_files = [self.entry(name, b'old host' if name == 'NexusPipeline.exe' else b'old dedicated updater' if name == 'NexusPipeline.update.exe' else raw)
                           for name, raw in self.files.items()]
        self.base_files.append(self.entry(recipe.MANIFEST_PATH, b'old manifest'))
        self.base = {'releaseTag': 'v0.17.3', 'releaseId': 42, 'assetId': 24, 'assetSha256': 'c' * 64,
                     'payloadManifestSha256': hashlib.sha256(b'old manifest').hexdigest()}
        self.guides = {name: b'Exit and back up your installation before copying application files.' for name in recipe.GUIDES}

    @staticmethod
    def entry(name, raw):
        return {'path': name, 'sizeBytes': len(raw), 'sha256': hashlib.sha256(raw).hexdigest()}

    def delta(self):
        return recipe.create(self.application, self.manifest, 'update', self.base, self.base_files)

    def test_exact_three_public_assets(self):
        self.assertEqual(recipe.asset_names('v0.17.4'), {'NexusPipeline-v0.17.4-win-x64-' + suffix
                                                        for suffix in ('Setup.exe', 'Portable.zip', 'Update.zip')})

    def test_unchanged_runtime_is_reused_and_reconstruction_is_exact(self):
        delta = self.delta()
        self.assertIn('resources/desktop/runtime.dll', {item['path'] for item in delta['reusedFiles']})
        self.assertTrue(delta['manualCover']['allowed'])
        legacy = [file for file in self.base_files if file['path'] != 'NexusPipeline.update.exe']
        self.assertFalse(recipe.create(self.application, self.manifest, 'update', self.base, legacy)['manualCover']['allowed'])
        package = self.root / 'Update.zip'
        recipe.generate(self.production, package, delta, self.guides)
        recipe.verify(package, delta)
        with zipfile.ZipFile(package) as archive:
            reconstructed = {item['path']: self.files[item['path']] for item in delta['reusedFiles']}
            reconstructed.update({item['path']: archive.read(item['path']) for item in delta['includedFiles']})
            self.assertNotIn('resources/desktop/runtime.dll', archive.namelist())
        self.assertEqual(reconstructed, {**self.files, recipe.MANIFEST_PATH: self.manifest})

    def test_full_fallback_and_portable_do_not_claim_manual_delta_eligibility(self):
        for kind in ('portable', 'update'):
            value = recipe.create(self.application, self.manifest, kind)
            self.assertFalse(value['manualCover']['allowed'])
            self.assertEqual(value['reusedFiles'], [])
            package = self.root / (kind + '.zip')
            recipe.generate(self.production, package, value, self.guides)
            recipe.verify(package, value)

    def test_removal_disables_manual_cover(self):
        self.base_files.append(self.entry('resources/desktop/obsolete.dll', b'old'))
        value = self.delta()
        self.assertFalse(value['manualCover']['allowed'])
        self.assertEqual(value['removedFiles'][0]['path'], 'resources/desktop/obsolete.dll')

    def test_portable_bundles_are_verified_without_becoming_application_files(self):
        name, raw = 'plugins/LiveScreenshot/plugin.json', b'plugin manifest'
        path = self.production / name
        path.parent.mkdir(parents=True)
        path.write_bytes(raw)
        value = recipe.create(self.application, self.manifest, 'portable', bundled_files=[self.entry(name, raw)])
        package = self.root / 'Portable.zip'
        recipe.generate(self.production, package, value, self.guides)
        recipe.verify(package, value)
        self.assertNotIn(name, {item['path'] for item in value['includedFiles']})
        value['packageKind'] = 'update'
        with self.assertRaisesRegex(ValueError, 'bundled files only'):
            recipe.validate(value)
        with self.assertRaisesRegex(ValueError, 'bundled path'):
            recipe.create(self.application, self.manifest, 'portable', bundled_files=[self.entry('plugins/Unknown/a', raw)])

    def test_changed_runtime_is_included(self):
        next(item for item in self.base_files if item['path'].endswith('runtime.dll'))['sha256'] = 'd' * 64
        self.assertIn('resources/desktop/runtime.dll', {item['path'] for item in self.delta()['includedFiles']})

    def test_older_or_non_forward_base_is_rejected(self):
        for tag in ('v0.17.2', 'v0.17.3-rc.1', 'v0.17.4', 'v0.18.0'):
            with self.subTest(tag=tag), self.assertRaisesRegex(ValueError, 'base version'):
                recipe.create(self.application, self.manifest, 'update', {**self.base, 'releaseTag': tag}, self.base_files)

    def test_rejects_unsafe_paths_collisions_and_overlap(self):
        for name in ('../escape', 'config/user.json', 'NexusPipeline Uninstall.exe', 'resources/desktop/CON',
                     'resources/desktop/x:stream', 'resources/desktop/X/../y'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                recipe.file_map([self.entry(name, b'x')])
        value = self.delta()
        value['includedFiles'].append(copy.deepcopy(value['reusedFiles'][0]))
        with self.assertRaises(ValueError):
            recipe.validate(value)
        with self.assertRaises(ValueError):
            recipe.file_map([self.entry('README.md', b'x'), self.entry('readme.md', b'x')])

    def test_rejects_duplicate_metadata_and_unknown_schema(self):
        raw = recipe.encode(self.delta())
        with self.assertRaises(ValueError):
            recipe.parse(raw.replace(b'"schemaVersion":1', b'"schemaVersion":1,"schemaVersion":1'))
        with self.assertRaises(ValueError):
            recipe.parse(raw.replace(b'"schemaVersion":1', b'"schemaVersion":3'))

    def test_input_mutation_is_rejected(self):
        value = self.delta()
        (self.production / 'NexusPipeline.exe').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'input changed'):
            recipe.generate(self.production, self.root / 'changed.zip', value, self.guides)

    def test_unknown_archive_file_is_rejected(self):
        value = self.delta()
        package = self.root / 'unknown.zip'
        recipe.generate(self.production, package, value, self.guides)
        with zipfile.ZipFile(package, 'a') as archive:
            archive.writestr('unknown.txt', b'unknown')
        with self.assertRaisesRegex(ValueError, 'file set'):
            recipe.verify(package, value)

    def test_file_directory_conflict_is_rejected(self):
        value = self.delta()
        value['includedFiles'].append(self.entry('resources/desktop/resources', b'file'))
        with self.assertRaisesRegex(ValueError, 'directory conflict'):
            recipe.validate(value)

    def test_manifest_cannot_claim_a_different_final_tree(self):
        value = self.delta()
        value['reusedFiles'][0]['sha256'] = 'e' * 64
        package = self.root / 'wrong-tree.zip'
        recipe.generate(self.production, package, value, self.guides)
        with self.assertRaisesRegex(ValueError, 'target tree'):
            recipe.verify(package, value)

    def test_deterministic_packages(self):
        value = self.delta()
        one, two = self.root / 'one.zip', self.root / 'two.zip'
        recipe.generate(self.production, one, value, self.guides)
        recipe.generate(self.production, two, value, self.guides)
        self.assertEqual(one.read_bytes(), two.read_bytes())


if __name__ == '__main__':
    unittest.main()
