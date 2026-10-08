"""Freeze the actual tracked and untracked source bytes without creating a commit."""
from __future__ import annotations

import hashlib
from pathlib import Path
import re
import subprocess


def git(root: Path, *args: str) -> str:
    return subprocess.check_output(['git', '-C', str(root), *args], encoding='utf-8').rstrip('\r\n')


def full_sha(root: Path, value: str) -> str:
    if not re.fullmatch('[0-9a-f]{40}', value):
        raise ValueError('Expected a complete commit SHA')
    if git(root, 'rev-parse', '--verify', value + '^{commit}') != value:
        raise ValueError('Commit identity mismatch')
    return value


def changed_paths(root: Path, base: str, head: str) -> list[str]:
    full_sha(root, base)
    full_sha(root, head)
    ancestor = git(root, 'merge-base', base, head)
    return sorted(set(git(root, 'diff', '--name-only', '-z', '--no-renames', ancestor, head).split('\0')) - {''})


def source_entries(root: Path) -> list[tuple[str, str, bytes]]:
    stages = git(root, 'ls-files', '--stage', '-z').split('\0')
    modes = {}
    for stage in filter(None, stages):
        match = re.fullmatch(r'(100644|100755) [0-9a-f]{40} 0\t(.+)', stage, re.DOTALL)
        if not match:
            raise ValueError('Linked or unmerged source cannot be frozen')
        modes[match[2]] = match[1]
    names = sorted(set(modes) | set(filter(None, git(root, 'ls-files', '--others', '--exclude-standard', '-z').split('\0'))))
    entries = []
    for name in names:
        path = root / name
        if not path.exists():
            continue
        if path.resolve().is_relative_to(root.resolve()) is False or not path.is_file():
            raise ValueError('Unsafe source path')
        for parent in (path, *path.parents):
            if parent == root.parent:
                break
            if parent.is_symlink() or parent.is_junction():
                raise ValueError('Linked source input')
        entries.append((name, modes.get(name, '100644'), path.read_bytes()))
    if not entries:
        raise ValueError('Empty source input')
    return entries


def fingerprint(entries: list[tuple[str, str, bytes]]) -> str:
    digest = hashlib.sha256()
    for name, mode, raw in entries:
        digest.update(f'{mode} {name}\0{hashlib.sha256(raw).hexdigest()}\n'.encode())
    return digest.hexdigest()


def tree_sha(root: Path, entries: list[tuple[str, str, bytes]]) -> str:
    attributes = subprocess.check_output(['git', '-C', str(root), 'check-attr', '-z', '--stdin', 'text'],
                                        input=('\0'.join(item[0] for item in entries) + '\0').encode()).decode().split('\0')
    text = {attributes[index]: attributes[index + 2] for index in range(0, len(attributes) - 1, 3)}
    tree = {}
    def object_sha(kind, raw):
        return hashlib.sha1(f'{kind} {len(raw)}\0'.encode() + raw).digest()
    for name, mode, raw in entries:
        if text.get(name) == 'set' or text.get(name) == 'auto' and b'\0' not in raw:
            raw = raw.replace(b'\r\n', b'\n')
        parts = name.split('/')
        directory = tree
        for part in parts[:-1]:
            directory = directory.setdefault(part, {})
        directory[parts[-1]] = (mode, object_sha('blob', raw))
    def encode(directory):
        ordered = sorted(directory.items(), key=lambda item: (item[0] + ('/' if isinstance(item[1], dict) else '')).encode())
        data = b''.join((f'40000 {name}\0'.encode() + encode(value)) if isinstance(value, dict)
                        else f'{value[0]} {name}\0'.encode() + value[1] for name, value in ordered)
        return object_sha('tree', data)
    return encode(tree).hex()


def snapshot(root: Path, output: Path, entries: list[tuple[str, str, bytes]]) -> None:
    if output.exists() or output.is_symlink():
        raise ValueError('Source snapshot output already exists')
    output.mkdir(parents=True)
    for name, mode, raw in entries:
        target = output / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(raw)
        if mode == '100755':
            target.chmod(0o755)
