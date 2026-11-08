"""Publish a frozen Host and maintenance runtime with the pinned desktop shell."""
from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess
import time
import uuid

from binary_inputs import hash_file
from build_product import build_environment, build_product
from desktop_bundle import bundle_desktop, require_no_links
from dotnet_bundle import verify_publish
from application_manifest import create_manifest
from source_snapshot import digest, source_files, write_json, external_directory


def inventory(root):
    result = []
    for path in sorted(root.rglob('*')):
        require_no_links(path)
        if path.is_file():
            result.append(dict(path=path.relative_to(root).as_posix(), sizeBytes=path.stat().st_size, sha256=hash_file(path)))
    return result


def clear_publish_slot(directory, receipt):
    previous = json.loads(receipt.read_bytes()) if receipt.is_file() else []
    if inventory(directory) != previous:
        raise ValueError('Publish slot contains changed or unowned files')
    try:
        for record in previous:
            (directory / record['path']).unlink()
    finally:
        write_json(receipt, inventory(directory))


def publish_product(root, build_root, output, runtime_archive, profile):
    root = Path(root).resolve(strict=True)
    build_root, output = external_directory(root, build_root), external_directory(root, output)
    for path in (build_root, output):
        require_no_links(path)
        if path == root or root in path.parents or path in root.parents:
            raise ValueError('Product outputs must be outside the source tree')
    if output == build_root or output in build_root.parents or build_root in output.parents:
        raise ValueError('Product and compile slots must be disjoint')
    ownership = output / 'product-workspace.json'
    expected_owner = dict(format='nexus.product.workspace', version=1, buildRoot=str(build_root), profile=profile)
    if output.exists() and any(output.iterdir()):
        if not ownership.is_file() or json.loads(ownership.read_bytes()) != expected_owner:
            raise ValueError('Existing product workspace is not owned by this build')
    output.mkdir(parents=True, exist_ok=True)
    write_json(ownership, expected_owner)
    result = build_product(root, build_root, profile, ['NexusPipeline.Host', 'NexusPipeline.Launcher', 'NexusPipeline.Maintenance', 'NexusPipeline.Installer'])
    if result:
        raise ValueError('Product compile failed; original build reports retained')
    build = json.loads((build_root / 'build-receipt.json').read_bytes())
    source = build['source']
    frozen = Path(source['exportRoot'])
    source_manifest = json.loads(Path(source['sourceManifestPath']).read_bytes())['files']
    if source_files(root) != source_manifest:
        raise ValueError('Product source changed after compilation')
    run = output / 'runs' / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S') + '-' + uuid.uuid4().hex[:8])
    run.mkdir(parents=True)
    report = dict(status='RUNNING', profile=profile, source=source, buildReceipt=str(build_root / 'runs' / build['runId'] / 'build-receipt.json'), commands=[])
    write_json(run / 'publish-receipt.json', report)
    env = build_environment(build_root)
    published = {}
    try:
        for project in ('Host', 'Launcher', 'Maintenance', 'Installer'):
            publish_component(root, build_root, output, profile, build, source, frozen, run, env, report, published, project)
        report['installerWorker'] = dict(path=str(output / 'publish/installer/NexusPipeline.Installer.exe'), file=published.pop('Installer')[0])
        desktop = output / 'desktop'
        desktop_result = bundle_desktop(frozen, Path(runtime_archive), desktop)
        report['desktop'] = desktop_result
        report['desktopReceiptSha256'] = hash_file(desktop / 'desktop-bundle.json')
        report['published'] = published
        frontend = next(item for item in build['web']['artifacts'] if item['path'] == 'index.html')
        index = Path(build['web']['output']) / 'index.html'
        if index.stat().st_size != frontend['sizeBytes'] or hash_file(index) != frontend['sha256']:
            raise ValueError('Published frontend changed from the compiled input')
        manifest = create_manifest(frozen, source, profile, published,
            json.loads((desktop / 'desktop-bundle.json').read_bytes()), frontend['sha256'])
        write_json(run / 'payload-manifest.json', manifest)
        report['applicationManifest'] = dict(path=str(run / 'payload-manifest.json'), sha256=hash_file(run / 'payload-manifest.json'))
        if source_files(root) != source_manifest:
            raise ValueError('Product source changed during publishing')
        report['status'] = 'PASS_PUBLISHED_NOT_ASSEMBLED'
        write_json(run / 'publish-receipt.json', report)
        write_json(output / 'publish-current.json', dict(receipt=str(run / 'publish-receipt.json'), sha256=hash_file(run / 'publish-receipt.json')))
        return dict(status=report['status'], receipt=str(run / 'publish-receipt.json'))
    except Exception as error:
        report.update(status='FAIL', error=str(error))
        write_json(run / 'publish-receipt.json', report)
        raise


