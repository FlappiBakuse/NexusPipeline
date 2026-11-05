import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tooling'))
from binary_inputs import hash_file
from product_assembly import assemble_product
from product_publish import inventory
from product_assets import write_archive, prepare_output
from artifact_lock import artifact_lock
from source_snapshot import write_json


class ProductAssemblyTests(unittest.TestCase):
    def setUp(self):
        self.scope = tempfile.TemporaryDirectory(prefix='assembly-', dir=os.environ['NEXUS_TEST_ROOT'])
        self.root = Path(self.scope.name)
        self.workspace = self.root / 'published'
        self.output = self.root / 'assembled'
        components = {}
        for project in ('Host', 'Maintenance'):
            directory = self.workspace / 'publish' / project.lower()
            directory.mkdir(parents=True)
            (directory / ('NexusPipeline.' + project + '.exe')).write_bytes(project.encode())
            components[project] = inventory(directory)
        desktop = self.workspace / 'desktop'
        desktop.mkdir()
        (desktop / 'NexusPipeline.Desktop.exe').write_bytes(b'desktop')
        write_json(desktop / 'desktop-bundle.json', dict(status='PASS', files=inventory(desktop)))
        source = self.workspace / 'source'
        (source / 'tooling').mkdir(parents=True)
        (source / 'tooling/application-readme.md').write_bytes(b'Application guide')
        source_manifest = source / 'source-manifest.json'
        write_json(source_manifest, dict(files=inventory(source)))
        manifest = self.workspace / 'payload-manifest.json'
        manifest.write_bytes(b'{}')
        self.receipt = self.workspace / 'publish.json'
        write_json(self.receipt, dict(status='PASS_PUBLISHED_NOT_ASSEMBLED', profile='test',
            source=dict(sourceManifestPath=str(source_manifest), sourceSnapshotHash=hash_file(source_manifest), exportRoot=str(source)),
            applicationManifest=dict(path=str(manifest), sha256=hash_file(manifest)),
            published=components, desktopReceiptSha256=hash_file(desktop / 'desktop-bundle.json')))
        write_json(self.workspace / 'publish-current.json', dict(receipt=str(self.receipt), sha256=hash_file(self.receipt)))

    def tearDown(self):
        self.scope.cleanup()

    def test_excludes_producers(self):
        with artifact_lock(self.output):
            with self.assertRaisesRegex(ValueError, 'Another producer'):
                with artifact_lock(self.output):
                    self.fail('Concurrent producer admitted')
        with artifact_lock(self.output):
            self.assertFalse(self.output.exists())

    def test_writes_zip(self):
        original = self.root / 'original.bin'
        original.write_bytes(bytes(range(256)) * 400)
        path = self.root / 'payload.zip'
        write_archive(path, {'application/original.bin': original, 'manifest.json': b'{}'})
        with zipfile.ZipFile(path) as archive:
            self.assertTrue(all(item.extract_version <= 20 and item.extra == b'' for item in archive.infolist()))
            self.assertEqual(original.read_bytes(), archive.read('application/original.bin'))
            self.assertEqual(b'{}', archive.read('manifest.json'))
        second = self.root / 'repeated.zip'
        write_archive(second, {'application/original.bin': original, 'manifest.json': b'{}'})
        self.assertEqual(path.read_bytes(), second.read_bytes())

    def test_preserves_mutation(self):
        self.output.mkdir()
        artifact = self.output / 'Setup.exe'
        artifact.write_bytes(b'original')
        write_json(self.output / 'assets-receipt.json', dict(status='PASS_THREE_ASSETS_BUILT_NOT_QUALIFIED',
            assets=[dict(name=artifact.name, sha256=hash_file(artifact))]))
        artifact.write_bytes(b'unknown')
        with self.assertRaisesRegex(ValueError, 'Previous candidate bytes changed'):
            prepare_output(Path(__file__).resolve().parents[2], self.output)
        self.assertEqual(b'unknown', artifact.read_bytes())

    def test_requires_single(self):
        assemble_product(self.workspace, self.output)
        self.assertEqual(b'Host', (self.output / 'NexusPipeline.exe').read_bytes())
        self.assertEqual(b'Maintenance', (self.output / 'NexusPipeline.update.exe').read_bytes())
        runtime = self.output / 'runtime'
        runtime.mkdir()
        (runtime / 'user-data').write_bytes(b'preserve')
        assemble_product(self.workspace, self.output)
        self.assertEqual(b'preserve', (runtime / 'user-data').read_bytes())
        host = self.workspace / 'publish/host'
        (host / 'shared.dll').write_bytes(b'unowned sidecar')
        self.refresh_host(host)
        with self.assertRaisesRegex(ValueError, 'single-file entry'):
            assemble_product(self.workspace, self.output, replace_application=True)
        self.assertEqual(b'Host', (self.output / 'NexusPipeline.exe').read_bytes())

    def test_rejects_mutation(self):
        assemble_product(self.workspace, self.output)
        changed = self.output / 'resources/desktop/NexusPipeline.Desktop.exe'
        changed.write_bytes(b'changed')
        for replace in (False, True):
            with self.assertRaisesRegex(ValueError, 'changed or unowned'):
                assemble_product(self.workspace, self.output, replace_application=replace)
        self.assertEqual(b'changed', changed.read_bytes())

    def test_replaces_payload(self):
        desktop = self.workspace / 'desktop'
        old = desktop / 'old/dependency.bin'
        old.parent.mkdir()
        old.write_bytes(b'old runtime resource')
        self.refresh_desktop(desktop)
        assemble_product(self.workspace, self.output)
        runtime = self.output / 'runtime'
        runtime.mkdir()
        (runtime / 'user-data').write_bytes(b'preserve')
        host = self.workspace / 'publish/host'
        (host / 'NexusPipeline.Host.exe').write_bytes(b'new host')
        self.refresh_host(host)
        old.unlink(); old.parent.rmdir()
        self.refresh_desktop(desktop)
        unowned = self.output / 'notes.txt'
        unowned.write_bytes(b'Preserve external material')
        assemble_product(self.workspace, self.output, replace_application=True)
        self.assertEqual(b'new host', (self.output / 'NexusPipeline.exe').read_bytes())
        self.assertEqual(b'preserve', (runtime / 'user-data').read_bytes())
        self.assertEqual(b'Preserve external material', unowned.read_bytes())
        self.assertFalse((self.output / 'resources/desktop/old').exists())

    def refresh_desktop(self, desktop):
        write_json(desktop / 'desktop-bundle.json', dict(status='PASS', files=[item for item in inventory(desktop)
            if item['path'] != 'desktop-bundle.json']))
        receipt = json.loads(self.receipt.read_bytes())
        receipt['desktopReceiptSha256'] = hash_file(desktop / 'desktop-bundle.json')
        write_json(self.receipt, receipt)
        write_json(self.workspace / 'publish-current.json', dict(receipt=str(self.receipt), sha256=hash_file(self.receipt)))

    def refresh_host(self, host):
        receipt = json.loads(self.receipt.read_bytes())
        receipt['published']['Host'] = inventory(host)
        write_json(self.receipt, receipt)
        write_json(self.workspace / 'publish-current.json', dict(receipt=str(self.receipt), sha256=hash_file(self.receipt)))

    def test_binds_desktop(self):
        desktop = self.workspace / 'desktop'
        (desktop / 'NexusPipeline.Desktop.exe').write_bytes(b'replaced')
        write_json(desktop / 'desktop-bundle.json', dict(status='PASS', files=[item for item in inventory(desktop)
            if item['path'] != 'desktop-bundle.json']))
        with self.assertRaisesRegex(ValueError, 'ownership receipt changed'):
            assemble_product(self.workspace, self.output)
        self.assertFalse(self.output.exists())
