"""Resolve native suite policy, method filters and their assembly owners."""
import json
import re
from binary_inputs import PROJECTS

PYTHON_GROUPS = {'tooling', 'host-boundary'}
DATA_CLASSES = {'DateFilterTests', 'RecordStoreTests', 'RecordSummaryTests', 'RecordTimingTests'}
DATA_ASSEMBLY = 'NexusPipeline.StorageDataTests'


def suite_classes(source_root, group):
    classes = set()
    visited = set()

    def read(name):
        if not isinstance(name, str) or not re.fullmatch(r"[a-z]+(?:-[a-z]+)*", name) or name in visited:
            raise ValueError("Invalid or repeated native suite")
        visited.add(name)
        policy = json.loads((source_root / "tests/policy" / (name + ".json")).read_bytes())
        if policy.get("group") != name or set(policy) - {"group", "classes", "includes"}:
            raise ValueError("Unrecognized native suite")
        classes.update(policy.get("classes", []))
        for child in policy.get("includes", []):
            read(child)

    read(group)
    if not classes or any(not re.fullmatch(r"NexusPipeline\.[A-Za-z0-9_.+]+", value) for value in classes):
        raise ValueError("Empty or invalid native suite")
    return sorted(classes)


def suite_filter(root, group, method, desktop_method):
    if method is not None:
        methods = [method] if isinstance(method, str) else method
        return "|".join("FullyQualifiedName=" + identity for identity in methods)
    selected = suite_classes(root, group)
    selection = "|".join("FullyQualifiedName~" + owner + "." for owner in selected)
    if group == "storage":
        excluded = suite_classes(root, "storage-config-cross-volume")
        exclusion = "&".join(["FullyQualifiedName!~" + owner + "." for owner in excluded] +
                              ["FullyQualifiedName!=" + desktop_method])
        selection = "(" + selection + ")&" + exclusion
    return selection


def assembly_for(identity):
    owner = identity.split('.')
    if len(owner) < 3 or not identity.startswith('NexusPipeline.'):
        raise ValueError('Native selection has no assembly owner')
    assembly = '.'.join(owner[:2])
    return DATA_ASSEMBLY if assembly == 'NexusPipeline.StorageTests' and owner[2] in DATA_CLASSES else assembly

def select_tests(root, group, methods=None):
    selected = list(methods or [])
    if group in PYTHON_GROUPS:
        if len(selected) > 1:
            raise ValueError('Python accepts one exact method ID')
        projects = []
        if group == 'host-boundary':
            projects = ['NexusPipeline.Host', 'OwnedWriter', 'TargetGame']
        return {'methods': selected, 'projects': projects, 'assemblies': [], 'python': True}
    classes = suite_classes(root, group)
    if group == "storage" and selected:
        for policy in sorted((root / "tests/policy").glob("storage-*.json")):
            if policy.stem != "storage-config-cross-volume":
                classes.extend(suite_classes(root, policy.stem))
        dedicated = suite_classes(root, "storage-config-cross-volume")
        classes = [owner for owner in classes if owner not in dedicated]
    if any(not any(identity.startswith(owner + '.') for owner in classes) for identity in selected):
        raise ValueError('Selected native method is outside this suite')
    chosen = selected or classes
    assemblies = sorted({assembly_for(identity) for identity in chosen})
    if assemblies == [DATA_ASSEMBLY]:
        projects = [DATA_ASSEMBLY]
    else:
        projects = list(PROJECTS[group])
        if DATA_ASSEMBLY in assemblies:
            projects.append(DATA_ASSEMBLY)
    return {'methods': chosen, 'projects': sorted(set(projects)), 'assemblies': assemblies, 'python': False}
