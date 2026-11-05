"""Keep signed generations and their receipt identities within one owned workspace."""
import json
from pathlib import Path

from binary_inputs import hash_file
from desktop_bundle import require_no_links, safe_name
from source_snapshot import write_json


def bundle_path(directory, record):
    relative = record.get('bundleRelativePath', 'bundle')
    safe_name(relative)
    bundle = directory / relative
    require_no_links(bundle)
    if directory not in bundle.parents or not bundle.is_dir():
        raise ValueError('Store bundle is outside its owned workspace')
    return bundle


def verify_store(directory, record):
    if record.get('status') != 'PASS_LOCAL_SIGNED_STORE':
        raise ValueError('Incomplete store workspace')
    bundle = bundle_path(directory, record)
    expected = record['files']
    if any('/' in safe_name(item['path']) for item in expected):
        raise ValueError('Store files must be direct children')
    if len({item['path'].casefold() for item in expected}) != len(expected):
        raise ValueError('Repeated store file identity')
    if {path.name for path in bundle.iterdir()} != {item['path'] for item in expected}:
        raise ValueError('Store bundle has unowned files')
    for item in expected:
        path = bundle / item['path']
        require_no_links(path)
        if not path.is_file() or path.stat().st_size != item['sizeBytes'] or hash_file(path) != item['sha256']:
            raise ValueError('Store bundle changed')
    return bundle


def prepare_generation(output, generation, sequence):
    require_no_links(output)
    previous = None
    if output.exists():
        if not (output / 'store-receipt.json').is_file() or any(
                item.name not in ('bundle', 'store-receipt.json', 'generations') for item in output.iterdir()):
            raise ValueError('Existing store workspace is not owned')
        previous = (output / 'store-receipt.json').read_bytes()
        record = json.loads(previous)
        verify_store(output, record)
        if sequence <= record['pointer']['sequence'] or generation == record['pointer']['generationId']:
            raise ValueError('New store generation requires a higher sequence and distinct identity')
        history = output / 'generations' / record['pointer']['generationId']
        require_no_links(history)
        history.mkdir(parents=True, exist_ok=True)
        receipt = history / 'store-receipt.json'
        if receipt.exists() and receipt.read_bytes() != previous:
            raise ValueError('Historical store receipt changed')
        if not receipt.exists():
            receipt.write_bytes(previous)
    else:
        output.mkdir(parents=True)
    directory = output / 'generations' / generation
    require_no_links(directory)
    directory.mkdir(parents=True)
    (directory / 'bundle').mkdir()
    return directory, previous


def publish_generation(output, directory, record, previous):
    receipt = output / 'store-receipt.json'
    if (receipt.read_bytes() if receipt.exists() else None) != previous:
        raise ValueError('Store pointer changed during generation')
    if previous is not None:
        verify_store(output, json.loads(previous))
    record['bundleRelativePath'] = (directory / 'bundle').relative_to(output).as_posix()
    verify_store(output, record)
    write_json(directory / 'store-receipt.json', record)
    pending = output / 'store-receipt.pending.json'
    require_no_links(pending)
    if pending.exists():
        raise ValueError('Unowned pending store pointer')
    write_json(pending, record)
    pending.replace(receipt)
