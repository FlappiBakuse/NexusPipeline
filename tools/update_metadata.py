from __future__ import annotations

import argparse
import json
import re
import subprocess
from pathlib import Path

POLICY_CANONICAL = "metadata/updates/legacy-policy.json"
POLICY_PROJECTION = "update-policy.json"


def validate_legacy_projection(root: Path, base: str | None = None) -> None:
    canonical = root / POLICY_CANONICAL
    projection = root / POLICY_PROJECTION
    for file in (canonical, projection):
        if not file.is_file() or file.is_symlink():
            raise ValueError(f"Legacy policy must be an ordinary file: {file}")
    if canonical.read_bytes() != projection.read_bytes():
        raise ValueError("Legacy policy canonical and root projection differ")
    policy = json.loads(canonical.read_text(encoding="utf-8-sig"))
    if (not isinstance(policy, dict) or type(policy.get("schemaVersion")) is not int
            or policy["schemaVersion"] != 1 or policy.get("repository") != "FlappiBakuse/NexusPipeline"
            or not isinstance(policy.get("barriers"), list)):
        raise ValueError("Invalid legacy policy envelope")
    if base:
        previous = subprocess.run(['git', 'show', f'{base}:{POLICY_CANONICAL}'], cwd=root,
                                  capture_output=True, check=False)
        if previous.returncode:
            previous = subprocess.run(['git', 'show', f'{base}:{POLICY_PROJECTION}'], cwd=root,
                                      capture_output=True, check=True)
        original = json.loads(previous.stdout.decode('utf-8-sig'))['barriers']
        if policy['barriers'][:len(original)] != original:
            raise ValueError('Published legacy barriers must remain an unchanged prefix')


def validate_channels(root: Path, version: str | None = None) -> None:
    path = root / 'metadata/updates/channels.json'
    if not path.is_file() or path.is_symlink() or path.stat().st_size > 2 * 1024 * 1024:
        raise ValueError('Missing/linked/oversized update channel declaration')
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError('Duplicate update channel field')
            result[key] = value
        return result
    data = json.loads(path.read_text(encoding='utf-8-sig'), object_pairs_hook=unique)
    def fields(value, names):
        if not isinstance(value, dict) or set(value) != set(names):
            raise ValueError('Unknown/missing update channel field')
    fields(data, ('schemaVersion', 'channels', 'bridges'))
    if type(data['schemaVersion']) is not int or data['schemaVersion'] != 1:
        raise ValueError('Unsupported discovery protocol')
    fields(data['channels'], ('stable', 'prerelease'))
    def capabilities(value):
        if (not isinstance(value, list) or len(value) > 16
                or any(not isinstance(item, str) or not 1 <= len(item) <= 256 for item in value)
                or len(set(value)) != len(value)):
            raise ValueError('Invalid discovery capabilities')
    def protocol(value):
        if type(value) is not int or not 1 <= value <= 2**31 - 1:
            raise ValueError('Invalid discovery protocol')
    def target_fields(target):
        fields(target, ('releaseTag', 'manifestAssetName', 'readerProtocol', 'requiredCapabilities'))
        tag = target['releaseTag']
        if (not isinstance(tag, str) or len(tag) > 256
                or not re.fullmatch(r'v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:beta|rc)\.(?:0|[1-9]\d*))?', tag)):
            raise ValueError('Invalid discovery Release tag')
        if target['manifestAssetName'] != f'NexusPipeline-{tag}-win-x64-update.json':
            raise ValueError('Discovery manifest name does not match its Release')
        protocol(target['readerProtocol'])
        capabilities(target['requiredCapabilities'])
        return tag
    bridges = data['bridges']
    if not isinstance(bridges, list) or len(bridges) > 8:
        raise ValueError('Invalid discovery bridges')
    bridge_tags = set()
    for bridge in bridges:
        fields(bridge, ('sourceReaderProtocol', 'sourceCapabilities', 'targetReaderProtocol', 'targetCapabilities', 'entryTarget'))
        protocol(bridge['sourceReaderProtocol'])
        protocol(bridge['targetReaderProtocol'])
        capabilities(bridge['sourceCapabilities'])
        capabilities(bridge['targetCapabilities'])
        tag = target_fields(bridge['entryTarget'])
        if tag in bridge_tags:
            raise ValueError('Duplicate discovery bridge target')
        bridge_tags.add(tag)
    pending_tags = []
    for name, channel in data['channels'].items():
        fields(channel, ('previousPublishedTarget', 'pendingTarget'))
        for slot, target in channel.items():
            if target is None:
                continue
            tag = target_fields(target)
            if name == 'stable' and (tag.startswith('v0.') or '-' in tag):
                raise ValueError('Prerelease target in stable channel')
            if slot == 'pendingTarget':
                pending_tags.append(tag)
    if version and f'v{version}' not in pending_tags:
        raise ValueError('Current product version is not declared as a pending target')


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("check", "generate"))
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--base')
    args = parser.parse_args()
    root = args.root.resolve()
    if args.mode == "generate":
        canonical = root / POLICY_CANONICAL
        projection = root / POLICY_PROJECTION
        if canonical.is_symlink() or not canonical.is_file() or projection.is_symlink():
            raise ValueError("Linked/missing legacy policy")
        projection.write_bytes(canonical.read_bytes())
    validate_legacy_projection(root, args.base)
    validate_channels(root)
    print("Update metadata and legacy projection: PASS")


if __name__ == "__main__":
    main()
