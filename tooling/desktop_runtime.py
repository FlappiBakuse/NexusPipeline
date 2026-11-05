"""Bind the declared desktop runtime selection to its official archive inventory."""
import hashlib
import json


def read_profile(root):
    profile = json.loads((root / 'apps/desktop/runtime-profile.json').read_bytes())
    expected = dict(schemaVersion=1, id='win-x64-en-zh-v1', locales=['en-US', 'zh-CN'], fallbackLocale='en-US', dependencyFiles=[])
    if profile != expected:
        raise ValueError('Unsupported desktop runtime profile')
    return profile


def selected_member(name, profile):
    return not name.startswith('locales/') or name in {f'locales/{locale}.pak' for locale in profile['locales']}


def runtime_identity(root, lock, records):
    profile = read_profile(root)
    files = [dict(path=item['path'], sizeBytes=item['sizeBytes'], sha256=item['sha256'])
             for item in sorted(records, key=lambda item: item['path']) if item['owner'] == 'electron-runtime']
    if ({item['path'] for item in files if item['path'].startswith('locales/')} !=
            {f'locales/{locale}.pak' for locale in profile['locales']} or
            any(item['sizeBytes'] > 256 * 1024 * 1024 for item in files)):
        raise ValueError('Desktop runtime inventory differs from its declared profile')
    profile_hash = hashlib.sha256((root / 'apps/desktop/runtime-profile.json').read_bytes()).hexdigest()
    inventory = dict(schemaVersion=2, electronVersion=lock['version'], electronArchiveSha256=lock['sha256'],
        runtimeProfileId=profile['id'], runtimeProfileSha256=profile_hash, files=files)
    encoded = json.dumps(inventory, sort_keys=True, separators=(',', ':')).encode('utf-8')
    return dict(runtimeProfileId=profile['id'], runtimeProfileSha256=profile_hash,
                runtimeInventorySha256=hashlib.sha256(encoded).hexdigest())
