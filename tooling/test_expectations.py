"""Resolve native suite class filters; the native runner discovers methods and cases."""
import json
import re


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
