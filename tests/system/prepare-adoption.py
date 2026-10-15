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
for name in ('application', 'baseline-package', 'baseline-release', 'output'):
    parser.add_argument('--' + name, type=Path, required=True)
args = parser.parse_args()
if args.output.exists():
    parser.error('Diagnostic fixture output exists')
application_reader.read(args.application, '0.17.4')
baseline = update_baseline.read(args.baseline_package, json.loads(args.baseline_release.read_bytes()), target_version='0.17.4')
args.output.mkdir(parents=True)
with zipfile.ZipFile(args.baseline_package) as archive:
    for file in baseline[1]:
        target = args.output / 'baseline' / file['path']
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(archive.read(file['path']))
packages = args.output / 'packages'
packages.mkdir()
raw = (args.application / update_recipe.MANIFEST_PATH).read_bytes()
manifest = json.loads(raw)
payload = [{key: file[key] for key in ('path', 'sizeBytes', 'sha256')} for file in manifest['files']]
payload.append({'path': update_recipe.MANIFEST_PATH, 'sizeBytes': len(raw), 'sha256': hashlib.sha256(raw).hexdigest()})
report = update_packages.generate(args.application, packages, 'v0.17.4', manifest, payload, baseline=baseline)
if not report['manualCover']['allowed']:
    raise ValueError('This target does not support exact manual covering of the official baseline')
setup = packages / 'NexusPipeline-v0.17.4-win-x64-Setup.exe'
setup.write_bytes(b'not-executed-metadata-only-fixture')
assets = []
for index, file in enumerate(sorted(packages.iterdir()), 1):
    if file.suffix not in ('.zip', '.exe'):
        continue
    with file.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    assets.append({'id': 17400 + index, 'name': file.name, 'state': 'uploaded', 'size': file.stat().st_size,
        'digest': 'sha256:' + digest,
        'browser_download_url': 'https://github.com/FlappiBakuse/NexusPipeline/releases/download/v0.17.4/' + file.name})
release = {'id': 174, 'tag_name': 'v0.17.4', 'draft': False, 'prerelease': True,
           'published_at': '2026-10-14T00:00:00Z', 'assets': assets}
for name, value in [('release.json', release), ('assets.json', assets),
                    ('policy.json', {'schemaVersion': 1, 'repository': 'FlappiBakuse/NexusPipeline', 'barriers': []})]:
    (packages / name).write_text(json.dumps(value), encoding='utf-8')
(args.output / 'fixture.json').write_text(json.dumps({'classification': 'LOCAL_DIAGNOSTIC',
    'officialBaseline': baseline[0], 'targetPackages': report, 'setupExecuted': False}, indent=2) + '\n', encoding='utf-8')
print('Verified baseline and controlled adoption transports prepared')
