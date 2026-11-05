"""Bind Setup prerequisites to the actual single-file framework declarations."""
import json
from pathlib import Path
import re

from dotnet_bundle import read_bundle, read_member


LOCK = 'tooling/runtime-dependencies.json'
SCRIPT = 'tooling/installer-runtime.iss'
FRAMEWORKS = ('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')


def read_dependencies(root):
    value = json.loads((root / LOCK).read_bytes())
    if set(value) != {'schemaVersion', 'dependencies'} or value['schemaVersion'] != 1 or len(value['dependencies']) != 2:
        raise ValueError('Invalid Setup runtime lock')
    result = {}
    for item in value['dependencies']:
        if (set(item) != {'framework', 'minimumVersion', 'version', 'rid', 'url', 'sizeBytes', 'sha512', 'sha256', 'metadataUrl'} or
                item['framework'] not in FRAMEWORKS or item['framework'] in result or item['rid'] != 'win-x64' or
                item['minimumVersion'] != '10.0.0' or item['version'] != '10.0.14' or
                type(item['sizeBytes']) is not int or not 0 < item['sizeBytes'] <= 134217728 or
                re.fullmatch('[a-f0-9]{64}', item['sha256']) is None or re.fullmatch('[a-f0-9]{128}', item['sha512']) is None or
                item['metadataUrl'] != 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'):
            raise ValueError('Invalid Setup runtime identity')
        prefix = 'Runtime/' if item['framework'] == FRAMEWORKS[0] else 'aspnetcore/Runtime/'
        name = 'dotnet-runtime-' if item['framework'] == FRAMEWORKS[0] else 'aspnetcore-runtime-'
        expected = 'https://builds.dotnet.microsoft.com/dotnet/' + prefix + item['version'] + '/' + name + item['version'] + '-win-x64.exe'
        if item['url'] != expected:
            raise ValueError('Setup runtime must use its fixed official package')
        result[item['framework']] = item
    return result


def required_frameworks(application, installer=None):
    result = {}
    programs = [(application / 'NexusPipeline.exe', 'Host'), (application / 'NexusPipeline.update.exe', 'Maintenance')]
    if installer is not None:
        programs.append((installer, 'Installer'))
    for path, project in programs:
        with path.open('rb') as stream:
            files = read_bundle(stream)
            raw = read_member(stream, files, 'NexusPipeline.' + project + '.runtimeconfig.json', 4)
        options = json.loads(raw)['runtimeOptions']
        if 'includedFrameworks' in options:
            raise ValueError('Setup requires framework-dependent product programs')
        frameworks = options.get('frameworks') or [options['framework']]
        for framework in frameworks:
            name, version = framework['name'], framework['version']
            if name not in FRAMEWORKS or name in result and result[name] != version:
                raise ValueError('Conflicting product framework declarations')
            result[name] = version
    if set(result) != set(FRAMEWORKS):
        raise ValueError('Product framework closure is incomplete')
    return result


def render_runtime(root, application, installer=None):
    dependencies = read_dependencies(root)
    required = required_frameworks(application, installer)
    rows = []
    for framework in FRAMEWORKS:
        item = dependencies[framework]
        if required[framework] != item['minimumVersion']:
            raise ValueError('Setup runtime minimum differs from the actual program')
        description = '.NET Core Runtime 10 x64' if framework == FRAMEWORKS[0] else 'ASP.NET Core Runtime 10 x64'
        arguments = [framework, item['minimumVersion'], description, item['url'], item['url'].rsplit('/', 1)[1], item['sha256']]
        rows.append('  Result := InstallFramework(' + ', '.join("'" + value + "'" for value in arguments) + ', NeedsRestart);')
        rows.append("  if Result <> '' then Exit;")
    return (root / SCRIPT).read_text(encoding='utf-8').replace('@@RUNTIME_CALLS@@', '\n'.join(rows))
