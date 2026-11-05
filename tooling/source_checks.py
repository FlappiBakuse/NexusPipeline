"""Check file boundaries and text hygiene without claiming product behavior."""
from pathlib import Path
import json

from source_snapshot import source_files
from contract_codegen import expected_outputs
from source_names import check_names

LIMITS = {
    ".cs": 450, ".ts": 350, ".js": 350, ".mjs": 350,
    ".vue": 300, ".css": 350, ".scss": 350,
    ".py": 400, ".json": 350, ".yaml": 350, ".yml": 350, ".md": 400,
}


def check_source(root, records=None):
    records = source_files(root) if records is None else records
    errors = []
    registry = json.loads((root / "tooling/generated_sources.json").read_text(encoding="utf-8"))
    generated = expected_outputs(root)
    errors.extend(check_names(root, records, generated))
    exemptions = set()
    for entry in registry["files"]:
        if entry["path"] == "apps/web/package-lock.json" and entry["generator"] == "npm" and entry["verifyCommand"] == ["python", "-B", "tooling/web_build.py", "--check-lock"]:
            from web_build import verify_lock
            verify_lock(root)
            exemptions.add(entry["path"])
            continue
        if entry["generator"] != "tooling/contract_codegen.py" or entry["verifyCommand"] != ["python", "-B", "tooling/contract_codegen.py", "--check"]:
            raise ValueError("Unrecognized generated source verifier")
        relative = entry["path"]
        if relative not in generated or (root / relative).read_bytes() != generated[relative].encode("utf-8"):
            raise ValueError("Generated source exemption does not match its authoritative generator")
        exemptions.add(relative)
    for record in records:
        path = Path(record["path"])
        if path.suffix not in LIMITS:
            continue
        data = (root / path).read_bytes()
        try:
            text = data.decode("utf-8")
        except UnicodeDecodeError:
            errors.append(f"{path}: not UTF-8")
            continue
        if text.startswith("\ufeff") or "\r" in text or "\0" in text:
            errors.append(f"{path}: expected UTF-8 without BOM, LF, no NUL")
        if text and not text.endswith("\n"):
            errors.append(f"{path}: missing final LF")
        limit = 500 if "tests" in path.parts else LIMITS[path.suffix]
        if len(text.splitlines()) > limit and path.as_posix() not in exemptions:
            errors.append(f"{path}: exceeds {limit} physical lines")
    for error in errors:
        print(error, flush=True)
    print(f"source check: {len(records)} files, {len(errors)} errors", flush=True)
    return 1 if errors else 0
