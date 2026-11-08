"""Publish real single-file inputs for maintenance's executable fixture methods."""
from datetime import datetime, timezone
import json
from pathlib import Path
import uuid

from binary_inputs import hash_file
from build_product import build_environment
from desktop_bundle import require_no_links
from product_publish import inventory, publish_component
from source_snapshot import write_json


def prepare_bundles(source, build_root, run_root, methods):
    required = {'NexusPipeline.StorageTests.MaintenanceContentTests.PinsVerifiedContents'} | {
        'NexusPipeline.StorageTests.MaintenanceBinaryTests.' + name for name in (
            'RestoresFailedCandidate', 'RestoresDerivedPayload', 'CommitsPreparedCandidate',
            'ConsumesStartupUpdate', 'ConsumesDeferredStartup', 'RestartsOwnedDesktop')}
    if not any(required_method == selected or required_method.startswith(selected + '.')
               for required_method in required for selected in methods):
        return None
    output = build_root / 'test-publication'
    require_no_links(output)
    marker = output / 'workspace.json'
    owner = dict(format='nexus.test.publication', version=1, buildRoot=str(build_root))
    if output.exists() and any(output.iterdir()) and (not marker.is_file() or json.loads(marker.read_bytes()) != owner):
        raise ValueError('Native bundle workspace has unknown ownership')
    write_json(marker, owner)
    build = json.loads((build_root / 'build-receipt.json').read_bytes())
    report = dict(status='RUNNING', profile='test', source=build['source'], commands=[])
    run = output / 'runs' / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S') + '-' + uuid.uuid4().hex[:8])
    run.mkdir(parents=True)
    published = {}
    try:
        for project in ('Host', 'Maintenance'):
            publish_component(source, build_root, output, 'test', build, build['source'], source, run,
                              build_environment(build_root), report, published, project)
        report.update(status='PASS', published=published)
    except Exception as error:
        report.update(status='FAIL', error=str(error))
        raise
    finally:
        write_json(run / 'publish-receipt.json', report)
    proof = dict(root=str(output / 'publish'), receipt=str(run / 'publish-receipt.json'),
                 receiptSha256=hash_file(run / 'publish-receipt.json'), files=inventory(output / 'publish'))
    write_json(run_root / 'maintenance-bundles.json', proof)
    return proof


def verify_bundles(proof):
    if proof is not None and (hash_file(Path(proof['receipt'])) != proof['receiptSha256'] or
                              inventory(Path(proof['root'])) != proof['files']):
        raise ValueError('Executable maintenance fixture inputs changed during the native method')
