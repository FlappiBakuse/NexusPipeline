"""Compose strict native test expectations without duplicating suite ownership."""
import json
import re


def load_expected_methods(source_root, group):
    expected = {}
    visited = set()
    active = set()

    def read(name):
        if not isinstance(name, str) or not re.fullmatch(r"[a-z]+(?:-[a-z]+)*", name):
            raise ValueError("Invalid native test policy name")
        if name in active or name in visited or len(visited) + len(active) >= 16:
            raise ValueError("Repeated, cyclic, or oversized native test policy closure")
        active.add(name)
        policy = json.loads((source_root / "tests" / "policy" / (name + ".json")).read_text(encoding="utf-8"))
        if not isinstance(policy, dict) or set(policy) - {"group", "expectedMethods", "includes"} or policy.get("group") != name:
            raise ValueError("Unrecognized native test policy")
        methods = policy.get("expectedMethods", {})
        includes = policy.get("includes", [])
        if not isinstance(methods, dict) or not isinstance(includes, list) or len(includes) > 12:
            raise ValueError("Invalid native test policy members")
        for method, count in methods.items():
            if not isinstance(method, str) or not re.fullmatch(r"[A-Za-z_]\w*(?:[.+][A-Za-z_]\w*){2,}", method) or method in expected or type(count) is not int or count < 1:
                raise ValueError("Duplicate or invalid native test expectation")
            expected[method] = count
        for child in includes:
            read(child)
        active.remove(name)
        visited.add(name)

    read(group)
    if not expected:
        raise ValueError("Empty native test expectation closure")
    return expected
