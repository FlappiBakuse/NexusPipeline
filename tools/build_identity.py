"""Canonical frozen inputs shared by Host and desktop payload readers."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re

MAX_IDENTITY_BYTES = 64 * 1024
INPUT_FIELDS = {
    "schemaVersion", "productVersion", "generation", "rid", "sourceSha",
    "sourceTreeSha", "partnerSha", "workflowSha", "frontendHash",
    "frontendPackageLockSha256", "desktopPackageLockSha256", "electronVersion",
    "electronArchiveSha256", "bootstrapProtocolVersion", "toolchain",
}
PROFILE_FIELDS = {'runtimeProfileId', 'runtimeProfileSha256', 'runtimeInventorySha256'}
TOOLCHAIN_FIELDS = {
    "dotnetSdkVersion", "dotnetRuntimeVersion", "nodeVersion", "npmVersion",
    "pythonVersion", "innoSetupVersion", "innoCompilerSha256", "innoDistributionSha256",
}
RECORD_FIELDS = {
    "schemaVersion", "product", "productVersion", "installationGeneration", "rid",
    "buildId", "frontendHash", "bootstrapProtocolVersion", "buildInputs",
}
VERSION = r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"


class BuildIdentityError(ValueError):
    pass


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise BuildIdentityError(message)


def _pairs(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        _require(key not in result, f"Duplicate key: {key}")
        result[key] = value
    return result


def _reject_number(value: str) -> None:
    raise BuildIdentityError(f"Non-integer value: {value}")


def _integer(value: str) -> int:
    _require(re.fullmatch(r"0|[1-9][0-9]*", value) is not None, "Invalid integer")
    _require(len(value) <= 16, "Integer exceeds the supported range")
    number = int(value)
    _require(number <= 9007199254740991, "Integer exceeds the supported range")
    return number


def parse_json(data: bytes) -> dict:
    _require(0 < len(data) <= MAX_IDENTITY_BYTES, "Identity exceeds the size limit")
    try:
        value = json.loads(data.decode("utf-8"), object_pairs_hook=_pairs,
                           parse_float=_reject_number, parse_constant=_reject_number, parse_int=_integer)
    except (UnicodeError, json.JSONDecodeError, RecursionError) as error:
        raise BuildIdentityError("Invalid identity JSON") from error
    _require(type(value) is dict, "Identity must be an object")
    canonical_json(value)
    return value


def canonical_json(value: dict) -> bytes:
    def validate(item: object, depth: int) -> None:
        _require(depth <= 4, "Identity nesting exceeds the limit")
        if type(item) is dict:
            for key, child in item.items():
                _require(type(key) is str and all(32 <= ord(c) <= 126 for c in key),
                         "Object keys must be printable ASCII")
                validate(child, depth + 1)
        elif type(item) is str:
            _require(all(32 <= ord(c) <= 126 for c in item), "Values must be printable ASCII")
        else:
            _require(type(item) is int and 0 <= item <= 9007199254740991,
                     "Values must be strings, objects or non-negative safe integers")
    _require(type(value) is dict, "Identity must be an object")
    validate(value, 0)
    data = json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    _require(len(data) <= MAX_IDENTITY_BYTES, "Identity exceeds the size limit")
    return data


def _fields(value: object, expected: set[str], name: str) -> dict:
    _require(type(value) is dict and set(value) == expected, f"Invalid {name} fields")
    return value


def _string(value: object, pattern: str, name: str) -> None:
    _require(type(value) is str and re.fullmatch(pattern, value) is not None,
             f"Invalid {name}")


def validate_inputs(inputs: dict) -> None:
    canonical_json(inputs)
    _fields(inputs, INPUT_FIELDS | (PROFILE_FIELDS if inputs.get('schemaVersion') == 2 else set()), "buildInputs")
    _require(type(inputs["schemaVersion"]) is int and inputs["schemaVersion"] in (1, 2),
             "Invalid schemaVersion")
    if inputs['schemaVersion'] == 2:
        _string(inputs['runtimeProfileId'], r'[a-z0-9][a-z0-9-]{0,63}', 'runtimeProfileId')
        for key in ('runtimeProfileSha256', 'runtimeInventorySha256'):
            _string(inputs[key], r'[0-9a-f]{64}', key)
    _require(type(inputs["bootstrapProtocolVersion"]) is int and inputs["bootstrapProtocolVersion"] == 1,
             "Invalid bootstrapProtocolVersion")
    _require(inputs["generation"] == "g0170" and inputs["rid"] == "win-x64", "Invalid target")
    _string(inputs["productVersion"], VERSION + r"(?:-(beta|rc)\.(0|[1-9][0-9]*))?", "productVersion")
    _require(all(len(part) <= 10 and int(part) <= 2147483647
                 for part in re.findall(r"[0-9]+", inputs["productVersion"])),
             "Product version component exceeds the supported range")
    _string(inputs["electronVersion"], VERSION, "electronVersion")
    for key in ("sourceSha", "sourceTreeSha", "partnerSha", "workflowSha"):
        _string(inputs[key], r"[0-9a-f]{40}", key)
    for key in ("frontendHash", "frontendPackageLockSha256", "desktopPackageLockSha256", "electronArchiveSha256"):
        _string(inputs[key], r"[0-9a-f]{64}", key)
    toolchain = _fields(inputs["toolchain"], TOOLCHAIN_FIELDS, "toolchain")
    for key, value in toolchain.items():
        _string(value, r"[0-9a-f]{64}" if key.endswith("Sha256") else r"[\x20-\x7e]+", key)


def build_id(inputs: dict) -> str:
    validate_inputs(inputs)
    return hashlib.sha256(canonical_json(inputs)).hexdigest()


def create_record(inputs: dict) -> dict:
    identity = build_id(inputs)
    return {
        "schemaVersion": 1, "product": "NexusPipeline", "productVersion": inputs["productVersion"],
        "installationGeneration": inputs["generation"], "rid": inputs["rid"], "buildId": identity,
        "frontendHash": inputs["frontendHash"], "bootstrapProtocolVersion": inputs["bootstrapProtocolVersion"],
        "buildInputs": json.loads(canonical_json(inputs)),
    }


def validate_record(record: dict) -> None:
    canonical_json(record)
    _fields(record, RECORD_FIELDS, "desktop-build")
    expected = create_record(record["buildInputs"])
    _require(record == expected, "Build identity or input projection mismatch")


def read_identity(path: Path) -> dict:
    with path.open("rb") as source:
        return parse_json(source.read(MAX_IDENTITY_BYTES + 1))


def main() -> int:
    parser = argparse.ArgumentParser(allow_abbrev=False)
    commands = parser.add_subparsers(dest="command", required=True)
    generate = commands.add_parser("generate", allow_abbrev=False)
    generate.add_argument("--inputs", type=Path, required=True)
    generate.add_argument("--output", type=Path, required=True)
    verify = commands.add_parser("verify", allow_abbrev=False)
    verify.add_argument("--record", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "generate":
            record = create_record(read_identity(args.inputs))
            data = canonical_json(record)
            args.output.parent.mkdir(parents=True, exist_ok=True)
            with args.output.open("xb") as output:
                output.write(data)
        else:
            record = read_identity(args.record)
            validate_record(record)
        print(record["buildId"])
        return 0
    except (BuildIdentityError, OSError) as error:
        parser.exit(1, f"Build identity failed: {error}\n")


if __name__ == "__main__":
    raise SystemExit(main())
