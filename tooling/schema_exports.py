"""Resolve the offline schema closures for the plugin SDK and the browser."""
from copy import deepcopy
import json
import re

NAME = re.compile(r"^[a-z0-9-]+\.schema\.json$")


def load_exports(root, schemas):
    value = json.loads((root / "contracts/sdk-exports.json").read_bytes())
    if set(value) != {"format", "version", "pluginRoots", "controlRoots", "internalRoots", "sharedDocuments", "sharedFragments"} or \
            value["format"] != "nexus.sdk.exports" or value["version"] != 1:
        raise ValueError("Unknown SDK export definition")
    groups = [value[key] for key in ("pluginRoots", "controlRoots", "internalRoots", "sharedDocuments")]
    listed = [name for group in groups for name in group]
    if any(not NAME.fullmatch(name) for name in listed) or len(set(listed)) != len(listed) or set(listed) != set(schemas):
        raise ValueError("Every authoritative schema needs exactly one reviewed export classification")
    fragments = value["sharedFragments"]
    if len(set(fragments)) != len(fragments):
        raise ValueError("Repeated SDK dependency")
    for reference in fragments:
        resolve(schemas, reference)
    return value


def resolve(schemas, reference, current=None):
    name, _, fragment = reference.partition("#")
    name = name or current
    if name is None or not NAME.fullmatch(name) or name not in schemas:
        raise ValueError("SDK reference is not an offline authoritative schema")
    parts = fragment.split("/") if fragment else []
    if parts and (len(parts) != 3 or parts[0] != "" or parts[1] not in ("$defs", "properties") or not parts[2]):
        raise ValueError("Only reviewed top-level definition or property references are exported")
    node = schemas[name] if not parts else schemas[name][parts[1]][parts[2].replace("~1", "/").replace("~0", "~")]
    return name, fragment, node


def without_definitions(node):
    if isinstance(node, list):
        return [without_definitions(item) for item in node]
    if isinstance(node, dict):
        return {key: deepcopy(value) if key in ("const", "enum", "default", "examples") else without_definitions(value)
                for key, value in node.items() if key != "$defs"}
    return node


def public_schemas(schemas, exports):
    roots = set(exports["pluginRoots"])
    allowed = set(exports["sharedFragments"])
    pending = [(name, "") for name in sorted(roots)]
    visited, result = set(), {}
    while pending:
        name, fragment = pending.pop()
        if (name, fragment) in visited:
            continue
        visited.add((name, fragment))
        if len(visited) > 256:
            raise ValueError("SDK public reference closure exceeds its fixed limit")
        _, _, original = resolve(schemas, name + ("#" + fragment if fragment else ""))
        node = without_definitions(deepcopy(original))
        target = result.setdefault(name, {key: schemas[name][key] for key in ("$schema", "$id") if key in schemas[name]})
        if fragment:
            if name not in roots and (name, "") not in visited:
                target["not"] = {}
            key = fragment.split("/")[-1].replace("~1", "/").replace("~0", "~")
            target.setdefault(fragment.split("/")[1], {})[key] = node
        else:
            target.pop("not", None)
            target.update(node)

        def inspect(value):
            if isinstance(value, dict):
                if any(key in value for key in ("$dynamicRef", "$recursiveRef", "$anchor", "$dynamicAnchor")):
                    raise ValueError("Unreviewed dynamic SDK schema reference")
                if "$ref" in value:
                    doc, part, _ = resolve(schemas, value["$ref"], name)
                    reference = doc + ("#" + part if part else "")
                    if doc not in roots and reference not in allowed:
                        raise ValueError("SDK reference exceeds its reviewed export closure: " + reference)
                    pending.append((doc, part))
                for key, child in value.items():
                    if key not in ("const", "enum", "default", "examples"):
                        inspect(child)
            elif isinstance(value, list):
                for child in value:
                    inspect(child)
        inspect(node)
    return dict(sorted(result.items()))


def schema_semantics(node):
    if isinstance(node, list):
        return [schema_semantics(item) for item in node]
    if not isinstance(node, dict):
        return node
    result = {}
    for key, value in node.items():
        if key in ("$comment", "description", "title", "examples"):
            continue
        if key in ("properties", "$defs", "patternProperties", "dependentSchemas"):
            result[key] = {name: schema_semantics(child) for name, child in value.items()}
        elif key in ("const", "enum", "default"):
            result[key] = value
        else:
            result[key] = schema_semantics(value)
    return result


def schema_closure(schemas, references):
    pending = list(references)
    visited, result = set(), {}
    while pending:
        reference = pending.pop()
        name, fragment, original = resolve(schemas, reference)
        identity = (name, fragment)
        if identity in visited:
            continue
        visited.add(identity)
        if len(visited) > 4096:
            raise ValueError("Web schema closure exceeds its supported size")
        node = without_definitions(deepcopy(original))
        target = result.setdefault(name, {key: schemas[name][key] for key in ("$schema", "$id") if key in schemas[name]})
        if fragment:
            group, key = fragment.strip('/').split('/')
            target.setdefault(group, {})[key.replace('~1', '/').replace('~0', '~')] = node
        else:
            target.update(node)

        def inspect(value):
            if isinstance(value, dict):
                if any(key in value for key in ("$dynamicRef", "$recursiveRef", "$anchor", "$dynamicAnchor")):
                    raise ValueError("Web schemas require explicit offline references")
                if "$ref" in value:
                    document, part, _ = resolve(schemas, value["$ref"], name)
                    pending.append(document + ("#" + part if part else ""))
                for key, child in value.items():
                    if key not in ("const", "enum", "default", "examples"):
                        inspect(child)
            elif isinstance(value, list):
                for child in value:
                    inspect(child)
        inspect(node)
    return dict(sorted(result.items()))


def web_outputs(root, schemas, generator):
    roots = json.loads((root / "contracts/web-roots.json").read_bytes())
    if set(roots) != {"format", "version", "types", "runtime"} or \
            roots["format"] != "nexus.web.schema-roots" or roots["version"] != 1:
        raise ValueError("Unknown browser schema roots")
    for group in ("types", "runtime"):
        values = roots[group]
        if not isinstance(values, list) or not values or len(set(values)) != len(values):
            raise ValueError("Browser roots must be unique nonempty lists")
        for value in values:
            resolve(schemas, value)
    if any('#' in name for name in roots["types"]):
        raise ValueError("Browser wire roots must name complete message shapes")
    selected = schema_closure(schemas, roots["types"] + roots["runtime"])
    protocol = generator(selected, roots["types"])
    protocol.generate()
    header = "// <auto-generated from contracts/schemas by tooling/contract_codegen.py />\n"
    return {
        "apps/web/src/control/wire.generated.ts": header +
            "\n\n".join(body for _, body in sorted(protocol.typescript.items())) + "\n",
        "apps/web/src/control/schemas.generated.ts": header +
            "export default " + json.dumps(selected, ensure_ascii=False, indent=2) + ";\n",
    }
