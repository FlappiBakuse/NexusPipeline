"""Synthetic store packages built from the current Host fixture assembly."""
import base64
import hashlib
import io
import json
from pathlib import Path
import sys
import zipfile

assembly, output, run_id = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3]
manifest = {
    "schemaVersion": 2, "name": "store-fixture", "artifactName": "StoreFixture",
    "displayName": "Store fixture", "description": "Synthetic installation fixture",
    "version": "0.1.0", "kind": "managed-code", "apiVersion": "1.8",
    "minHostVersion": "0.16.11", "capabilities": [],
    "entryAssembly": "NexusPipeline.TestPlugin.dll", "entryType": "NexusPipeline.TestPlugin.TestPlugin",
}
buffer = io.BytesIO()
with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
    for name, data in [("plugin.json", json.dumps(manifest).encode()), (assembly.name, assembly.read_bytes())]:
        info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
        archive.writestr(info, data)
package = buffer.getvalue()
prefix = "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/"
entry = {key: value for key, value in manifest.items() if key not in {"entryAssembly", "entryType", "schemaVersion"}}
entry.update(packageUrl=prefix + "packages/StoreFixture/StoreFixture-0.1.0.zip",
             sha256=hashlib.sha256(package).hexdigest(), sizeBytes=len(package),
             changelog=[{"version": "0.1.0", "date": "2026-09-29", "items": ["Synthetic fixture"]}])
bad = dict(entry, name="broken-fixture", artifactName="BrokenFixture", sha256="0" * 64,
           packageUrl=prefix + "packages/BrokenFixture/BrokenFixture-0.1.0.zip")
catalog = {"schemaVersion": 2, "repository": "FlappiBakuse/NexusPipeline-Plugins",
           "generatedAt": "2026-09-29T00:00:00Z", "plugins": [entry, bad]}

def exchange(identity, url, body, content_type):
    return {"id": identity, "method": "GET", "url": url, "status": 200,
            "bodyBase64": base64.b64encode(body).decode(), "contentType": content_type}

plan = {"runId": run_id, "exchanges": [
    exchange("catalog", prefix + "catalog.json", json.dumps(catalog).encode(), "application/json"),
    exchange("bad-hash", bad["packageUrl"], package, "application/zip"),
    exchange("install", entry["packageUrl"], package, "application/zip"),
]}
if "--upgrade" in sys.argv[4:]:
    upgraded = dict(manifest, version="0.1.1")
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, data in [("plugin.json", json.dumps(upgraded).encode()), (assembly.name, assembly.read_bytes())]:
            archive.writestr(zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0)), data)
    upgrade_package = buffer.getvalue()
    next_entry = dict(entry, version="0.1.1", packageUrl=prefix + "packages/StoreFixture/StoreFixture-0.1.1.zip",
                      sha256=hashlib.sha256(upgrade_package).hexdigest(), sizeBytes=len(upgrade_package),
                      changelog=[{"version": "0.1.1", "date": "2026-09-29", "items": ["Synthetic update"]}])
    plan["exchanges"] += [
        exchange("catalog-update", prefix + "catalog.json", json.dumps(dict(catalog, plugins=[next_entry, bad])).encode(), "application/json"),
        exchange("update", next_entry["packageUrl"], upgrade_package, "application/zip"),
    ]
with output.open("x", encoding="utf8") as file:
    json.dump(plan, file)
print("Prepared synthetic catalog and actual fixture DLL package")
