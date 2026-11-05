"""Validate the published discovery contract and append-only installation barriers."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import re
from urllib.parse import urlsplit

from desktop_bundle import require_no_links
from git_source import git

CANONICAL = "metadata/updates/legacy-policy.json"
PROJECTION = "update-policy.json"
REPOSITORY = "FlappiBakuse/NexusPipeline"
GUIDE = "https://github.com/FlappiBakuse/NexusPipeline/blob/main/docs/users/install-018.md"
CAPABILITIES = ["payload-manifest-v2", "single-delta-v1", "transaction-recovery-v1"]


def parse(data):
    if not 0 < len(data) <= 2 * 1024 * 1024:
        raise ValueError("Update discovery metadata exceeds its byte budget")

    def unique(pairs):
        result = {}
        for name, value in pairs:
            if name in result:
                raise ValueError("Duplicate update metadata field")
            result[name] = value
        return result

    return json.loads(data.decode("utf-8-sig"), object_pairs_hook=unique)


def fields(value, expected):
    if type(value) is not dict or set(value) != set(expected):
        raise ValueError("Unknown or missing update discovery field")


def version_key(value):
    if type(value) is not str or len(value) > 64 or not re.fullmatch(
            r"(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:beta|rc)\.(?:0|[1-9]\d*))?", value):
        raise ValueError("Invalid update version")
    main, _, suffix = value.partition("-")
    stage, _, number = suffix.partition(".")
    return (*map(int, main.split(".")), {"beta": 0, "rc": 1, "": 2}[stage], int(number or 0))


def policy(data, previous=None):
    value = parse(data)
    fields(value, ("schemaVersion", "repository", "barriers"))
    if type(value["schemaVersion"]) is not int or value["schemaVersion"] != 1 or value["repository"] != REPOSITORY or \
            type(value["barriers"]) is not list or not 1 <= len(value["barriers"]) <= 256:
        raise ValueError("Invalid legacy policy envelope")
    last = None
    for entry in value["barriers"]:
        fields(entry, ("version", "code", "migrationUrl"))
        current = version_key(entry["version"])
        uri = urlsplit(entry["migrationUrl"])
        if last is not None and current <= last or not re.fullmatch(r"[a-z0-9][a-z0-9._-]{0,79}", entry["code"]) or \
                uri.scheme != "https" or not uri.hostname or uri.username or uri.password:
            raise ValueError("Invalid or unordered installation barrier")
        last = current
    if previous is not None:
        original = parse(previous)["barriers"]
        if value["barriers"][:len(original)] != original:
            raise ValueError("Published barriers must remain an unchanged prefix")
    barrier = next((entry for entry in value["barriers"] if entry["version"] == "0.18.0"), None)
    if barrier is None or barrier["migrationUrl"] != GUIDE:
        raise ValueError("The 0.18 new-instance installation barrier is required")
    return value


def target(value, schema):
    names = ("releaseTag", "readerProtocol", "requiredCapabilities", "assets", "basePayloadManifestSha256") \
        if schema == 2 else ("releaseTag", "readerProtocol", "requiredCapabilities", "manifestAssetName")
    fields(value, names)
    tag = value["releaseTag"]
    if type(tag) is not str or not tag.startswith("v"):
        raise ValueError("Invalid discovery release tag")
    version_key(tag[1:])
    if type(value["readerProtocol"]) is not int or value["readerProtocol"] < 1 or \
            type(value["requiredCapabilities"]) is not list or len(value["requiredCapabilities"]) > 16 or \
            any(type(item) is not str or not 1 <= len(item) <= 256 for item in value["requiredCapabilities"]) or \
            len(set(value["requiredCapabilities"])) != len(value["requiredCapabilities"]):
        raise ValueError("Invalid discovery reader capabilities")
    if schema == 2:
        expected = {kind: f"NexusPipeline-{tag}-win-x64-{suffix}" for kind, suffix in
                    (("setup", "Setup.exe"), ("portable", "Portable.zip"), ("update", "Update.zip"))}
        hint = value["basePayloadManifestSha256"]
        if value["assets"] != expected or value["readerProtocol"] != 2 or value["requiredCapabilities"] != CAPABILITIES or \
                hint is not None and (type(hint) is not str or not re.fullmatch("[0-9a-f]{64}", hint)):
            raise ValueError("Invalid three-asset discovery target")
    elif value["manifestAssetName"] != f"NexusPipeline-{tag}-win-x64-update.json":
        raise ValueError("Invalid historical manifest asset")
    return tag


def channels(data, schema, current=None):
    value = parse(data)
    fields(value, ("schemaVersion", "channels") if schema == 2 else ("schemaVersion", "channels", "bridges"))
    if type(value["schemaVersion"]) is not int or value["schemaVersion"] != schema:
        raise ValueError("Invalid discovery schema")
    fields(value["channels"], ("stable", "prerelease"))
    pending = []
    for name, channel in value["channels"].items():
        fields(channel, ("previousPublishedTarget", "pendingTarget"))
        for slot, item in channel.items():
            if item is None:
                continue
            tag = target(item, schema)
            if name == "stable" and (tag.startswith("v0.") or "-" in tag):
                raise ValueError("A prerelease cannot enter the stable Host channel")
            if slot == "pendingTarget":
                pending.append(tag)
    bridges = value.get("bridges", [])
    if type(bridges) is not list or len(bridges) > 8:
        raise ValueError("Invalid historical discovery bridges")
    seen = set()
    for bridge in bridges:
        fields(bridge, ("sourceReaderProtocol", "sourceCapabilities", "targetReaderProtocol", "targetCapabilities", "entryTarget"))
        for side in ("source", "target"):
            capabilities = bridge[side + "Capabilities"]
            if type(bridge[side + "ReaderProtocol"]) is not int or bridge[side + "ReaderProtocol"] < 1 or \
                    type(capabilities) is not list or len(capabilities) > 16 or \
                    any(type(item) is not str or not 1 <= len(item) <= 256 for item in capabilities) or len(set(capabilities)) != len(capabilities):
                raise ValueError("Invalid historical bridge capabilities")
        tag = target(bridge["entryTarget"], 1)
        if tag in seen:
            raise ValueError("Duplicate historical bridge target")
        seen.add(tag)
    if current is not None and "v" + current not in pending:
        raise ValueError("The current Host is missing its pending discovery target")
    return value


def check(root, base="origin/main", generate=False):
    for name in (CANONICAL, PROJECTION, "metadata/updates/channels.json", "metadata/updates/v2/channels.json", "docs/users/install-018.md"):
        require_no_links(root / name)
        if not (root / name).is_file():
            raise ValueError("Required published discovery input is missing: " + name)
    original = git(root, "show", base + ":" + CANONICAL) if base else None
    data = (root / CANONICAL).read_bytes()
    policy(data, original)
    channels((root / "metadata/updates/channels.json").read_bytes(), 1)
    channels((root / "metadata/updates/v2/channels.json").read_bytes(), 2, "0.18.0")
    if generate:
        (root / PROJECTION).write_bytes(data)
    elif (root / PROJECTION).read_bytes() != data:
        raise ValueError("Legacy canonical and fixed root projection differ")
    return {"status": "PASS", "scope": "Local discovery and append-only barrier contract; official readback is separate"}


def published_policy(fetch):
    commit = fetch(f"repos/{REPOSITORY}/commits/main")["sha"]
    if not re.fullmatch("[0-9a-f]{40}", commit):
        raise ValueError("Official policy main did not resolve to a fixed commit")
    result = {}
    for name in (CANONICAL, PROJECTION):
        item = fetch(f"repos/{REPOSITORY}/contents/{name}?ref={commit}")
        if item.get("type") != "file" or item.get("path") != name or item.get("encoding") != "base64":
            raise ValueError("Official policy content identity differs")
        data = base64.b64decode(item["content"], validate=False)
        policy(data)
        result[name] = data
    if result[CANONICAL] != result[PROJECTION]:
        raise ValueError("Official policy projection differs")
    return {"sourceCommit": commit, "sha256": hashlib.sha256(result[CANONICAL]).hexdigest(), "barrierVersion": "0.18.0"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("check", "generate"))
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--base", default="origin/main")
    args = parser.parse_args()
    root = args.root.resolve(strict=True)
    print(json.dumps(check(root, args.base, args.command == "generate")))


if __name__ == "__main__":
    main()
