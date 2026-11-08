"""Prepare pinned binary inputs for the existing production assemblers."""
import hashlib
import json
from pathlib import Path
import urllib.request


def download(url, target, digest, size=None):
    target.parent.mkdir(parents=True, exist_ok=True)
    if not target.exists():
        request = urllib.request.Request(url, headers={"User-Agent": "NexusPipeline-CI"})
        with urllib.request.urlopen(request, timeout=120) as response, target.open("xb") as stream:
            total = 0
            while block := response.read(1024 * 1024):
                total += len(block)
                if total > (size if size is not None else 1024 * 1024 * 1024):
                    raise ValueError("Pinned binary input exceeds its size bound")
                stream.write(block)
    with target.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha256").hexdigest()
    if actual != digest or size is not None and target.stat().st_size != size:
        raise ValueError("Pinned binary input digest or size differs")
    return target


def desktop_archive(host, output):
    pin = json.loads((host / "apps/desktop/runtime-lock.json").read_bytes())
    return download(pin["url"], output / "electron.zip", pin["sha256"], pin["sizeBytes"])
