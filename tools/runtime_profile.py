"""Validate the declared desktop runtime selection before assembly."""
import hashlib
import json
from pathlib import Path
import re


def read_json(file: Path) -> dict:
    def fields(pairs):
        value = {}
        for key, item in pairs:
            if key in value:
                raise ValueError('Duplicate runtime profile field')
            value[key] = item
        return value
    value = json.loads(file.read_bytes(), object_pairs_hook=fields)
    if type(value) is not dict:
        raise ValueError('Invalid runtime profile object')
    return value


def load(root: Path) -> dict:
    file = root / 'desktop/runtime-profile.json'
    profile = read_json(file)
    if set(profile) != {'schemaVersion', 'id', 'locales', 'fallbackLocale', 'dependencyFiles'} or type(profile['schemaVersion']) is not int or profile['schemaVersion'] != 1:
        raise ValueError('Unsupported desktop runtime profile')
    locales = profile['locales']
    if type(profile['id']) is not str or not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,63}', profile['id']) or type(locales) is not list or not 2 <= len(locales) <= 64 or any(type(locale) is not str or not re.fullmatch(r'[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*', locale) for locale in locales) or len(set(locales)) != len(locales) or not {'en-US','zh-CN'}.issubset(locales) or profile['fallbackLocale'] != 'en-US':
        raise ValueError('Invalid desktop runtime locale selection')
    files = profile['dependencyFiles']
    if type(files) is not list or not files or len(files) > 128 or any(type(path) is not str for path in files) or len({path.casefold() for path in files}) != len(files):
        raise ValueError('Invalid desktop dependency selection')
    for path in files:
        if not isinstance(path, str) or not re.fullmatch(r'(?:koffi|@koromix/koffi-win32-x64)/[A-Za-z0-9./_-]+', path) or any(part in ('', '.', '..') for part in path.split('/')):
            raise ValueError('Unsafe desktop dependency path')
    return profile


def frozen(root: Path) -> dict:
    profile = load(root)
    inventory_file = root / 'desktop/runtime-files.json'
    inventory = read_json(inventory_file)
    profile_hash = hashlib.sha256((root / 'desktop/runtime-profile.json').read_bytes()).hexdigest()
    if set(inventory) != {'schemaVersion', 'electronVersion', 'electronArchiveSha256', 'runtimeProfileId', 'runtimeProfileSha256', 'files'} or type(inventory['schemaVersion']) is not int or inventory['schemaVersion'] != 2 or inventory['runtimeProfileId'] != profile['id'] or inventory['runtimeProfileSha256'] != profile_hash:
        raise ValueError('Runtime profile and inventory disagree')
    names = set()
    for item in inventory['files']:
        if type(item) is not dict or set(item) != {'path', 'sizeBytes', 'sha256'} or type(item['path']) is not str or item['path'].casefold() in names or type(item['sizeBytes']) is not int or not 0 <= item['sizeBytes'] <= 256 * 1024 * 1024 or type(item['sha256']) is not str or not re.fullmatch('[0-9a-f]{64}', item['sha256']):
            raise ValueError('Invalid runtime inventory entry')
        names.add(item['path'].casefold())
    if sorted(name for name in names if name.startswith('locales/')) != sorted(f'locales/{locale}.pak'.casefold() for locale in profile['locales']):
        raise ValueError('Runtime locale inventory mismatch')
    return {'runtimeProfileId': profile['id'], 'runtimeProfileSha256': profile_hash,
            'runtimeInventorySha256': hashlib.sha256(inventory_file.read_bytes()).hexdigest()}
