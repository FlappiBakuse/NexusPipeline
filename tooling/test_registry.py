"""Check declared test identities against compiled metadata and select exact suites."""
from collections import defaultdict
import json
import subprocess

from test_expectations import load_expected_methods
from source_snapshot import write_json
from test_selection import PYTHON_GROUPS, assembly_for


def declared_methods(root):
    methods = {}
    for path in sorted((root / "tests/policy").glob("*.json")):
        if path.stem in PYTHON_GROUPS:
            continue
        policy = json.loads(path.read_bytes())
        if not ({"expectedMethods", "includes"} & policy.keys()):
            continue
        for identity, count in load_expected_methods(root, path.stem).items():
            if identity in methods and methods[identity] != count:
                raise ValueError("Conflicting case counts: " + identity)
            methods[identity] = count
    return methods


def verify_registry(root, output, run_root, env, projects=None):
    report = run_root / "compiled-test-identities.json"
    command = ["dotnet", str(output / "build/bin/NexusPipeline.Boundaries/release/NexusPipeline.Boundaries.dll"),
               "--test-identities", str(output / "build/bin")]
    if projects is not None:
        command.append(",".join(projects))
    with report.open("wb") as stream:
        result = subprocess.run(command, cwd=root, env=env, stdout=stream, stderr=subprocess.STDOUT)
    if result.returncode:
        return {"command": command, "exitCode": result.returncode, "nativeReport": str(report)}
    actual = json.loads(report.read_bytes())
    identities = [item["identity"] for item in actual]
    expected = declared_methods(root)
    if projects is not None:
        expected = {name: count for name, count in expected.items() if assembly_for(name) in projects}
    missing = sorted(set(expected) - set(identities))
    unexpected = sorted(set(identities) - set(expected))
    valid = bool(expected) and len(set(identities)) == len(identities) and not missing and not unexpected
    receipt = dict(command=command, exitCode=0 if valid else 1, nativeReport=str(report),
                   declaredMethods=len(expected), compiledMethods=len(actual),
                   declaredCases=sum(expected.values()), missing=missing, unexpected=unexpected)
    write_json(run_root / "test-registry.json", receipt)
    return receipt


def suite_filter(root, group, method, desktop_method):
    if method is not None:
        methods = [method] if isinstance(method, str) else method
        return "|".join("FullyQualifiedName=" + name for name in methods)
    if group in ("core", "contracts", "windows"):
        return None
    if group == "storage":
        excluded = set(load_expected_methods(root, "storage-config-cross-volume")) | {desktop_method}
        return "&".join("FullyQualifiedName!=" + identity for identity in sorted(excluded))
    selected = load_expected_methods(root, group)
    all_classes, selected_classes = defaultdict(set), defaultdict(set)
    for identity in declared_methods(root):
        all_classes[identity.rsplit(".", 1)[0]].add(identity)
    for identity in selected:
        selected_classes[identity.rsplit(".", 1)[0]].add(identity)
    filters = []
    for owner, methods in sorted(selected_classes.items()):
        filters.extend(["FullyQualifiedName~" + owner + "."] if methods == all_classes[owner]
                       else ["FullyQualifiedName=" + identity for identity in sorted(methods)])
    selection = "|".join(filters)
    if not selection or len(selection) > 28000:
        raise ValueError("Declared suite cannot form a bounded native filter")
    return selection
