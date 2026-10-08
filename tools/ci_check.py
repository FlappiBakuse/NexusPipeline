"""Plan and run affected production compilation; no project tests are invoked."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

from build_source import changed_paths, fingerprint, full_sha, git, snapshot, source_entries, tree_sha


def scope(paths: list[str]) -> dict:
    targets = set()
    reasons = []
    for path in paths:
        if (path.startswith(('docs/', 'tests/')) or path in ('AGENTS.md', 'CHANGELOG.md')
                or path.endswith('.md') and path != 'README.md'
                or path == '.github/workflows/final-budget.yml'):
            continue
        if path.startswith('frontend/'):
            targets.update(('frontend', 'host', 'architecture'))
        elif path.startswith('src/'):
            targets.update(('frontend', 'host', 'architecture'))
            if path == 'src/Modules/Plugins/Repository/BundledPlugins.json':
                targets.add('bundled')
        elif path.startswith('desktop/'):
            targets.update(('frontend', 'desktop', 'host', 'architecture'))
        else:
            targets.update(('frontend', 'desktop', 'host', 'architecture', 'bundled'))
        reasons.append(path)
    return {'targets': sorted(targets), 'reasons': reasons, 'nodeRequired': bool(targets),
            'dotnetRequired': bool(targets), 'partnerRequired': 'bundled' in targets}


def plan(root: Path, base: str, head: str, checkout: str, partner_sha: str | None = None) -> dict:
    full_sha(root, checkout)
    if git(root, 'rev-parse', 'HEAD') != checkout:
        raise ValueError('Actual checkout does not match the declared SHA')
    paths = sorted(set(changed_paths(root, base, head))
                   | set(filter(None, git(root, 'diff', '--name-only', '-z', 'HEAD').split('\0')))
                   | set(filter(None, git(root, 'ls-files', '--others', '--exclude-standard', '-z').split('\0'))))
    selected = scope(paths)
    if selected['partnerRequired']:
        partner_sha = partner_sha or subprocess.check_output(
            ['git', 'ls-remote', 'https://github.com/FlappiBakuse/NexusPipeline-Plugins.git', 'refs/heads/main'],
            encoding='utf-8').split()[0]
    else:
        partner_sha = partner_sha or checkout
    if not re.fullmatch('[0-9a-f]{40}', partner_sha):
        raise ValueError('Invalid fixed Plugins SHA')
    entries = source_entries(root)
    return {'schemaVersion': 1, 'baseSha': base, 'headSha': head, 'checkoutSha': checkout,
            'sourceFingerprint': fingerprint(entries), 'sourceTreeSha': tree_sha(root, entries),
            'partnerSha': partner_sha, 'paths': paths, **selected}


def run(root: Path, selected: dict, output: Path, partner: Path | None) -> None:
    actual = plan(root, selected['baseSha'], selected['headSha'], selected['checkoutSha'], selected['partnerSha'])
    if selected != actual:
        raise ValueError('Production plan or source inputs changed')
    if output.exists() or output.is_symlink() or output.resolve().is_relative_to(root.resolve()):
        raise ValueError('Use a new external production-check directory')
    output.mkdir(parents=True)
    report = {'schemaVersion': 1, 'status': 'FAIL', 'plan': selected, 'steps': [],
              'qualification': 'production-compilation-only'}
    started = time.monotonic()
    environment = os.environ.copy()
    cache = output.parent / 'cache'
    temporary = output / 'tmp'
    temporary.mkdir()
    environment.update(TEMP=str(temporary), TMP=str(temporary), NUGET_PACKAGES=str(cache / 'nuget'),
                       NUGET_HTTP_CACHE_PATH=str(cache / 'nuget-http'), NUGET_PLUGINS_CACHE_PATH=str(cache / 'nuget-plugins'),
                       npm_config_cache=str(cache / 'npm'), DOTNET_CLI_HOME=str(cache / 'dotnet'),
                       DOTNET_ADD_GLOBAL_TO_PATH='false', DOTNET_GENERATE_ASPNET_CERTIFICATE='false',
                       DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='true',
                       ELECTRON_SKIP_BINARY_DOWNLOAD='1', PYTHONDONTWRITEBYTECODE='1', PYTHONUTF8='1')
    def command(label, args, cwd):
        before = time.monotonic()
        print(f'[production] {label}', flush=True)
        with (output / f'{len(report["steps"]):02d}.log').open('w', encoding='utf-8') as log:
            child = subprocess.Popen([str(arg) for arg in args], cwd=cwd, env=environment,
                                     stdout=subprocess.PIPE, stderr=subprocess.STDOUT, encoding='utf-8', errors='replace')
            for line in child.stdout:
                print(line, end='', flush=True)
                log.write(line)
            code = child.wait()
        report['steps'].append({'label': label, 'command': [str(arg) for arg in args],
                                'exitCode': code, 'elapsedSeconds': time.monotonic() - before})
        if code:
            raise subprocess.CalledProcessError(code, args)
    try:
        targets = set(selected['targets'])
        if not targets:
            print('[production] 无需生产构建: documentation/local-test inputs only', flush=True)
        else:
            source = output / 'source'
            snapshot(root, source, source_entries(root))
            npm = 'npm.cmd' if os.name == 'nt' else 'npm'
            command('Frontend dependencies', [npm, 'ci', '--no-audit', '--no-fund'], source / 'frontend')
            command('Frontend typecheck', [npm, 'run', 'typecheck'], source / 'frontend')
            command('Frontend production build', [npm, 'run', 'build'], source / 'frontend')
            command('A05 bridge boundary', ['node', 'frontend/scripts/architecture-check.mjs', '--report', output / 'architecture-frontend.json'], source)
            if 'desktop' in targets:
                command('Desktop dependencies', [npm, 'ci', '--no-audit', '--no-fund'], source / 'desktop')
                command('Desktop production build', [npm, 'run', 'build'], source / 'desktop')
            from application_build import prepare_compilation
            inputs = prepare_compilation(source, output / 'inputs', source_sha=selected['checkoutSha'],
                                         source_tree_sha=selected['sourceTreeSha'], partner_sha=selected['partnerSha'],
                                         workflow_sha=selected['checkoutSha'])
            properties = ['-p:NexusTestHost=false', '-p:UseSharedCompilation=false',
                          '-p:NexusFrontendProps=' + inputs['frontendProps'], '-p:NexusBuildIdentityPath=' + inputs['identity']]
            command('Host production compilation', ['dotnet', 'build', 'src/NexusPipeline.csproj', '-c', 'Release',
                    '-r', 'win-x64', '--nologo', '--disable-build-servers', *properties, '--output', output / 'host'], source)
            command('Architecture compiler', ['dotnet', 'build', 'tools/NexusPipeline.Architecture/NexusPipeline.Architecture.csproj',
                    '-c', 'Release', '--nologo', '--disable-build-servers', '-p:UseSharedCompilation=false',
                    '--output', output / 'architecture'], source)
            command('A01-A04 production semantics', ['dotnet', output / 'architecture/NexusPipeline.Architecture.dll', source,
                    '--report', output / 'architecture-backend.json', '--frontend-props', inputs['frontendProps'],
                    '--build-identity', inputs['identity'], '--mode', 'production'], source)
            if 'bundled' in targets:
                if partner is None or git(partner, 'rev-parse', 'HEAD') != selected['partnerSha'] or git(partner, 'status', '--porcelain'):
                    raise ValueError('Bundled validation requires the fixed clean Plugins checkout')
                from host_release import stage_bundled_plugins
                (output / 'bundled').mkdir()
                stage_bundled_plugins(output / 'bundled', partner, source / 'src/Modules/Plugins/Repository/BundledPlugins.json')
        report['status'] = 'PASS'
    finally:
        report['elapsedSeconds'] = time.monotonic() - started
        (output / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def main() -> int:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    parser = argparse.ArgumentParser(allow_abbrev=False)
    sub = parser.add_subparsers(dest='operation', required=True)
    planning = sub.add_parser('plan', allow_abbrev=False)
    for name in ('base', 'head', 'checkout'):
        planning.add_argument('--' + name, required=True)
    planning.add_argument('--partner-sha')
    planning.add_argument('--report', type=Path, required=True)
    running = sub.add_parser('run', allow_abbrev=False)
    running.add_argument('--plan', type=Path, required=True)
    running.add_argument('--output', type=Path, required=True)
    running.add_argument('--plugins-root', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    try:
        if args.operation == 'plan':
            selected = plan(root, args.base, args.head, args.checkout, args.partner_sha)
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(selected, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
            if os.environ.get('GITHUB_OUTPUT'):
                with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as stream:
                    for key in ('nodeRequired', 'dotnetRequired', 'partnerRequired', 'partnerSha'):
                        stream.write(f'{key}={str(selected[key]).lower() if isinstance(selected[key], bool) else selected[key]}\n')
            print(json.dumps(selected, ensure_ascii=False), flush=True)
        else:
            run(root, json.loads(args.plan.read_bytes()), args.output.resolve(), args.plugins_root)
        return 0
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        print(f'Production check failed: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
