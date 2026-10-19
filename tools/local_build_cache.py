"""Verified, exclusive component artifacts for local builds."""
from __future__ import annotations
import hashlib
import json
from pathlib import Path
import re
import time


def digest(path: Path) -> str:
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def guard(path: Path) -> None:
    for part in (path, *path.parents):
        if part.is_symlink() or part.is_junction():
            raise ValueError('Linked cache path: ' + str(part))


def inventory(root: Path) -> list:
    guard(root)
    result = []
    for file in sorted(root.rglob('*')):
        guard(file)
        if file.is_file():
            result.append([file.relative_to(root).as_posix(), file.stat().st_size, digest(file)])
    return sorted(result)


class ComponentCache:
    def __init__(self, root: Path, events: list):
        self.root = root.absolute()
        guard(self.root)
        self.root.mkdir(parents=True, exist_ok=True)
        self.events = events

    def prepare(self, component: str, inputs, build) -> Path:
        if not re.fullmatch(r'[A-Za-z0-9_-]+', component):
            raise ValueError('Invalid component')
        key = hashlib.sha256(json.dumps(inputs, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        directory = self.root / component / key[:16]
        guard(directory)
        directory.parent.mkdir(parents=True, exist_ok=True)
        lock = directory.with_suffix('.lock')
        started = time.monotonic()
        lock.open('x').close()
        try:
            receipt = directory / 'complete.json'
            output = directory / 'output'
            hit = directory.exists()
            if hit:
                guard(receipt)
                saved = json.loads(receipt.read_bytes())
                if saved['key'] != key or saved['files'] != inventory(output):
                    raise ValueError('Component cache changed: ' + component)
            else:
                directory.mkdir()
                build(output)
                files = inventory(output)
                if not files:
                    raise ValueError('Empty component: ' + component)
                receipt.write_text(json.dumps({'key': key, 'inputs': inputs, 'files': files}), encoding='utf-8')
            self.events.append({'component': component, 'cacheHit': hit, 'key': key,
                            'elapsedSeconds': time.monotonic() - started})
            print(f'[component] {component}: {"HIT" if hit else "BUILD"}', flush=True)
            return output
        finally:
            lock.unlink()
