from copy import deepcopy
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tooling'))
from binary_inputs import hash_file
from installer_runtime import LOCK, FRAMEWORKS, read_dependencies, required_frameworks, render_runtime
from product_assets import COMPILER_SHA256
from installer_fixture import application, bundle, native_probe
from installer_flow import native_flow


class RuntimeTests(unittest.TestCase):
    def setUp(self):
        self.scope = tempfile.TemporaryDirectory(prefix='runtime-', dir=os.environ['NEXUS_TEST_ROOT'])
        self.work = Path(self.scope.name)
        self.root = Path(__file__).resolve().parents[2]
        self.software = self.work / 'application'
        application(self.software)

    def tearDown(self):
        self.scope.cleanup()

    def test_binds_requirements(self):
        required = required_frameworks(self.software)
        dependencies = read_dependencies(self.root)
        self.assertEqual({name: '10.0.0' for name in FRAMEWORKS}, required)
        self.assertEqual(set(FRAMEWORKS), set(dependencies))
        self.assertTrue(all(item['minimumVersion'] == required[name] for name, item in dependencies.items()))
        rendered = render_runtime(self.root, self.software)
        self.assertNotIn('@@', rendered)
        self.assertTrue(rendered)
        worker = self.work / 'NexusPipeline.Installer.exe'
        worker.write_bytes(bundle('Installer', dict(framework=dict(name=FRAMEWORKS[0], version='10.0.0'))))
        self.assertEqual(required, required_frameworks(self.software, worker))
        self.assertTrue(render_runtime(self.root, self.software, worker))

    def test_rejects_lock(self):
        original = json.loads((self.root / LOCK).read_bytes())
        altered = self.work / 'source'
        (altered / 'tooling').mkdir(parents=True)
        for field, value in (('framework', 'Microsoft.WindowsDesktop.App'), ('rid', 'win-arm64'), ('url', 'https://other.test/runtime.exe'),
                ('sha256', '0'), ('sha512', '0'), ('sizeBytes', True), ('minimumVersion', '10.0.13')):
            document = deepcopy(original); document['dependencies'][0][field] = value
            (altered / LOCK).write_text(json.dumps(document), encoding='utf-8')
            with self.subTest(field=field), self.assertRaises(ValueError):
                read_dependencies(altered)

    def test_rejects_programs(self):
        for fault in ('self-contained', 'conflicting', 'unknown'):
            software = self.work / fault
            application(software, fault=fault)
            with self.subTest(fault=fault), self.assertRaises(ValueError):
                required_frameworks(software)
        changed = self.work / 'different-minimum'
        application(changed, version='10.0.13')
        with self.assertRaisesRegex(ValueError, 'actual program'):
            render_runtime(self.root, changed)
        for project, program in (('Installer', self.work / 'NexusPipeline.Installer.exe'),
                                 ('Launcher', self.software / 'resources/desktop/NexusPipeline.Launcher.exe')):
            for value in (dict(framework=dict(name=FRAMEWORKS[0], version='10.0.1')),
                          dict(framework=dict(name='Microsoft.WindowsDesktop.App', version='10.0.0')),
                          dict(includedFrameworks=[dict(name=FRAMEWORKS[0], version='10.0.0')])):
                program.write_bytes(bundle(project, value))
                with self.subTest(project=project, frameworks=value), self.assertRaises(ValueError):
                    render_runtime(self.root, self.software, program if project == 'Installer' else None)

    def test_native_runtime(self):
        compiler = Path(os.environ['NEXUS_INSTALLER_COMPILER'])
        self.assertEqual(COMPILER_SHA256, hash_file(compiler))
        receipt = json.loads(Path(os.environ['NEXUS_INSTALLER_RUNTIME_RECEIPT']).read_bytes())
        self.assertEqual('PASS_OFFICIAL_RUNTIME_HASHES', receipt['status'])
        self.assertEqual(2, len(receipt['dependencies']))
        for item in receipt['dependencies']:
            self.assertEqual(item['sha256'], hash_file(Path(item['path'])))
        retained = Path(tempfile.mkdtemp(prefix='runtime-native-', dir=os.environ['NEXUS_TEST_ROOT']))
        result = native_probe(self.root, retained, self.software, compiler, receipt['dependencies'])
        self.assertIn('detected=2', result)
        self.assertIn('missingSilent=rejected', result)
        self.assertIn('officialSignatures=2', result)

    def test_runtime_flow(self):
        compiler = Path(os.environ['NEXUS_INSTALLER_COMPILER'])
        self.assertEqual(COMPILER_SHA256, hash_file(compiler))
        retained = Path(tempfile.mkdtemp(prefix='runtime-flow-', dir=os.environ['NEXUS_TEST_ROOT']))
        result = native_flow(self.root, retained, self.software, compiler)
        self.assertIn('controlledCases=13', result)
