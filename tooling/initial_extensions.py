"""Select verified first-install inputs independently of application ownership."""
import hashlib
import json
from pathlib import Path
import re

from binary_inputs import hash_file
from desktop_bundle import require_no_links
from store_workspace import verify_store

PREFIX = 'package-info/initial-extensions/'


def valid_name(name):
    return name.startswith(PREFIX) and '/' not in name[len(PREFIX):] and bool(re.fullmatch(
        r'(?:selection\.json|index\.json|catalog\.json|catalog\.sig\.json|[A-Za-z][A-Za-z0-9]*-[0-9]+\.[0-9]+\.[0-9]+(?:-(?:beta|rc)\.[1-9][0-9]*)?\.zip)',
        name[len(PREFIX):]))


def records(inputs):
    return [dict(path=name, sizeBytes=len(value) if isinstance(value, bytes) else value.stat().st_size,
                 sha256=hashlib.sha256(value).hexdigest() if isinstance(value, bytes) else hash_file(value))
            for name, value in sorted(inputs.items())]


def collect_initial(directory, profile):
    directory = Path(directory).absolute()
    require_no_links(directory)
    receipt = directory / 'store-receipt.json'
    stored = json.loads(receipt.read_bytes())
    if stored.get('status') != 'PASS_LOCAL_SIGNED_STORE' or stored['profile'] != profile:
        raise ValueError('Initial catalog profile does not match the product')
    bundle = verify_store(directory, stored)
    catalog = json.loads((bundle / 'catalog.json').read_bytes())
    if hash_file(bundle / 'catalog.json') != stored['pointer']['catalogSha256']:
        raise ValueError('Signed initial catalog changed')
    ids = stored['bundledExtensions']
    selected = [item for item in catalog['entries'] if item['extensionId'] in ids]
    if len(set(ids)) != len(ids) or len(selected) != len(ids) or not 1 <= len(ids) <= 64:
        raise ValueError('Initial extensions are incomplete')
    names = ['index.json', 'catalog.json', 'catalog.sig.json']
    names.extend(item['artifactName'] + '-' + item['packageVersion'] + '.zip' for item in selected)
    if not set(names) <= {item['path'] for item in stored['files']}:
        raise ValueError('Initial extension package missing')
    inputs = {PREFIX + name: bundle / name for name in names}
    inputs[PREFIX + 'selection.json'] = (json.dumps(dict(format='nexus.extensions.initial-selection',
        version=1, extensionIds=sorted(ids)), indent=2) + '\n').encode('utf-8')
    return inputs, dict(path=str(receipt), sha256=hash_file(receipt), files=records(inputs))


def from_assembly(assembly):
    bound = assembly.get('initialExtensions')
    if bound is None:
        return {}
    path = Path(bound['path'])
    if hash_file(path) != bound['sha256']:
        raise ValueError('Initial store receipt changed')
    inputs, actual = collect_initial(path.parent, assembly['profile'])
    if actual != bound:
        raise ValueError('Initial distribution inputs changed')
    return inputs
