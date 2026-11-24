"""Check file boundaries and text hygiene without claiming product behavior."""
from pathlib import Path
import ast
import json

from source_snapshot import digest, source_files
from contract_codegen import expected_outputs
TEXT_SUFFIXES = {
    ".cs", ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs",
    ".vue", ".css", ".scss", ".py", ".json", ".jsonc", ".yaml", ".yml", ".md",
    ".sql", ".html", ".xml", ".csproj", ".props", ".targets", ".slnx", ".iss", ".isl",
    ".ps1", ".cmd", ".bat", ".sh", ".toml", ".config", ".manifest", ".txt", ".svg",
}
TEXT_NAMES = {".editorconfig", ".gitattributes", ".gitignore", "LICENSE"}
LEGACY_SCHEMA_ROOT = Path("tests/fixtures/legacy-state")


def check_source(root, records=None):
    records = source_files(root) if records is None else records
    errors = []
    registry = json.loads((root / "tooling/generated_sources.json").read_text(encoding="utf-8"))
    generated = expected_outputs(root)
    for entry in registry["files"]:
        if entry["path"] == "apps/desktop/browser-login-limits.generated.cjs" and \
                entry["generator"] == "tooling/browser_login_contract.py" and \
                entry["verifyCommand"] == ["python", "-B", "tooling/browser_login_contract.py", "--check"]:
            from browser_login_contract import generate
            if (root / entry["path"]).read_bytes() != generate(root):
                raise ValueError("Desktop login limits differ from the Core owner")
            continue
        if entry["path"] == "apps/web/package-lock.json" and entry["generator"] == "npm" and entry["verifyCommand"] == ["python", "-B", "tooling/web_build.py", "--check-lock"]:
            from web_build import verify_lock
            verify_lock(root)
            continue
        if entry["generator"] != "tooling/contract_codegen.py" or entry["verifyCommand"] != ["python", "-B", "tooling/contract_codegen.py", "--check"]:
            raise ValueError("Unrecognized generated source verifier")
        relative = entry["path"]
        if relative not in generated or (root / relative).read_bytes() != generated[relative].encode("utf-8"):
            raise ValueError("Generated source does not match its authoritative generator")
    schemas = json.loads((root / LEGACY_SCHEMA_ROOT / "provenance.json").read_bytes())["schemas"]
    legacy = {LEGACY_SCHEMA_ROOT / (name + ".sql"): checksum for name, checksum in schemas.items()}
    for record in records:
        path = Path(record["path"])
        if path.suffix.lower() not in TEXT_SUFFIXES and path.name not in TEXT_NAMES:
            continue
        data = (root / path).read_bytes()
        if path in legacy:
            if digest(data) != legacy[path]:
                errors.append(f"{path}: archived SQL differs from its provenance")
            continue
        try:
            text = data.decode("utf-8")
        except UnicodeDecodeError:
            errors.append(f"{path}: not UTF-8")
            continue
        if text.startswith("\ufeff") or "\r" in text or "\0" in text:
            errors.append(f"{path}: expected UTF-8 without BOM, LF, no NUL")
        if text and not text.endswith("\n"):
            errors.append(f"{path}: missing final LF")
        if path.suffix == ".py":
            try:
                ast.parse(text, filename=str(path))
            except SyntaxError as error:
                errors.append(f"{path}:{error.lineno}: {error.msg}")
    for error in errors:
        print(error, flush=True)
    print(f"source check: {len(records)} files, {len(errors)} errors", flush=True)
    return 1 if errors else 0
