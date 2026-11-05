"""Prepare pinned binary inputs for the existing production assemblers."""
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile


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


def maa_runtime(plugins, output):
    plugin = plugins / "plugins/managed/maa-framework"
    pin = json.loads((plugin / "runtime.release.json").read_bytes())
    request = urllib.request.Request("https://api.github.com/repos/" + pin["repository"] + "/releases/assets/" +
        str(pin["assetId"]), headers={"User-Agent": "NexusPipeline-CI", "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(request, timeout=60) as response:
        asset = json.loads(response.read(2 * 1024 * 1024))
    url = "https://github.com/" + pin["repository"] + "/releases/download/" + pin["tag"] + "/" + pin["assetName"]
    if asset["id"] != pin["assetId"] or asset["name"] != pin["assetName"] or \
            asset["browser_download_url"] != url or asset.get("digest") != "sha256:" + pin["assetSha256"]:
        raise ValueError("Official binary asset differs from its fixed release pin")
    archive = download(url, output / pin["assetName"], pin["assetSha256"], asset["size"])
    runtime = output / "runtime"
    declarations = json.loads((plugin / "extension.json").read_bytes())["managed"]["nativeLibraries"]
    files = []
    with zipfile.ZipFile(archive) as package:
        for declaration in declarations:
            name = Path(declaration).name
            if declaration != "runtime/win-x64/" + name or not name.endswith(".dll"):
                raise ValueError("Unknown runtime binary declaration")
            relative = "bin/" + name
            if package.namelist().count(relative) != 1:
                raise ValueError("Missing or duplicate pinned runtime binary")
            data = package.read(relative)
            target = runtime / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            files.append({"path": relative, "sizeBytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
    receipt = {"status": "VERIFIED_BINARY_INPUT_ONLY", "releaseId": pin["releaseId"], "tag": pin["tag"],
               "sourceCommit": pin["sourceCommit"], "asset": asset, "archive": str(archive),
               "runtime": str(runtime), "files": files}
    path = output / "runtime-receipt.json"
    path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    return path
