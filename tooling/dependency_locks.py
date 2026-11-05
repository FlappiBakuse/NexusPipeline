"""Resolve explicit project pins into reviewable locks without editing the source export."""
from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess
import uuid
import xml.etree.ElementTree as ET
from build_inputs import solution_inputs

from build_product import build_environment, update_export
from source_snapshot import ALGORITHM, digest, source_files, write_json, external_directory


def update_locks(root, build_root, projects=None):
    output = external_directory(root, build_root)
    source = update_export(root, output)
    run = output / "dependency-runs" / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S") + "-" + uuid.uuid4().hex[:8])
    locks = run / "locks"
    locks.mkdir(parents=True)
    frozen = Path(source['exportRoot'])
    selected = None
    solution = frozen / 'NexusPipeline.slnx'
    if projects:
        component = solution_inputs(frozen, 'test', build_environment(output), projects,
            json.loads(Path(source['sourceManifestPath']).read_bytes())['files'])
        selected = set(component['projectNames'])
        document = ET.parse(solution)
        for project in list(document.getroot()):
            if Path(project.attrib['Path']).stem not in selected:
                document.getroot().remove(project)
            else:
                project.set('Path', str(frozen / project.attrib['Path']))
        solution = run / 'selected.slnx'
        document.write(solution, encoding='utf-8', xml_declaration=True)
    command = ["dotnet", "restore", str(solution),
               "--force-evaluate", "--disable-parallel", "--disable-build-servers", "-m:1", "--nologo", "-p:RestoreLockedMode=false",
               f"-p:NexusLockOutputRoot={locks}", f"-p:NexusArtifactsRoot={output / 'build'}",
               f"-p:NexusPackagesRoot={output.parent / 'cache/nuget'}", "-p:NexusTestHost=true"]
    process = subprocess.run(command, cwd=source["exportRoot"], env=build_environment(output),
                             stdout=subprocess.PIPE, stderr=subprocess.STDOUT, encoding="utf-8", errors="replace")
    (run / "restore.log").write_bytes(process.stdout.encode())
    print(process.stdout, flush=True)
    receipt = {"status": "FAIL", "command": command, "exitCode": process.returncode, "source": source,
               "nativeLog": str(run / "restore.log"), "importedLocks": []}
    write_json(run / "receipt.json", receipt)
    if process.returncode:
        return process.returncode
    current = (json.dumps({"manifestAlgorithm": ALGORITHM, "files": source_files(root)}, ensure_ascii=False, indent=2) + "\n").encode()
    if digest(current) != source["sourceSnapshotHash"]:
        raise ValueError("Author source changed during dependency resolution")
    pending = []
    for project in sorted(root.rglob("*.csproj")):
        if selected is not None and project.stem not in selected:
            continue
        generated = locks / (project.stem + ".packages.lock.json")
        raw = generated.read_bytes()
        value = json.loads(raw.decode("utf-8-sig"))
        if not value.get("dependencies"):
            raise ValueError(f"Missing resolved dependency graph: {project}")
        normalized = (raw.decode("utf-8-sig").replace("\r\n", "\n").rstrip("\n") + "\n").encode()
        path = project.parent / "packages.lock.json"
        pending.append((path, normalized))
        receipt["importedLocks"].append({"path": path.relative_to(root).as_posix(), "nativeSha256": digest(raw),
                                        "sourceSha256": digest(normalized), "nativePath": str(generated)})
    for path, data in pending:
        path.write_bytes(data)
    receipt["status"] = "PASS_DEPENDENCY_RESOLUTION_ONLY"
    write_json(run / "receipt.json", receipt)
    print(str(run / "receipt.json"), flush=True)
    return 0
