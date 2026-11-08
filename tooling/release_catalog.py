"""Create an immutable signed local store generation from original package receipts."""
import argparse
from datetime import datetime
import json
from pathlib import Path
import re
import shutil
import subprocess
from urllib.parse import urlsplit
import uuid
import zipfile

from binary_inputs import hash_file
from desktop_bundle import require_no_links, safe_name
from source_snapshot import digest, source_files, write_json, external_directory
from artifact_lock import artifact_lock
from store_workspace import prepare_generation, publish_generation
from store_release_metadata import release_metadata


FIXTURE_PUBLIC = 'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6uyjBJaylz5W+08fYBHMupD7vnjEOsNOFQGlLksjWMd+NAK85+0rrHkg8zoubm64PNjUvrMV7JOhQKRVUdWDBg=='


def metadata_root(value, profile):
    parsed = urlsplit(value)
    if (parsed.scheme != 'https' or not parsed.hostname or '.' not in parsed.hostname or parsed.port is not None or
            parsed.username or parsed.password or parsed.query or parsed.fragment or not parsed.path.endswith('/') or
            parsed.path == '/' or '%' in value or '\\' in value):
        raise ValueError('Explicit canonical HTTPS metadata roots required')
    if profile == 'production' and (parsed.hostname.endswith(('.test', '.invalid', '.example', '.local')) or
            parsed.hostname in ('example.com', 'example.org', 'example.net')):
        raise ValueError('Production metadata cannot use example hosts')
    return value


def package_inputs(path):
    require_no_links(path)
    record = json.loads(path.read_bytes())
    if record.get('status') != 'PASS':
        raise ValueError('Incomplete package set')
    result = []
    for entry in record['runs']:
        if not entry['label'].endswith('-package'):
            continue
        receipt = Path(entry['receipt'])
        require_no_links(receipt)
        if hash_file(receipt) != entry['sha256']:
            raise ValueError('Package receipt changed')
        package = json.loads(receipt.read_bytes())
        archive = Path(package['artifact']['path'])
        require_no_links(archive)
        if (package['status'] != 'PASS' or hash_file(archive) != package['artifact']['sha256'] or
                archive.stat().st_size != package['artifact']['sizeBytes'] or package['sdkReceiptSha256'] != record['sdkReceiptSha256']):
            raise ValueError('Package is not bound to the complete SDK package set')
        with zipfile.ZipFile(archive) as payload:
            actual = payload.infolist()
            expected = {item['path']: item for item in package['members']}
            if len(actual) != len(expected) or len({item.filename.casefold() for item in actual}) != len(actual):
                raise ValueError('Package member set changed')
            for member in actual:
                safe_name(member.filename)
                item = expected[member.filename]
                with payload.open(member) as stream:
                    import hashlib
                    actual_hash = hashlib.file_digest(stream, 'sha256').hexdigest()
                if member.file_size != item['sizeBytes'] or actual_hash != item['sha256']:
                    raise ValueError('Package member changed')
            manifest_bytes = payload.read('manifest.json')
        manifest = json.loads(manifest_bytes)
        if (digest(manifest_bytes) != package['manifestSha256'] or manifest['extensionId'] != package['extensionId'] or
                manifest['contractFingerprint'] != package['contractFingerprint'] or manifest['sdkVersion'] != '0.1.0'):
            raise ValueError('Package manifest changed')
        result.append((package, manifest, archive))
    if not 1 <= len(result) <= 64 or len({item[1]['extensionId'] for item in result}) != len(result):
        raise ValueError('A bounded set of distinct packages is required')
    if len({item[1]['contractFingerprint'] for item in result}) != 1:
        raise ValueError('Mixed SDK contract fingerprints')
    return result


def create_catalog(args):
    output = external_directory(Path(__file__).resolve().parent.parent, args.output)
    with artifact_lock(output):
        return assemble_catalog(args)


