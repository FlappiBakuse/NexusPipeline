"""Prepare authenticated official baseline bytes and controlled target transports for the adoption diagnostic."""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from tools import application_reader, update_baseline, update_packages, update_recipe

parser = argparse.ArgumentParser(allow_abbrev=False)
for name in ('application', 'compatible-baseline', 'uninstaller', 'baseline-package', 'baseline-release', 'output'):
    parser.add_argument('--' + name, type=Path, required=True)
args = parser.parse_args()
if args.output.exists():
    parser.error('Diagnostic fixture output exists')
application_reader.read(args.application, '0.17.5')
application_reader.read(args.compatible_baseline, '0.17.4')
official = update_baseline.read(args.baseline_package, json.loads(args.baseline_release.read_bytes()), target_version='0.17.4')
args.output.mkdir(parents=True)
packages = args.output / 'packages'
packages.mkdir()
source_raw = (args.compatible_baseline / update_recipe.MANIFEST_PATH).read_bytes()
source_manifest = json.loads(source_raw)
source_files = [{key: file[key] for key in ('path','sizeBytes','sha256')} for file in source_manifest['files']]
source_files.append({'path':update_recipe.MANIFEST_PATH,'sizeBytes':len(source_raw),'sha256':hashlib.sha256(source_raw).hexdigest()})
source_package = args.output / 'compatible-source-Portable.zip'
source_recipe = update_recipe.create(source_manifest, source_raw, 'portable')
update_recipe.generate(args.compatible_baseline, source_package, source_recipe, update_recipe.guides(source_recipe))
with source_package.open('rb') as stream: source_digest = hashlib.file_digest(stream,'sha256').hexdigest()
baseline = ({'releaseTag':'v0.17.4','releaseId':174,'assetId':17401,'assetSha256':source_digest,
    'payloadManifestSha256':hashlib.sha256(source_raw).hexdigest()}, source_files)
raw = (args.application / update_recipe.MANIFEST_PATH).read_bytes()
manifest = json.loads(raw)
payload = [{key: file[key] for key in ('path', 'sizeBytes', 'sha256')} for file in manifest['files']]
payload.append({'path': update_recipe.MANIFEST_PATH, 'sizeBytes': len(raw), 'sha256': hashlib.sha256(raw).hexdigest()})
report = update_packages.generate(args.application, packages, 'v0.17.5', manifest, payload, baseline=baseline)
if not report['manualCover']['allowed']: raise ValueError('Compatible schema2 fixture must allow exact covering')
tools=args.output/'tools'; tools.mkdir()
import shutil
shutil.copy2(args.uninstaller,tools/'NexusPipeline.uninstall.exe')
setup = packages / 'NexusPipeline-v0.17.5-win-x64-Setup.exe'
setup.write_bytes(b'not-executed-metadata-only-fixture')
assets = []
for index, file in enumerate(sorted(packages.iterdir()), 1):
    if file.suffix not in ('.zip', '.exe'):
        continue
    with file.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    assets.append({'id': 17400 + index, 'name': file.name, 'state': 'uploaded', 'size': file.stat().st_size,
        'digest': 'sha256:' + digest,
        'browser_download_url': 'https://github.com/FlappiBakuse/NexusPipeline/releases/download/v0.17.5/' + file.name})
release = {'id': 175, 'tag_name': 'v0.17.5', 'draft': False, 'prerelease': True,
           'published_at': '2026-10-14T00:00:00Z', 'assets': assets}
for name, value in [('release.json', release), ('assets.json', assets),
                    ('policy.json', {'schemaVersion': 1, 'repository': 'FlappiBakuse/NexusPipeline', 'barriers': []})]:
    (packages / name).write_text(json.dumps(value), encoding='utf-8')
(args.output / 'fixture.json').write_text(json.dumps({'classification': 'LOCAL_DIAGNOSTIC',
    'officialBaselineReadonly': official[0], 'compatibleSourceFixture': baseline[0], 'compatibleSourcePublished': False, 'targetIsFutureVersionFixture': True, 'targetPackages': report, 'setupExecuted': False}, indent=2) + '\n', encoding='utf-8')
print('Verified baseline and controlled adoption transports prepared')
