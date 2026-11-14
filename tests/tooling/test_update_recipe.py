from copy import deepcopy
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tooling'))
from application_manifest import ASAR, ENTRY, HELPER, HOST, MANIFEST, canonical, create_manifest, kind
from product_assets import transport, write_archive
from update_recipe import CAPABILITIES, GUIDES, RECIPE, create, encode, parse, validate


class RecipeTests(unittest.TestCase):
    def setUp(self):
        self.scope = tempfile.TemporaryDirectory(prefix='recipe-', dir=os.environ['NEXUS_TEST_ROOT'])
        self.root = Path(self.scope.name)
        self.files = {}
        for name in (HOST, HELPER, 'README.md', ENTRY, ASAR, 'resources/desktop/obsolete.bin'):
            self.write(name, name.encode())
        self.application = self.manifest('0.18.0')

    def tearDown(self):
        self.scope.cleanup()

    def write(self, name, content):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        self.files[name] = dict(path=name, sizeBytes=len(content), sha256=hashlib.sha256(content).hexdigest(), kind=kind(name))

    def manifest(self, version):
        return dict(schemaVersion=2, installationGeneration='g0180', productVersion=version,
            buildId='a' * 64, frontendHash='b' * 64, files=sorted(self.files.values(), key=lambda item:item['path']))

    def test_writes_complete(self):
        raw = canonical(self.application)
        (self.root / MANIFEST).write_bytes(raw)
        bundle_name = 'package-info/initial-extensions/EmulatorSupport-0.5.0.zip'
        path = self.root / bundle_name
        path.parent.mkdir(parents=True)
        path.write_bytes(b'controlled bundle')
        bundled = [dict(path=bundle_name, sizeBytes=path.stat().st_size, sha256=hashlib.sha256(path.read_bytes()).hexdigest())]
        for package_kind in ('portable', 'update'):
            selected = bundled if package_kind == 'portable' else []
            files = transport(self.root, self.application, raw, package_kind, selected)
            archive_path = self.root / (package_kind + '.zip')
            write_archive(archive_path, files)
            with zipfile.ZipFile(archive_path) as archive:
                recipe = parse(archive.read(RECIPE))
                expected = {item['path'] for item in recipe['includedFiles'] + recipe['bundledFiles']} | {RECIPE, *GUIDES}
                self.assertEqual(expected, set(archive.namelist()))
                for item in recipe['includedFiles'] + recipe['bundledFiles']:
                    value = archive.read(item['path'])
                    self.assertEqual((item['sizeBytes'], item['sha256']), (len(value), hashlib.sha256(value).hexdigest()))
                self.assertEqual(package_kind == 'portable', bundle_name in archive.namelist())
                self.assertFalse(recipe['manualCover']['allowed'])

    def test_pins_delta(self):
        raw = canonical(self.application)
        previous = create(self.application, raw, 'update')['includedFiles']
        base = dict(releaseTag='v0.18.0', releaseId=123, assetId=456, assetSha256='c' * 64,
            payloadManifestSha256=hashlib.sha256(raw).hexdigest())
        self.write('README.md', b'updated guide')
        self.files.pop('resources/desktop/obsolete.bin')
        self.write('resources/desktop/new.bin', b'new resource')
        target = self.manifest('0.18.1')
        recipe = create(target, canonical(target), 'update', base, previous)
        self.assertEqual(base, recipe['base'])
        self.assertEqual({'README.md', MANIFEST, 'resources/desktop/new.bin'}, {item['path'] for item in recipe['includedFiles']})
        self.assertEqual({'resources/desktop/obsolete.bin'}, {item['path'] for item in recipe['removedFiles']})
        self.assertEqual({HOST, HELPER, ENTRY, ASAR}, {item['path'] for item in recipe['reusedFiles']})
        self.assertFalse(recipe['manualCover']['allowed'])
        self.assertEqual(recipe, parse(encode(recipe)))

    def test_rejects_unbound(self):
        recipe = create(self.application, canonical(self.application), 'update')
        faults = []
        changed = deepcopy(recipe); changed['manualCover']['allowed'] = True; faults.append(changed)
        changed = deepcopy(recipe); changed['reusedFiles'] = [deepcopy(changed['includedFiles'][0])]; faults.append(changed)
        changed = deepcopy(recipe); changed['includedFiles'][0]['path'] = '../NexusPipeline.exe'; faults.append(changed)
        changed = deepcopy(recipe); changed['includedFiles'][0]['path'] = 'runtime/state.db'; faults.append(changed)
        changed = deepcopy(recipe); changed['includedFiles'][0]['sizeBytes'] = True; faults.append(changed)
        changed = deepcopy(recipe); changed['requirements']['requiredCapabilities'] = CAPABILITIES[::-1]; faults.append(changed)
        changed = deepcopy(recipe); changed['bundledFiles'] = [dict(path='plugins/Other/Other.zip', sizeBytes=1, sha256='d' * 64)]; faults.append(changed)
        for value in faults:
            with self.subTest(value=value), self.assertRaises(ValueError):
                validate(value)
        with self.assertRaises(ValueError):
            parse(b'{"schemaVersion":1,"schemaVersion":1}')
        with self.assertRaises(ValueError):
            create(self.application, canonical(self.application), 'update', dict(releaseTag='v0.17.3'), recipe['includedFiles'])

    def test_requires_source(self):
        source_root = self.root / 'source'
        readme = source_root / 'tooling/application-readme.md'
        readme.parent.mkdir(parents=True)
        readme.write_bytes(b'bound application guide')
        lock = source_root / 'tooling/desktop-tools/package-lock.json'
        lock.parent.mkdir()
        lock.write_bytes(b'{}')
        source_manifest = source_root / 'manifest.json'
        source_manifest.write_bytes(canonical(dict(files=[dict(path='tooling/application-readme.md',
            sizeBytes=readme.stat().st_size, sha256=hashlib.sha256(readme.read_bytes()).hexdigest())])))
        source = dict(sourceCommit=None, sourceTree=None, sourceMode='local-uncommitted', sourceManifestPath=str(source_manifest),
            sourceSnapshotHash=hashlib.sha256(source_manifest.read_bytes()).hexdigest())
        published = {project: [dict(path='NexusPipeline.' + project + '.exe', sizeBytes=1, sha256=letter * 64)]
            for project, letter in (('Host', 'a'), ('Launcher', 'f'), ('Maintenance', 'b'))}
        desktop_files = [dict(path=name, sizeBytes=1, sha256='c' * 64) for name in
            (ENTRY[18:], ASAR[18:], 'locales/en-US.pak', 'locales/zh-CN.pak')]
        desktop = dict(files=desktop_files, runtime=dict(version='44.9.0', sha256='d' * 64),
            runtimeIdentity=dict(runtimeProfileId='win-x64-en-zh-v1', runtimeProfileSha256='e' * 64, runtimeInventorySha256='f' * 64))
        value = create_manifest(source_root, source, 'test', published, desktop, 'a' * 64)
        self.assertIsNone(value['sourceSha']); self.assertIsNone(value['sourceTreeSha'])
        self.assertEqual('desktop-runtime', next(item['kind'] for item in value['files']
            if item['path'] == 'resources/desktop/NexusPipeline.Launcher.exe'))
        changed = deepcopy(published)
        changed['Launcher'][0]['sha256'] = '9' * 64
        replacement = create_manifest(source_root, source, 'test', changed, desktop, 'a' * 64)
        self.assertNotEqual(value['buildId'], replacement['buildId'])
        self.assertNotEqual(value['desktop']['runtimeInventorySha256'], replacement['desktop']['runtimeInventorySha256'])
        self.assertEqual(value['desktop']['electronArchiveSha256'], replacement['desktop']['electronArchiveSha256'])
        with self.assertRaisesRegex(ValueError, 'exact clean Git source'):
            create_manifest(source_root, source, 'production', published, desktop, 'a' * 64)
