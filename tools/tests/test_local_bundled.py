import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from local_incremental import stage_local_bundled


class LocalBundledTests(unittest.TestCase):
    def fixture(self, root):
        host = root/'host'
        policy = host/'src/Modules/Plugins/Repository/BundledPlugins.json'
        policy.parent.mkdir(parents=True)
        artifacts = ('EmulatorSupport', 'LiveScreenshot')
        policy.write_text(json.dumps({'schemaVersion': 1, 'repository': 'FlappiBakuse/NexusPipeline-Plugins',
                                     'plugins': [{'artifactName': name} for name in artifacts]}))
        reports = []
        for name in artifacts:
            package = root/(name+'.zip')
            with zipfile.ZipFile(package, 'w') as archive:
                archive.writestr('plugin.json', json.dumps({'name': name, 'artifactName': name, 'version': '1.2.3',
                    'kind': 'managed-code', 'apiVersion': '2.3', 'minHostVersion': '0.17.6'}))
                archive.writestr('module.dll', b'current-source-build')
            reports.append({'artifactName': name, 'version': '1.2.3', 'status': 'PASS', 'package': str(package),
                            'sha256': hashlib.sha256(package.read_bytes()).hexdigest(), 'sizeBytes': package.stat().st_size})
        return host, reports

    def test_current_builds_supply_exact_bundled_identity_without_stable_catalog(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); host, reports = self.fixture(root)
            result = stage_local_bundled(root/'software', host, reports)
            self.assertEqual(len(result['plugins']), 2)
            for plugin in result['plugins']:
                self.assertEqual(plugin['apiVersion'], '2.3')
                for file in plugin['files']:
                    data = (root/'software/plugins'/plugin['artifactName']/file['path']).read_bytes()
                    self.assertEqual(hashlib.sha256(data).hexdigest(), file['sha256'])

    def test_changed_package_and_missing_build_are_rejected(self):
        for mutation in ('bytes', 'missing'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); host, reports = self.fixture(root)
                if mutation == 'bytes': Path(reports[0]['package']).write_bytes(b'changed')
                else: reports.pop(0)
                with self.assertRaises(ValueError): stage_local_bundled(root/'software', host, reports)


if __name__ == '__main__': unittest.main()
