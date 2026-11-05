"""Keep public semantic compatibility separate from full source provenance."""
import hashlib
import json

from contract_exports import schema_semantics


def canonical(value):
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n").encode()


def digest(value):
    return hashlib.sha256(canonical(value)).hexdigest()


def api_descriptor(root, name, expected):
    value = json.loads((root / "contracts" / name).read_bytes())
    if set(value) != {"format", "version", "assembly", "declarations"} or \
            value["format"] != expected or value["version"] != 1 or not value["declarations"]:
        raise ValueError("Missing compiled public API descriptor")
    return value


def compatibility(root, schemas, ui):
    dotnet = api_descriptor(root, "sdk-public-api.json", "nexus.sdk.dotnet-public-api")
    frontend = api_descriptor(root, "sdk-frontend-api.json", "nexus.sdk.frontend-public-api")
    value = {"schemas": {name: schema_semantics(schema) for name, schema in schemas.items()},
             "ui": ui, "dotnetApi": dotnet, "frontendApi": frontend}
    return digest(value), value


def source_identity(root):
    files = [path for path in (root / "contracts").rglob("*.json")
             if "examples" not in path.parts and "sdk-public" not in path.parts and
             path.name not in ("fingerprint.json", "source-fingerprint.json")]
    files.extend(path for path in (root / "sdk/dotnet").rglob("*.cs") if not path.name.endswith(".g.cs"))
    files.extend(path for path in (root / "sdk/frontend/src").rglob("*.ts") if not path.name.endswith(".generated.ts"))
    files.extend(root / "tooling" / name for name in
                 ("contract_codegen.py", "contract_exports.py", "contract_identity.py", "web_schema.py", "ui_codegen.py",
                  "sdk_frontend_surface.mjs", "boundaries/Program.cs", "boundaries/PublicSdkSurface.cs", "boundaries/PublicSdkSignatures.cs"))
    files.extend(root / name for name in ("sdk/dotnet/NexusPipeline.Extensibility.csproj", "sdk/frontend/package.json",
                                          "sdk/frontend/tsconfig.json", "sdk/frontend/build.mjs", "Directory.Build.props"))
    records = []
    for path in sorted(files, key=lambda item: item.relative_to(root).as_posix().encode()):
        data = path.read_bytes()
        records.append({"path": path.relative_to(root).as_posix(), "sizeBytes": len(data),
                        "sha256": hashlib.sha256(data).hexdigest()})
    return {"algorithm": "nexus-sdk-source-manifest-sha256-v1", "sourceFingerprint": digest(records), "inputs": records}