def publish_component(root, build_root, output, profile, build, source, frozen, run, env, report, published, project):
    destination = output / 'publish' / project.lower()
    slot_receipt = output / ('publish-' + project.lower() + '-files.json')
    proof_path = output / ('publish-' + project.lower() + '-proof.json')
    component = build['projectInputs']['NexusPipeline.' + project]
    sdk = shared_sdk(build_root, build)
    publish_inputs = dict(component=component, profile=profile, runtime='win-x64', selfContained=False,
                          singleFile=True, nativeExtraction=True, readyToRun=True,
                          sdkSha256=hash_file(sdk),
                          tools={name: hash_file(root / 'tooling' / name) for name in ('product_publish.py', 'dotnet_bundle.py', 'verify_manifests.py')})
    if component.get('conservativeReasons'):
        publish_inputs['sourceSnapshotHash'] = source['sourceSnapshotHash']
    signature = digest(json.dumps(publish_inputs, sort_keys=True).encode())
    if proof_path.is_file():
        proof = json.loads(proof_path.read_bytes())
        if proof.get('inputs') == signature and proof.get('files') == inventory(destination):
            original = Path(proof['receipt'])
            original_proof = json.loads(original.read_bytes())
            if (hash_file(original) != proof['receiptSha256'] or original_proof.get('status') != 'PASS' or
                    original_proof.get('inputs') != signature or original_proof.get('files') != proof['files']):
                raise ValueError('Original publish proof changed')
            published[project] = proof['files']
            report['commands'].append(dict(project=project, reusedReceipt=str(original), receiptSha256=proof['receiptSha256']))
            return
    clear_publish_slot(destination, slot_receipt)
    command = ['dotnet', 'publish', str(frozen / 'apps' / project.lower() / ('NexusPipeline.' + project + '.csproj')),
               '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'false', '--nologo',
               '--disable-build-servers', '-m:1', '--output', str(destination),
               f'-p:NexusArtifactsRoot={output / "work"}', f'-p:NexusPackagesRoot={build_root.parent / "cache/nuget"}',
               f'-p:NexusTestHost={str(profile == "test").lower()}',
               f'-p:NexusWebRoot={build_root / "web-workspace/apps/web/dist"}', '-p:RestoreLockedMode=true',
               f'-p:NexusSdkRuntime={sdk}',
               '-p:IncludeNativeLibrariesForSelfExtract=true',
               '-p:IncludeAllContentForSelfExtract=false', '-p:EnableCompressionInSingleFile=false',
               '-p:CopyOutputSymbolsToPublishDirectory=false', '-p:PublishReferencesDocumentationFiles=false',
               '-p:DebugType=None', '-p:DebugSymbols=false', '-p:AllowedReferenceRelatedFileExtensions=.xml',
               '-p:PublishIISAssets=false',
               '-p:PublishTrimmed=false', '-p:PublishReadyToRun=true']
    started = time.monotonic()
    try:
        completed = subprocess.run(command, cwd=frozen, env=env, capture_output=True, encoding='utf-8', errors='replace', timeout=600)
    finally:
        write_json(slot_receipt, inventory(destination))
    log = run / (project.lower() + '.log')
    log.write_bytes((completed.stdout + completed.stderr).encode('utf-8'))
    report['commands'].append(dict(command=command, exitCode=completed.returncode, elapsedSeconds=time.monotonic() - started,
                                   log=str(log), logSha256=hash_file(log)))
    write_json(run / 'publish-receipt.json', report)
    if completed.returncode:
        raise ValueError('Framework-dependent single-file publish failed: ' + str(log))
    entry = destination / ('NexusPipeline.' + project + '.exe')
    bundle = verify_publish(entry, project, shared_sdk(build_root, build).read_bytes())
    from verify_manifests import embedded_manifest, pe_subsystem
    import xml.etree.ElementTree as ET
    levels = [element.attrib['level'] for element in ET.fromstring(embedded_manifest(entry)).iter()
              if element.tag.endswith('requestedExecutionLevel')]
    expected_level = 'asInvoker' if project == 'Installer' or profile == 'test' else 'requireAdministrator'
    if levels != [expected_level]:
        raise ValueError('Published executable privilege differs from its role')
    expected_subsystem = 2 if project in ('Host', 'Launcher') else 3
    if pe_subsystem(entry) != expected_subsystem:
        raise ValueError('Published executable subsystem differs from its GUI or console role')
    bundle['executionLevel'] = levels[0]
    bundle['subsystem'] = expected_subsystem
    if inventory(destination) != [dict(path=entry.name, sizeBytes=entry.stat().st_size, sha256=hash_file(entry))]:
        raise ValueError('Single-file publication contains additional runtime or debug files')
    write_json(slot_receipt, inventory(destination))
    published[project] = inventory(destination)
    project_receipt = run / (project.lower() + '-published.json')
    write_json(project_receipt, dict(status='PASS', inputs=signature, source=source, bundle=bundle,
                                   command=report['commands'][-1], files=published[project]))
    write_json(proof_path, dict(inputs=signature, files=published[project], receipt=str(project_receipt), receiptSha256=hash_file(project_receipt)))


def shared_sdk(build_root, build):
    binary_manifest = json.loads(Path(build['binaryManifest']['path']).read_bytes())
    relative = 'NexusPipeline.Extensibility/release/NexusPipeline.Extensibility.dll'
    original = build_root / 'build/bin' / relative
    expected = next(item for item in binary_manifest if item['path'] == relative)
    if hash_file(original) != expected['sha256'] or original.stat().st_size != expected['sizeBytes']:
        raise ValueError('The shared SDK producer artifact changed')
    return original