def assemble_catalog(args):
    root = Path(__file__).resolve().parent.parent
    source = source_files(root)
    generation = str(uuid.UUID(args.generation))
    if generation != args.generation or uuid.UUID(generation).int == 0 or not 1 <= args.sequence <= 9007199254740990:
        raise ValueError('Invalid store generation or sequence')
    if not re.fullmatch(r'[A-Za-z0-9._-]{1,80}', args.key_id):
        raise ValueError('Invalid key identity')
    if not args.published_at.endswith('Z') or datetime.fromisoformat(args.published_at).utcoffset().total_seconds() != 0:
        raise ValueError('An explicit UTC publication timestamp is required')
    metadata = metadata_root(args.metadata_root, args.profile)
    artifacts = metadata_root(args.artifact_root, args.profile)
    inputs = package_inputs(args.package_set)
    released = release_metadata(getattr(args, 'distribution_catalog', None), inputs, args.profile)
    output = args.output.absolute()
    require_no_links(output)
    require_no_links(args.key)
    if output == root or root in output.parents or output in root.parents:
        raise ValueError('Use a store workspace outside the source tree')
    directory, previous = prepare_generation(output, generation, args.sequence)
    bundle = directory / 'bundle'
    entries = []
    for package, manifest, archive in inputs:
        name = safe_name(manifest['artifactName'] + '-' + manifest['packageVersion'] + '.zip')
        if '/' in name:
            raise ValueError('Invalid artifact basename')
        target = bundle / name
        with target.open('xb') as destination, archive.open('rb') as original:
            shutil.copyfileobj(original, destination)
        if hash_file(target) != package['artifact']['sha256']:
            raise ValueError('Package changed during store assembly')
        entry = {key: manifest[key] for key in ('extensionId', 'artifactName', 'packageVersion', 'kind', 'minHostVersion', 'sdkVersion')}
        entry.update(artifact=dict(url=artifacts + manifest['artifactName'] + '/' + name,
            sizeBytes=target.stat().st_size, sha256=hash_file(target)), manifestSha256=package['manifestSha256'])
        if manifest.get('metadata') is not None:
            entry['metadata'] = released.get(manifest['extensionId'], manifest['metadata'])
        entries.append(entry)
    catalog = bundle / 'catalog.json'
    write_json(catalog, dict(format='nexus.store.catalog', version=1, generationId=generation, sequence=args.sequence,
        channel='stable', publishedAt=args.published_at, entries=sorted(entries, key=lambda item: item['extensionId'])))
    signed = sign_metadata(root, args, catalog)
    write_json(bundle / 'catalog.sig.json', dict(format='nexus.detached.signature', version=1, keyId=args.key_id,
        algorithm='ecdsa-p256-sha256', encoding='base64-p1363', signature=signed['signature']))
    prefix = metadata + 'stable/generations/' + generation + '/'
    pointer = dict(format='nexus.store.pointer', version=1, generationId=generation, sequence=args.sequence, channel='stable',
        catalogUrl=prefix + 'catalog.json', catalogSha256=hash_file(catalog), signatureUrl=prefix + 'catalog.sig.json')
    write_json(bundle / 'index.json', pointer)
    if source_files(root) != source:
        raise ValueError('Metadata producer source changed')
    files = [dict(path=path.name, sizeBytes=path.stat().st_size, sha256=hash_file(path)) for path in sorted(bundle.iterdir())]
    publish_generation(output, directory, dict(status='PASS_LOCAL_SIGNED_STORE', profile=args.profile, keyId=args.key_id,
        bundledExtensions=[manifest['extensionId'] for package, manifest, _ in inputs if package['bundledByDefault']],
        publicKey=signed['publicKey'], packageSet=str(args.package_set), packageSetSha256=hash_file(args.package_set),
        producerSourceSha256=digest(json.dumps(source, sort_keys=True).encode()), files=files, pointer=pointer,
        scope='Local signed metadata and original packages; no upload or production release qualification.'), previous)
    return dict(status='PASS_LOCAL_SIGNED_STORE', receipt=str(output / 'store-receipt.json'), bundle=str(bundle))


def sign_metadata(root, args, catalog):
    result = subprocess.run([str(args.node), str(root / 'tooling/metadata_sign.mjs'), str(args.key), str(catalog)],
        check=True, capture_output=True, encoding='utf-8', timeout=30)
    signed = json.loads(result.stdout)
    if args.profile == 'production' and (signed['publicKey'] == FIXTURE_PUBLIC or
            any(word in args.key_id.lower() for word in ('test', 'fixture'))):
        raise ValueError('Fixture signing keys cannot produce production metadata')
    if args.profile == 'production':
        trust = json.loads((root / 'src/platform/Security/release-trust.json').read_bytes())
        if (trust.get('state') != 'configured' or trust.get('keys', {}).get(args.key_id) != signed['publicKey'] or
                trust.get('storeRoot') != args.metadata_root or
                not any(args.artifact_root.startswith(value) for value in trust.get('artifactRoots', [])) or
                args.sequence < trust.get('storeStableFloor', 1)):
            raise ValueError('Signing identity, roots or sequence differ from the production Host trust')
    return signed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('package-set', 'output', 'key', 'node'):
        parser.add_argument('--' + name, type=Path, required=True)
    for name in ('key-id', 'metadata-root', 'artifact-root', 'generation', 'published-at'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--sequence', type=int, required=True)
    parser.add_argument('--profile', choices=('test', 'production'), required=True)
    parser.add_argument('--distribution-catalog', type=Path)
    print(json.dumps(create_catalog(parser.parse_args()), indent=2))


if __name__ == '__main__':
    main()
