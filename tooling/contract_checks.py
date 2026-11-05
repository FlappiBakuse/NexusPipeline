"""Verify strict authoritative inputs and their generated SDK projections."""
import json
from pathlib import Path
from urllib.parse import urldefrag, urljoin

from contract_codegen import expected_outputs


def no_duplicates(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate contract field: {key}")
        result[key] = value
    return result


def strict_json(path):
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=no_duplicates,
                      parse_constant=lambda _: (_ for _ in ()).throw(ValueError("Non-finite JSON")))


def check_contracts(root):
    documents = {}
    for path in (root / "contracts").rglob("*.json"):
        value = strict_json(path)
        if path.parent == root / "contracts/schemas":
            identifier = value["$id"]
            if identifier in documents:
                raise ValueError("Duplicate schema identity")
            if value["$schema"] != "https://json-schema.org/draft/2020-12/schema":
                raise ValueError("Unexpected schema dialect")
            documents[identifier] = value

    references = []

    def inspect(value, base):
        if isinstance(value, dict):
            if isinstance(value.get("format"), str) and value["format"] not in ("uuid", "date-time", "uri"):
                raise ValueError("Schema uses an unregistered format")
            if "$ref" in value:
                references.append(urljoin(base, value["$ref"]))
            for child in value.values():
                inspect(child, base)
        elif isinstance(value, list):
            for child in value:
                inspect(child, base)

    for identifier, schema in documents.items():
        inspect(schema, identifier)
    for reference in references:
        identifier, fragment = urldefrag(reference)
        if identifier not in documents:
            raise ValueError("Schema reference is not registered offline")
        value = documents[identifier]
        for segment in fragment.strip("/").split("/") if fragment else []:
            value = value[segment.replace("~1", "/").replace("~0", "~")]
    outputs = expected_outputs(root)
    for relative, expected in outputs.items():
        if (root / relative).read_bytes() != expected.encode("utf-8"):
            raise ValueError(f"Generated contract output differs: {relative}")
    for directory in ("sdk/dotnet/Protocol", "src/platform/Serialization/Protocol"):
        actual = {path.relative_to(root).as_posix() for path in (root / directory).glob("*.g.cs")}
        expected = {name for name in outputs if name.startswith(directory + "/") and name.endswith(".g.cs")}
        if actual != expected:
            raise ValueError("Generated DTO ownership differs from the reviewed export")
    actual = {path.relative_to(root).as_posix() for path in (root / "contracts/sdk-public").rglob("*") if path.is_file()}
    expected = {name for name in outputs if name.startswith("contracts/sdk-public/")}
    if actual != expected:
        raise ValueError("SDK public mirror contains unowned or missing files")
    print(f"contracts check: {len(documents)} schemas, {len(references)} offline references, {len(outputs)} generated outputs")
    return 0
