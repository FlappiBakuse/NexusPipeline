"""Read application identities without loading or running candidate assemblies."""
from __future__ import annotations
import hashlib
import os
from pathlib import Path
import subprocess
try:
    from .build_identity import parse_json,validate_record
except ImportError:
    from build_identity import parse_json,validate_record

def read(directory: Path,version: str|None=None) -> dict:
    root=Path(__file__).resolve().parents[1]
    sources=['Directory.Build.props','tools/NexusPipeline.PayloadReader/NexusPipeline.PayloadReader.csproj','tools/NexusPipeline.PayloadReader/Program.cs',
             'src/Platform/Storage/BundleResourceReader.cs','src/Platform/Storage/PayloadPathSafety.cs','src/Modules/Updates/ApplicationPayload.cs','src/Modules/Updates/ApplicationBuildIdentity.cs','src/Shared/Versioning/NexusVersion.cs']
    digest=hashlib.sha256()
    for name in sources:digest.update(name.encode());digest.update((root/name).read_bytes())
    cache=Path(os.environ.get('NEXUS_TEST_ARTIFACT_ROOT',os.environ.get('TEMP',''))).resolve()/'application-reader'/digest.hexdigest()
    cache.mkdir(parents=True,exist_ok=True)
    dll=cache/'out'/'NexusPipeline.PayloadReader.dll'
    if not dll.exists():
        result=subprocess.run(['dotnet','build',str(root/'tools/NexusPipeline.PayloadReader/NexusPipeline.PayloadReader.csproj'),'--configuration','Release',
            '--output',str(cache/'out'),'-p:BaseIntermediateOutputPath='+str(cache/'obj')+os.sep,'-p:MSBuildProjectExtensionsPath='+str(cache/'obj')+os.sep],
            text=True,capture_output=True,encoding='utf-8',timeout=90)
        if result.returncode:raise ValueError('Application reader build failed: '+result.stdout+result.stderr)
    result=subprocess.run(['dotnet',str(dll),str(directory),*([version] if version else [])],text=True,capture_output=True,encoding='utf-8',timeout=30)
    if result.returncode:raise ValueError('Application payload rejected: '+result.stderr.strip())
    record=parse_json(result.stdout.encode('utf-8'));validate_record(record);return record
