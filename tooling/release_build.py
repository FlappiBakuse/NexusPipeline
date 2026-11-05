"""Build a source-bound Host candidate with the existing product assemblers."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time
from types import SimpleNamespace

from ci_assets import desktop_archive, download
from ci_build import external, package_plugins
from ci_plan import plugins, source_identity
from release_check import create_host, producer
from release_github import GitHub
from release_provenance import require, sha, verify_source


def build(args):
    host = args.source_root.resolve(strict=True)
    partner = args.partner_root.resolve(strict=True)
    control = producer('host')
    source = source_identity(host, sha(args.source_commit))
    plugin_source = source_identity(partner, sha(args.partner_commit))
    api = GitHub()
    verify_source(api.fetch, control['repository'], source)
    verify_source(api.fetch, 'FlappiBakuse/NexusPipeline-Plugins', plugin_source)
    output = external(host, partner, args.output)
    report = dict(status='RUNNING', source=source, partner=plugin_source, commands=[])
    environment = os.environ.copy()
    environment.pop('NEXUS_SIGNING_KEY', None)
    temporary = output / 'tmp'
    temporary.mkdir()
    environment.update(TEMP=str(temporary), TMP=str(temporary), PYTHONDONTWRITEBYTECODE='1',
                       npm_config_cache=str(output / 'cache/npm'))

    def save():
        (output / 'build.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')

    def run(label, command, cwd):
        start = time.monotonic()
        log = output / (label + '.log')
        with log.open('w', encoding='utf-8') as stream:
            result = subprocess.run(command, cwd=cwd, env=environment, stdout=stream, stderr=subprocess.STDOUT)
        report['commands'].append(dict(label=label, command=command, cwd=str(cwd), exitCode=result.returncode,
                                       seconds=time.monotonic() - start, log=str(log)))
        save()
        require(result.returncode == 0, 'Host candidate input failed: ' + label)
        return log.read_text(encoding='utf-8')

    def python(label, repository, script, *arguments):
        return run(label, [sys.executable, '-B', str(repository / script), *map(str, arguments)], repository)

    key = output / 'signing-key.pem'
    try:
        require(bool(os.environ.get('NEXUS_SIGNING_KEY')), 'Production signing identity is not configured')
        trust = json.loads((host / 'src/platform/Security/release-trust.json').read_bytes())
        require(trust.get('state') != 'unconfigured', 'Production release trust is not configured')
        consumer = output / 'consumer'
        run('consumer-clone', ['git', 'clone', '--no-hardlinks', '--no-checkout', str(partner), str(consumer)], output)
        run('consumer-source', ['git', '-c', 'core.autocrlf=false', 'checkout', '--detach', args.partner_commit], consumer)
        require(source_identity(consumer, args.partner_commit) == plugin_source, 'Plugin source differs')
        build_root = output / 'host-build'
        python('host-production', host, 'tooling/nexus.py', 'build', '--profile', 'production', '--output', build_root)
        package_plugins(python, run, host, consumer, build_root, output, sorted(plugins(partner)), report)
        digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
        sdk = Path(report['sdkReceipt'])
        package_set = output / 'package-set.json'
        package_set.write_text(json.dumps(dict(status='PASS', sdkReceiptSha256=digest(sdk), runs=[
            dict(label=item['extensionId'] + '-package', receipt=item['receipt'], sha256=digest(Path(item['receipt'])))
            for item in report['packages']]), indent=2) + '\n', encoding='utf-8')
        with key.open('x', encoding='utf-8') as stream:
            stream.write(os.environ['NEXUS_SIGNING_KEY'])
        python('signed-bundle', host, 'tooling/release_catalog.py', '--package-set', package_set,
            '--distribution-catalog', partner / 'catalog.json', '--output', output / 'store', '--key', key,
            '--node', 'node', '--key-id', args.key_id, '--metadata-root', args.metadata_root,
            '--artifact-root', args.artifact_root, '--generation', args.generation, '--sequence', args.sequence,
            '--published-at', args.published_at, '--profile', 'production')
        key.unlink()
        archive = desktop_archive(host, output / 'binary-inputs')
        python('product-publish', host, 'tooling/nexus.py', 'product-publish', '--profile', 'production',
               '--build-root', build_root, '--output', output / 'publication', '--runtime-archive', archive)
        python('product-assemble', host, 'tooling/nexus.py', 'product-assemble', '--publication-root', output / 'publication',
               '--output', output / 'application', '--bundle-root', output / 'store')
        installer = download('https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe',
            output / 'binary-inputs/inno.exe', '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732')
        compiler = output / 'compiler'
        run('installer-compiler', [str(installer), '/PORTABLE=1', '/DIR=' + str(compiler), '/VERYSILENT',
                                  '/SUPPRESSMSGBOXES', '/NORESTART'], output)
        python('product-assets', host, 'tooling/product_assets.py', '--assembly-root', output / 'application',
               '--output', output / 'assets', '--compiler', compiler / 'ISCC.exe')
        result = create_host(SimpleNamespace(source_root=host, partner_root=partner, source_commit=args.source_commit,
            partner_commit=args.partner_commit, assets_receipt=output / 'assets/assets-receipt.json', output=output / 'candidate'))
        report.update(result)
        return report
    except Exception as error:
        report.update(status='FAIL', error=str(error))
        raise
    finally:
        if key.exists():
            key.unlink()
        save()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('source-root', 'partner-root', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    for name in ('source-commit', 'partner-commit', 'key-id', 'metadata-root', 'artifact-root', 'generation', 'sequence', 'published-at'):
        parser.add_argument('--' + name, required=True)
    print(json.dumps(build(parser.parse_args()), indent=2))


if __name__ == '__main__':
    main()
