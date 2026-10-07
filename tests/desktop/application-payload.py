"""Exercise the trusted readonly reader against one real frozen application."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from tools.application_reader import read


def main():
    parser = argparse.ArgumentParser(allow_abbrev=False)
    parser.add_argument('--application', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        parser.error('Readonly test output already exists')
    root = args.output / 'application'
    shutil.copytree(args.application, root)
    manifest = root / 'resources/payload-manifest.json'
    original = manifest.read_bytes()
    cases = []

    def reject(case_id, mutate):
        restore = mutate()
        try:
            try:
                read(root, '0.17.0')
            except ValueError as error:
                cases.append({'id': case_id, 'status': 'PASS', 'rejection': str(error)})
            else:
                raise AssertionError('Reader accepted ' + case_id)
        finally:
            restore()
            manifest.write_bytes(original)

    def alter_manifest(change):
        value = json.loads(original)
        change(value)
        manifest.write_text(json.dumps(value), encoding='utf-8')
        return lambda: None

    def replace_asset(relative, replacement):
        file = root / relative
        saved = file.read_bytes()
        file.write_bytes(replacement(saved))
        value = json.loads(original)
        item = next(item for item in value['files'] if item['path'] == relative)
        item.update(sizeBytes=file.stat().st_size, sha256=hashlib.sha256(file.read_bytes()).hexdigest())
        manifest.write_text(json.dumps(value), encoding='utf-8')
        return lambda: file.write_bytes(saved)

    baseline = read(root, '0.17.0')
    cases.append({'id': 'real-application-identity', 'status': 'PASS'})
    reject('unknown-manifest-field', lambda: alter_manifest(lambda value: value.update(untrusted=True)))
    reject('negative-file-size', lambda: alter_manifest(lambda value: value['files'][0].update(sizeBytes=-1)))
    reject('frontend-identity-mismatch', lambda: alter_manifest(lambda value: value.update(frontendHash='0' * 64)))
    reject('invalid-host-bundle-with-matching-file-hash', lambda: replace_asset('NexusPipeline.exe', lambda _: b'not a .NET bundle'))
    reject('invalid-asar-with-matching-file-hash', lambda: replace_asset('resources/desktop/resources/app.asar', lambda value: b'\xff' * 16 + value[16:]))
    desktop = root / 'resources/desktop/NexusPipeline.Desktop.exe'
    missing = desktop.with_suffix('.missing')
    def remove_desktop():
        desktop.rename(missing)
        return lambda: missing.rename(desktop)
    reject('missing-desktop-executable', remove_desktop)
    unknown = root / 'resources/unknown.bin'
    def add_unknown():
        unknown.write_bytes(b'unknown')
        return unknown.unlink
    reject('unknown-application-resource', add_unknown)
    if read(root, '0.17.0') != baseline:
        raise AssertionError('Readonly tests changed the frozen application')
    report = {'schemaVersion': 1, 'status': 'PASS', 'classification': 'LOCAL_DIAGNOSTIC',
              'counts': {'testsRun': len(cases), 'failures': 0, 'skipped': 0}, 'caseIds': [case['id'] for case in cases],
              'cases': cases, 'buildId': baseline['buildId'], 'candidateExecuted': False}
    (args.output / 'reader-report.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print('Real readonly application reader: eight cases PASS')


if __name__ == '__main__':
    main()
