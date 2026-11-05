import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tooling'))
from build_inputs import solution_inputs, web_inputs
from frozen_build import capture_build, compile_receipt, reuse_build
from source_snapshot import digest, write_json, export_source
from build_product import build_environment, update_export
from web_artifacts import artifact_records, reuse_web


class BuildInputTests(unittest.TestCase):
    def setUp(self):
        self.scope = tempfile.TemporaryDirectory(prefix='build-inputs-', dir=os.environ['NEXUS_TEST_ROOT'])
        self.root = Path(self.scope.name) / 'repository'
        self.root.mkdir()
        subprocess.run(['git', 'init', '--initial-branch=develop', str(self.root)], check=True, capture_output=True)
        values = {'NexusPipeline.slnx': '<Solution><Project Path="app/App.csproj" /></Solution>',
            'Directory.Build.props': '<Project />', 'Directory.Build.targets': '<Project />',
            'NuGet.Config': '<configuration />', 'global.json': '{}',
            'app/App.csproj': '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../lib/Lib.csproj" />'
                '<Compile Include="../shared/*.cs" /><EmbeddedResource Include="../assets/*.json" /></ItemGroup></Project>',
            'app/App.cs': 'class App {}', 'lib/Lib.csproj': '<Project Sdk="Microsoft.NET.Sdk" />',
            'lib/Lib.cs': 'class Lib {}', 'shared/Shared.cs': 'class Shared {}', 'assets/input.json': '{}',
            'tooling/build_product.py': 'pass\n', 'tooling/web_build.py': 'pass\n',
            'tooling/frontend_names.mjs': 'export {};', 'tooling/frontend_boundaries.mjs': 'export {};',
            'tooling/frontend-exceptions.json': '[]',
            'tooling/generated_sources.json': '{}', 'apps/web/package.json': '{}',
            'apps/web/package-lock.json': '{}', 'apps/web/src/main.ts': 'export {};',
            'sdk/frontend/src/index.ts': 'export {};'}
        for name, text in values.items():
            self.write(name, text)
        self.environment = {'PATH': os.environ.get('PATH', os.environ.get('Path', ''))}

    def tearDown(self):
        self.scope.cleanup()

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')

    def inputs(self):
        return solution_inputs(self.root, 'test', self.environment)

    def test_ignores_document(self):
        before = self.inputs()
        self.write('README.md', 'Changed project documentation')
        self.write('docs/guide.md', 'Changed guide')
        self.assertEqual(before, self.inputs())

    def test_tracks_transitive(self):
        current = self.inputs()
        for name in ['lib/Lib.cs', 'shared/New.cs', 'assets/input.json', 'lib/packages.lock.json']:
            self.write(name, '// changed')
            following = self.inputs()
            self.assertNotEqual(current['sha256'], following['sha256'], name)
            current = following

    def test_scopes_projects(self):
        self.write('other/Other.csproj', '<Project Sdk="Microsoft.NET.Sdk" />')
        self.write('other/Other.cs', 'class Other {}')
        self.write('NexusPipeline.slnx', '<Solution><Project Path="app/App.csproj" /><Project Path="other/Other.csproj" /></Solution>')
        before = solution_inputs(self.root, 'test', self.environment, ['App'])
        aggregate = self.inputs()
        self.write('other/Other.cs', 'class Other { int changed; }')
        self.assertNotEqual(aggregate, self.inputs())
        self.assertEqual(before, solution_inputs(self.root, 'test', self.environment, ['App']))
        self.write('lib/Lib.cs', 'class Lib { int changed; }')
        self.assertNotEqual(before, solution_inputs(self.root, 'test', self.environment, ['App']))

    def test_tracks_environment(self):
        before = self.inputs()
        self.environment['MSBuildSDKsPath'] = 'changed-sdk-root'
        self.assertNotEqual(before['sha256'], self.inputs()['sha256'])

    def test_tracks_properties(self):
        before = self.inputs()
        self.write('.editorconfig', 'root = true\n[*.cs]\ndotnet_diagnostic.CS0168.severity = error\n')
        self.assertNotEqual(before['sha256'], self.inputs()['sha256'])
        self.write('Directory.Packages.props', '<Project><ItemGroup><PackageVersion Include="Example" Version="1" /></ItemGroup></Project>')
        self.assertTrue(self.inputs()['conservativeReasons'])

    def test_refuses_unknown(self):
        self.write('app/App.csproj', '<Project><Import Project="unknown.props" /></Project>')
        value = self.inputs()
        self.assertTrue(value['conservativeReasons'])
        self.assertIsNone(compile_receipt(Path(self.scope.name), value, 'test'))
        exported = export_source(self.root, self.root.parent / 'exported')
        frozen = Path(exported['exportRoot'])
        records = json.loads(Path(exported['sourceManifestPath']).read_bytes())['files']
        actual = solution_inputs(frozen, 'test', self.environment, source_records=records)
        self.assertEqual(value['files'], actual['files'])
        self.assertEqual(value['conservativeReasons'], actual['conservativeReasons'])
        (frozen / 'shared/Shared.cs').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'Frozen build source bytes changed'):
            solution_inputs(frozen, 'test', self.environment, source_records=records)
        (frozen / 'extra.cs').write_bytes(b'unowned')
        with self.assertRaisesRegex(ValueError, 'Frozen build source file set changed'):
            solution_inputs(frozen, 'test', self.environment, source_records=records)

    def test_tracks_browser(self):
        before = web_inputs(self.root, self.environment)
        self.write('apps/web/README.md', 'No browser input')
        self.assertEqual(before, web_inputs(self.root, self.environment))
        for name in ['apps/web/package-lock.json', 'apps/web/src/asset.svg', 'sdk/frontend/src/index.ts']:
            self.write(name, '// changed')
            following = web_inputs(self.root, self.environment)
            self.assertNotEqual(before['sha256'], following['sha256'])
            before = following

    def test_scopes_environment(self):
        before = web_inputs(self.root, self.environment)
        self.environment.update(DOTNET_CLI_HOME='other-home', MSBuildSDKsPath='other-sdk', NUGET_PACKAGES='other-cache')
        self.assertEqual(before, web_inputs(self.root, self.environment))
        for name in ('NODE_OPTIONS', 'VITE_API_URL', 'BROWSERSLIST_ENV'):
            self.environment[name] = 'changed'
            after = web_inputs(self.root, self.environment)
            self.assertNotEqual(before, after)
            before = after

    def test_checks_binaries(self):
        output = Path(self.scope.name) / 'build'
        binary = output / 'build/bin/App/release/App.dll'
        binary.parent.mkdir(parents=True)
        binary.write_bytes(b'compiled-output')
        original = output / 'runs/original/build-receipt.json'
        manifest = original.parent / 'build-binaries.json'
        write_json(manifest, capture_build(output))
        component = self.inputs()
        receipt = {'status': 'PASS', 'profile': 'test', 'runId': 'original', 'componentInputs': component,
            'binaryManifest': {'path': str(manifest), 'sha256': digest(manifest.read_bytes())}}
        write_json(original, receipt)
        write_json(output / 'build-receipt.json', receipt)
        reference = compile_receipt(output, component, 'test')
        self.assertEqual(digest(original.read_bytes()), reference['sha256'])
        preserved = original.read_bytes()
        binary.write_bytes(b'tampered-output')
        with self.assertRaises(ValueError):
            compile_receipt(output, component, 'test')
        self.assertEqual(preserved, original.read_bytes())

    def test_checks_browser(self):
        output = Path(self.scope.name) / 'web'
        dist = output / 'dist'
        dist.mkdir(parents=True)
        (dist / 'main.js').write_bytes(b'browser-output')
        original = output / 'runs/original/compiled.json'
        component = web_inputs(self.root, self.environment)
        receipt = {'status': 'PASS', 'componentInputs': component, 'output': str(dist), 'artifacts': artifact_records(dist)}
        write_json(original, receipt)
        receipt.update(originalReceipt=str(original), originalReceiptSha256=digest(original.read_bytes()))
        write_json(output / 'web-receipt.json', receipt)
        self.assertIsNotNone(reuse_web(output, component))
        (dist / 'extra.js').write_bytes(b'unowned-output')
        with self.assertRaises(ValueError):
            reuse_web(output, component)

    def test_reuses_current(self):
        output = Path(self.scope.name) / 'slot'
        source = export_source(self.root, output)
        binary = output / 'build/bin/App/release/App.dll'
        binary.parent.mkdir(parents=True)
        binary.write_bytes(b'compiled-output')
        env = build_environment(output)
        component = solution_inputs(self.root, 'test', env)
        component.update(webInputs=web_inputs(self.root, env)['sha256'], webArtifacts=[])
        original = output / 'runs/original/build-receipt.json'
        manifest = original.parent / 'build-binaries.json'
        write_json(manifest, capture_build(output))
        receipt = {'status': 'PASS', 'profile': 'test', 'runId': 'original', 'source': source,
            'componentInputs': component, 'binaryManifest': {'path': str(manifest), 'sha256': digest(manifest.read_bytes())}}
        write_json(original, receipt)
        write_json(output / 'build-receipt.json', receipt)
        evidence = original.read_bytes()
        self.assertTrue(reuse_build(self.root, output))
        self.write('README.md', 'Unrelated documentation update')
        self.assertTrue(reuse_build(self.root, output))
        current = update_export(self.root, output)
        self.assertNotEqual(source['sourceSnapshotHash'], current['sourceSnapshotHash'])
        self.assertTrue(reuse_build(self.root, output))
        self.assertEqual(evidence, original.read_bytes())
        (Path(current['exportRoot']) / 'app/App.cs').write_bytes(b'unowned-source-change')
        with self.assertRaises(ValueError):
            reuse_build(self.root, output)


if __name__ == '__main__':
    unittest.main()
