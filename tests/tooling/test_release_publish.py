import base64
from copy import deepcopy
import hashlib
import json
import shutil
import urllib.error
from unittest.mock import patch
import zipfile

from ci_fixture import GitFixture, HOST
from legacy_updates import CANONICAL, PROJECTION
from release_candidate import extract, inventory, member_path, parse
from release_publish import publish_host


class ReleaseService:
    def __init__(self, source, policy):
        self.source = source
        self.policy = policy
        self.repository = "FlappiBakuse/NexusPipeline"
        self.prefix = "repos/" + self.repository
        self.release = None
        self.records = {}
        self.bytes = {}
        self.calls = []
        self.fault = None

    def fetch(self, path):
        self.calls.append(("GET", path))
        if path == self.prefix + "/git/ref/tags/v0.18.0":
            return {"object": {"type": "commit", "sha": self.source}}
        if path == self.prefix + "/commits/main":
            return {"sha": self.source}
        for name in (CANONICAL, PROJECTION):
            if path == self.prefix + "/contents/" + name + "?ref=" + self.source:
                return {"type": "file", "path": name, "encoding": "base64",
                        "content": base64.b64encode(self.policy).decode()}
        if path.startswith(self.prefix + "/releases/tags/"):
            if self.release is None:
                raise urllib.error.HTTPError("https://api.github.com/", 404, "Not found", {}, None)
            return deepcopy(self.release)
        if path.endswith("/assets?per_page=100&page=1"):
            return deepcopy(list(self.records.values()))
        if "/releases/assets/" in path:
            return deepcopy(self.records[int(path.rsplit("/", 1)[1])])
        if self.release and path == self.prefix + "/releases/21":
            return deepcopy(self.release)
        raise AssertionError(path)

    def request(self, path, method, data):
        self.calls.append((method, path))
        if method == "POST":
            self.release = {"id": 21, **data}
        elif method == "PATCH":
            self.release.update(data)
        else:
            raise AssertionError(method)
        return deepcopy(self.release)

    def upload(self, repository, release_id, path, name):
        self.calls.append(("UPLOAD", name))
        identity = 51 + len(self.records)
        data = path.read_bytes()
        record = {"id": identity, "name": name, "state": "uploaded", "size": len(data),
                  "digest": "sha256:" + hashlib.sha256(data).hexdigest()}
        if self.fault:
            record[self.fault[0]] = self.fault[1]
        self.records[identity] = record
        self.bytes[identity] = data
        return deepcopy(record)

    def download(self, path, destination, size, digest):
        identity = int(path.rsplit("/", 1)[1])
        self.calls.append(("DOWNLOAD", identity))
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(self.bytes[identity])
        return destination


class ReleasePublishTests(GitFixture):
    def setUp(self):
        super().setUp()
        self.directory = self.scope / "candidate"
        self.directory.mkdir()
        base = "NexusPipeline-v0.18.0-win-x64-"
        (self.directory / (base + "Setup.exe")).write_bytes(b"MZ Controlled fixture, not executable")
        for suffix in ("Portable.zip", "Update.zip"):
            with zipfile.ZipFile(self.directory / (base + suffix), "x") as archive:
                for name in ("NexusPipeline.exe", "NexusPipeline.update.exe", "README.md",
                             "resources/payload-manifest.json", "resources/desktop/NexusPipeline.Launcher.exe", "package-info/recipe.json"):
                    archive.writestr(name, b"Controlled format fixture")
        files = inventory(self.directory)
        self.verified = {"candidate": {"kind": "host", "files": list(files.values()),
            "release": {"tag": "v0.18.0", "assets": sorted(files)}},
            "provenance": {"repository": "FlappiBakuse/NexusPipeline"}, "source": {"sourceCommit": "a" * 40}}
        self.api = ReleaseService("a" * 40, (HOST / CANONICAL).read_bytes())
        self.output = self.scope / "readback"

    def publish(self, write=True):
        with patch("subprocess.run", side_effect=AssertionError("Publisher must not execute source or producers")):
            return publish_host(self.api, self.directory, self.verified, self.output, write)

    def test_preserves_readonly(self):
        result = self.publish(False)
        self.assertFalse(result["remoteWritten"])
        self.assertEqual("VERIFIED_NOT_PUBLISHED", result["status"])
        self.assertTrue(all(method == "GET" for method, _ in self.api.calls))

    def test_reads_assets(self):
        result = self.publish()
        self.assertEqual("PUBLISHED", result["status"])
        self.assertEqual(3, len(result["assets"]))
        methods = [method for method, _ in self.api.calls]
        self.assertEqual(3, methods.count("UPLOAD"))
        self.assertGreater(methods.index("PATCH"), max(index for index, method in enumerate(methods) if method == "DOWNLOAD"))
        self.output = self.scope / "idempotent-readback"
        uploads = methods.count("UPLOAD")
        self.assertEqual("PUBLISHED", self.publish()["status"])
        self.assertEqual(uploads, [method for method, _ in self.api.calls].count("UPLOAD"))

    def test_rejects_server(self):
        for field, value in (("state", "new"), ("size", 0), ("digest", "sha256:" + "0" * 64), ("name", "other.exe")):
            self.api = ReleaseService("a" * 40, (HOST / CANONICAL).read_bytes())
            self.api.fault = (field, value)
            self.output = self.scope / field
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.publish()
            self.assertNotIn("PATCH", [method for method, _ in self.api.calls])

    def test_rejects_overwrite(self):
        self.publish()
        self.output = self.scope / "overwrite"
        identity = next(iter(self.api.bytes))
        self.api.bytes[identity] += b"different"
        self.api.records[identity]["size"] = len(self.api.bytes[identity])
        previous = len([method for method, _ in self.api.calls if method == "PATCH"])
        with self.assertRaises(ValueError):
            self.publish()
        self.assertEqual(previous, len([method for method, _ in self.api.calls if method == "PATCH"]))

    def test_rejects_identity(self):
        self.api.source = "b" * 40
        with self.assertRaises(ValueError):
            self.publish()
        self.assertTrue(all(method == "GET" for method, _ in self.api.calls))
        self.api.source = "a" * 40
        self.publish()
        duplicate = deepcopy(next(iter(self.api.records.values())))
        duplicate["id"] = 90
        self.api.records[90] = duplicate
        with self.assertRaises(ValueError):
            self.publish()

    def test_rejects_members(self):
        for name in ("../escape", "C:/escape", "folder\\escape", "CON.txt", "dir./file", "file "):
            with self.subTest(name=name), self.assertRaises(ValueError):
                member_path(name)
        with self.assertRaises(ValueError):
            parse(b'{"version":1,"version":2}')
        archive = self.scope / "collision.zip"
        with zipfile.ZipFile(archive, "x") as package:
            package.writestr("file.txt", b"one")
            package.writestr("FILE.txt", b"two")
        with self.assertRaises(ValueError):
            extract(archive, self.scope / "collision")
        self.assertFalse((self.scope / "collision").exists())
        archive = self.scope / "valid.zip"
        with zipfile.ZipFile(archive, "x") as package:
            package.writestr("folder/file.txt", b"exact original bytes")
        destination = extract(archive, self.scope / "valid")
        self.assertEqual(b"exact original bytes", (destination / "folder/file.txt").read_bytes())
