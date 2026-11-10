"""Resolve the SDK selected by the .NET host for a particular source root."""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess

from binary_inputs import hash_file
from source_snapshot import digest


SDK_PROPS = 'Microsoft.NETCoreSdk.BundledVersions.props'
VSTEST = 'vstest.console.dll'
SDK_FILES = (SDK_PROPS, 'MSBuild.dll', 'Roslyn/bincore/Microsoft.CodeAnalysis.CSharp.dll', VSTEST)


def sdk_output(host, argument, root, environment):
    result = subprocess.run([str(host), argument], cwd=root, env=environment, capture_output=True,
                            encoding='utf-8', errors='replace', timeout=30)
    if result.returncode:
        raise ValueError(f'.NET SDK resolution failed: {(result.stderr or result.stdout).strip()}')
    return result.stdout.strip()


def resolve_sdk(root, environment=None):
    environment = os.environ if environment is None else environment
    search = next((value for key, value in environment.items() if key.upper() == 'PATH'), '')
    executable = shutil.which('dotnet', path=search)
    if executable is None:
        raise ValueError('A stable .NET 10.0 SDK is required')
    host = Path(executable).resolve(strict=True)
    version = sdk_output(host, '--version', root, environment)
    if re.fullmatch(r'10\.0\.[1-9][0-9]{2,}', version) is None:
        raise ValueError(f'Expected a stable .NET 10.0 SDK, resolved {version!r}')
    installed = sdk_output(host, '--list-sdks', root, environment)
    roots = [line[len(version) + 2:-1] for line in installed.splitlines()
             if line.startswith(version + ' [') and line.endswith(']')]
    if len(roots) != 1:
        raise ValueError(f'Resolved .NET SDK {version} must identify one installed SDK directory')
    directory = (Path(roots[0]) / version).resolve(strict=True)
    return {'executable': 'dotnet', 'path': str(host), 'sha256': hash_file(host), 'version': version,
            'directory': str(directory), 'tools': {name: hash_file(directory / name) for name in SDK_FILES}}


def sdk_key(sdk):
    return sdk['version'] + '-' + digest(json.dumps(sdk, sort_keys=True).encode())[:16]


def sdk_environment(environment, sdk):
    # Generated solutions and consumer projects can live outside the policy's source root.
    return {**environment, 'MSBuildSDKsPath': str(Path(sdk['directory']) / 'Sdks')}
