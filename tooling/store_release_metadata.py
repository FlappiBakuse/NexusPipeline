"""Apply release catalog dates only to the exact packages selected for signing."""
from copy import deepcopy
from datetime import datetime
import json

from desktop_bundle import require_no_links


def release_metadata(path, packages, profile):
    if path is None:
        if profile == 'production':
            raise ValueError('Production signing requires the stable distribution catalog')
        return {}
    require_no_links(path)
    document = json.loads(path.read_bytes())
    if document.get('schemaVersion') != 2 or document.get('repository') != 'FlappiBakuse/NexusPipeline-Plugins':
        raise ValueError('Distribution catalog owner differs')
    if document.get('channel', 'stable') != 'stable':
        raise ValueError('Stable signing cannot consume preview metadata')
    entries = document.get('plugins')
    if not isinstance(entries, list) or len(entries) != len(packages):
        raise ValueError('Distribution catalog package set differs')
    by_id = {entry['name']: entry for entry in entries}
    if len(by_id) != len(entries) or set(by_id) != {manifest['extensionId'] for _, manifest, _ in packages}:
        raise ValueError('Distribution catalog identities differ')
    result = {}
    for package, manifest, _ in packages:
        entry = by_id[manifest['extensionId']]
        for key, expected in (('artifactName', manifest['artifactName']), ('version', manifest['packageVersion']),
                              ('sha256', package['artifact']['sha256']), ('sizeBytes', package['artifact']['sizeBytes']),
                              ('manifestSha256', package['manifestSha256'])):
            if entry.get(key) != expected:
                raise ValueError('Distribution catalog package differs: ' + key)
        metadata = deepcopy(entry.get('metadata'))
        authored = deepcopy(manifest.get('metadata'))
        if metadata is None or authored is None:
            if metadata != authored:
                raise ValueError('Distribution catalog metadata differs')
            continue
        for key in ('createdAt', 'updatedAt'):
            value = metadata.pop(key, None)
            authored.pop(key, None)
            if value is not None:
                if not isinstance(value, str) or not value.endswith('Z') or datetime.fromisoformat(value).utcoffset().total_seconds() != 0:
                    raise ValueError('Distribution metadata requires UTC dates')
        if metadata != authored:
            raise ValueError('Distribution catalog authored metadata differs')
        result[manifest['extensionId']] = deepcopy(entry['metadata'])
    return result
