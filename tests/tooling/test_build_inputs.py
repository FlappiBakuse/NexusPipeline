import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tooling'))
from build_inputs import project_inputs, solution_inputs, web_inputs
from frozen_build import reuse_build, save_build
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
            'apps/desktop/startup-content.css': 'html {}', 'apps/desktop/nexuspipeline.ico': 'icon fixture bytes',
            'sdk/frontend/src/index.ts': 'export {};'}
        for name, text in values.items():
            self.write(name, text)
        self.environment = {'PATH': os.environ.get('PATH', os.environ.get('Path', ''))}
        resolver = patch('build_inputs.resolve_sdk', return_value={'path': 'dotnet', 'version': '10.0.100'})
        self.sdk = resolver.start()
        self.addCleanup(resolver.stop)

    def tearDown(self):
        self.scope.cleanup()

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')

    def inputs(self):
        return solution_inputs(self.root, 'test', self.environment)

    def test_scopes_projects(self):
        self.write('other/Other.csproj', '<Project Sdk="Microsoft.NET.Sdk" />')
        self.write('other/Other.cs', 'class Other {}')
        self.write('NexusPipeline.slnx', '<Solution><Project Path="app/App.csproj" /><Project Path="other/Other.csproj" /></Solution>')
        output = self.root.parent / 'slot'
        selected = ['App']
        self.seed_build(output, selected)
        self.assertTrue(reuse_build(self.root, output))
        self.write('other/Other.cs', 'class Other { int changed; }')
        self.assertFalse(reuse_build(self.root, output))
        self.assertTrue(reuse_build(self.root, output, projects=selected))
        with patch.dict(os.environ, {'MSBuildSDKsPath': 'changed-sdk-root'}):
            self.assertFalse(reuse_build(self.root, output, projects=selected))
        changes = {
            'lib/Lib.cs': 'class Lib { int changed; }',
            'shared/New.cs': 'class New {}',
            'assets/input.json': '{"changed": true}',
            'lib/packages.lock.json': '{"version": 1}',
            '.editorconfig': 'root = true\n[*.cs]\ndotnet_diagnostic.CS0168.severity = error\n',
            'Directory.Packages.props': '<Project><ItemGroup><PackageVersion Include="Example" Version="1" /></ItemGroup></Project>',
        }
        for name, content in changes.items():
            path = self.root / name
            before = path.read_bytes() if path.exists() else None
            self.write(name, content)
            self.assertFalse(reuse_build(self.root, output, projects=selected), name)
            if before is None:
                path.unlink()
            else:
                path.write_bytes(before)
        self.assertTrue(reuse_build(self.root, output, projects=selected))

    def test_refuses_unknown(self):
        self.write('app/App.csproj', '<Project><Import Project="unknown.props" /></Project>')
        output = self.root.parent / 'slot'
        exported, _, _ = self.seed_build(output)
        self.assertFalse(reuse_build(self.root, output))
        value = self.inputs()
        frozen = Path(exported['exportRoot'])
        records = json.loads(Path(exported['sourceManifestPath']).read_bytes())['files']
        actual = solution_inputs(frozen, 'test', self.environment, source_records=records)
        self.assertEqual(value['sha256'], actual['sha256'])
        (frozen / 'shared/Shared.cs').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'Frozen build source bytes changed'):
            solution_inputs(frozen, 'test', self.environment, source_records=records)
        (frozen / 'extra.cs').write_bytes(b'unowned')
        with self.assertRaisesRegex(ValueError, 'Frozen build source file set changed'):
            solution_inputs(frozen, 'test', self.environment, source_records=records)

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

        def cached():
            return reuse_web(output, web_inputs(self.root, self.environment))

        self.write('apps/web/README.md', 'No browser input')
        self.environment.update(DOTNET_CLI_HOME='other-home', MSBuildSDKsPath='other-sdk', NUGET_PACKAGES='other-cache')
        self.assertIsNotNone(cached())
        for name in ('apps/web/package-lock.json', 'apps/web/src/asset.svg', 'sdk/frontend/src/index.ts',
                     'apps/desktop/startup-content.css', 'apps/desktop/nexuspipeline.ico'):
            path = self.root / name
            before = path.read_bytes() if path.exists() else None
            self.write(name, '// changed')
            self.assertIsNone(cached(), name)
            if before is None:
                path.unlink()
            else:
                path.write_bytes(before)
        for name in ('NODE_OPTIONS', 'VITE_API_URL', 'BROWSERSLIST_ENV'):
            with patch.dict(self.environment, {name: 'changed'}):
                self.assertIsNone(cached(), name)
        (dist / 'extra.js').write_bytes(b'unowned-output')
        with self.assertRaisesRegex(ValueError, 'artifact set or bytes changed'):
            cached()

    def seed_build(self, output, projects=None):
        source = export_source(self.root, output)
        binary = output / 'build/bin/App/release/App.dll'
        binary.parent.mkdir(parents=True)
        binary.write_bytes(b'compiled-output')
        env = build_environment(output)
        component = solution_inputs(self.root, 'test', env)
        component.update(webInputs=web_inputs(self.root, env)['sha256'], webArtifacts=[])
        original = output / 'runs/original/build-receipt.json'
        receipt = {'status': 'PASS', 'profile': 'test', 'runId': 'original', 'source': source,
            'componentInputs': component, 'binaryManifest': save_build(output, original.parent)}
        if projects:
            receipt['projectInputs'] = project_inputs(self.root, 'test', env, projects, component['webInputs'], [])
        write_json(original, receipt)
        write_json(output / 'build-receipt.json', receipt)
        return source, original, binary

    def test_reuses_current(self):
        output = self.root.parent / 'slot'
        source, original, binary = self.seed_build(output)
        evidence = original.read_bytes()
        self.assertTrue(reuse_build(self.root, output))
        self.write('README.md', 'Unrelated documentation update')
        self.assertTrue(reuse_build(self.root, output))
        current = update_export(self.root, output)
        self.assertNotEqual(source['sourceSnapshotHash'], current['sourceSnapshotHash'])
        self.assertTrue(reuse_build(self.root, output))
        binary_bytes = binary.read_bytes()
        binary.write_bytes(b'tampered-output')
        with self.assertRaisesRegex(ValueError, 'Frozen build artifacts changed'):
            reuse_build(self.root, output)
        binary.write_bytes(binary_bytes)
        (Path(current['exportRoot']) / 'app/App.cs').write_bytes(b'unowned-source-change')
        with self.assertRaisesRegex(ValueError, 'Frozen source export changed'):
            reuse_build(self.root, output)
        self.assertEqual(evidence, original.read_bytes())

    def test_tracks_dotnet_sdk(self):
        output = self.root.parent / 'slot'
        self.seed_build(output, ['App'])
        self.assertTrue(reuse_build(self.root, output, projects=['App']))
        self.sdk.return_value = {'path': 'dotnet', 'version': '10.0.300'}
        self.assertFalse(reuse_build(self.root, output, projects=['App']))


if __name__ == '__main__':
    unittest.main()
