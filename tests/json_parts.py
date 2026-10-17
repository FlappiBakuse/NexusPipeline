"""Resolve partitioned repository metadata and fingerprint every declared input."""
import hashlib
import json
from pathlib import Path
import re


def load_json_input(relative, read):
    files = []
    total = 0

    def read_value(name):
        nonlocal total
        raw = read(name)
        total += len(raw)
        if raw.startswith(b"\xef\xbb\xbf") or len(raw) > 4 * 1024 * 1024 or total > 8 * 1024 * 1024:
            raise ValueError("Invalid JSON input size or BOM")
        raw = raw.decode("utf-8").replace("\r\n", "\n").encode("utf-8")
        files.append({"path": name, "sha256": hashlib.sha256(raw).hexdigest()})
        return json.loads(raw), raw

    manifest, raw = read_value(relative)
    if not isinstance(manifest, dict) or "partitionVersion" not in manifest:
        return {"value": manifest, "bytes": raw, "digest": hashlib.sha256(raw).hexdigest(), "files": files}
    if type(manifest["partitionVersion"]) is not int or manifest["partitionVersion"] != 1 or not isinstance(manifest.get("parts"), list) or not 1 <= len(manifest["parts"]) <= 64:
        raise ValueError("Unsupported JSON partition manifest")
    prefix = relative.removesuffix(".json") + "/"
    sections, seen = {}, {relative.lower()}
    for part in manifest["parts"]:
        if (not isinstance(part, dict) or set(part) != {"path", "section"}
                or part["section"] not in ("groups", "topics") or not isinstance(part["path"], str)
                or not part["path"].startswith(prefix) or not re.fullmatch(r"[a-z0-9][a-z0-9.-]*\.json", part["path"][len(prefix):])
                or part["path"].lower() in seen or part["section"] in manifest):
            raise ValueError("Invalid, duplicate or escaping JSON part")
        seen.add(part["path"].lower())
        value, _ = read_value(part["path"])
        if part["section"] == "topics":
            if not isinstance(value, list):
                raise ValueError("Topics part must be an array")
            sections.setdefault("topics", []).extend(value)
        else:
            if not isinstance(value, dict):
                raise ValueError("Groups part must be an object")
            merged = sections.setdefault("groups", {})
            if merged.keys() & value.keys():
                raise ValueError("Duplicate JSON group")
            merged.update(value)
    if "topics" in sections and "order" not in manifest:
        raise ValueError("Missing JSON topic order")
    if "order" in manifest:
        if not isinstance(manifest["order"], dict) or set(manifest["order"]) != {"topics"} or "topics" not in sections:
            raise ValueError("Invalid JSON topic order")
        topics, order = sections["topics"], manifest["order"]["topics"]
        indexed = {topic.get("id"): topic for topic in topics if isinstance(topic, dict)}
        if (not isinstance(order, list) or len(indexed) != len(topics) or None in indexed
                or len(order) != len(topics) or len(set(order)) != len(order) or any(key not in indexed for key in order)):
            raise ValueError("Incomplete or duplicate JSON topic identities")
        sections["topics"] = [indexed[key] for key in order]
    value = {}
    for key, entry in manifest.items():
        if key in ("partitionVersion", "order"):
            continue
        if key == "parts":
            value.update(sections)
        else:
            value[key] = entry
    fingerprint = json.dumps(files, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return {"value": value, "bytes": (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8"),
            "digest": hashlib.sha256(fingerprint).hexdigest(), "files": files}


def read_json_input(root, relative):
    root = Path(root).resolve()

    def read(name):
        file = root / name
        if not file.resolve().is_relative_to(root) or any(p.is_symlink() or p.is_junction() for p in (file, *file.parents) if p != root and p.is_relative_to(root)):
            raise ValueError("Escaping or linked JSON input")
        return file.read_bytes()

    return load_json_input(relative, read)
