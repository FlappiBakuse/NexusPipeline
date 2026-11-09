import base64
from copy import deepcopy
import json

from ci_fixture import GitFixture, HOST
from legacy_updates import CANONICAL, PROJECTION, REPOSITORY, channels, check, parse, published_policy


class LegacyUpdateTests(GitFixture):
    def setUp(self):
        super().setUp()
        self.current = json.loads((HOST / CANONICAL).read_bytes())
        previous = deepcopy(self.current)
        previous["barriers"] = previous["barriers"][:-1]
        self.write(CANONICAL, self.encode(previous))
        self.base_commit = self.commit()
        self.save_policy(self.current)
        for name in ("metadata/updates/channels.json", "metadata/updates/v2/channels.json", "docs/users/install-018.md"):
            self.write(name, (HOST / name).read_bytes())

    def encode(self, value):
        return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8")

    def save_policy(self, value):
        for name in (CANONICAL, PROJECTION):
            self.write(name, self.encode(value))

    def test_accepts_current(self):
        self.assertEqual("PASS", check(self.repo, self.base_commit)["status"])
        old = json.loads(self.git("show", self.base_commit + ":" + CANONICAL))
        self.assertEqual(old["barriers"], self.current["barriers"][:-1])
        self.assertEqual("0.18.0", self.current["barriers"][-1]["version"])

    def test_rejects_history(self):
        for fault in ("changed", "removed", "reordered", "guide", "duplicate"):
            altered = deepcopy(self.current)
            if fault == "changed": altered["barriers"][0]["code"] = "changed"
            if fault == "removed": altered["barriers"].pop(0)
            if fault == "reordered": altered["barriers"].reverse()
            if fault == "guide": altered["barriers"][-1]["migrationUrl"] = "https://example.invalid/tool"
            if fault == "duplicate": altered["barriers"].append(altered["barriers"][-1])
            self.save_policy(altered)
            before = (self.repo / PROJECTION).read_bytes()
            with self.subTest(fault=fault), self.assertRaises(ValueError):
                check(self.repo, self.base_commit, generate=True)
            self.assertEqual(before, (self.repo / PROJECTION).read_bytes())

    def test_rejects_targets(self):
        path = "metadata/updates/v2/channels.json"
        original = json.loads((self.repo / path).read_bytes())
        for fault in ("name", "capabilities", "hint", "schema", "current"):
            altered = deepcopy(original)
            pending = altered["channels"]["prerelease"]["pendingTarget"]
            if fault == "name": pending["assets"]["setup"] = "other.exe"
            if fault == "capabilities": pending["requiredCapabilities"] = []
            if fault == "hint": pending["basePayloadManifestSha256"] = "../unknown"
            if fault == "schema": altered["schemaVersion"] = True
            if fault == "current": altered["channels"]["prerelease"]["pendingTarget"] = None
            with self.subTest(fault=fault), self.assertRaises(ValueError):
                channels(self.encode(altered), 2, "0.18.0")
        with self.assertRaises(ValueError):
            parse(b'{"schemaVersion":1,"schemaVersion":2}')

    def test_preserves_projection(self):
        self.write(PROJECTION, b"original projection\n")
        with self.assertRaises(ValueError):
            check(self.repo, self.base_commit)
        self.assertEqual(b"original projection\n", (self.repo / PROJECTION).read_bytes())
        check(self.repo, self.base_commit, generate=True)
        self.assertEqual((self.repo / CANONICAL).read_bytes(), (self.repo / PROJECTION).read_bytes())
        (self.repo / "docs/users/install-018.md").unlink()
        with self.assertRaises(ValueError):
            check(self.repo, self.base_commit)

    def test_requires_readback(self):
        commit = "a" * 40
        replies = {f"repos/{REPOSITORY}/commits/main": {"sha": commit}}
        for name in (CANONICAL, PROJECTION):
            replies[f"repos/{REPOSITORY}/contents/{name}?ref={commit}"] = {
                "type": "file", "path": name, "encoding": "base64",
                "content": base64.b64encode(self.encode(self.current)).decode()}
        def fetch(path):
            return replies[path]

        result = published_policy(fetch)
        self.assertEqual(commit, result["sourceCommit"])
        self.assertEqual("0.18.0", result["barrierVersion"])
        old = deepcopy(self.current)
        old["barriers"].pop()
        replies[f"repos/{REPOSITORY}/contents/{CANONICAL}?ref={commit}"]["content"] = base64.b64encode(self.encode(old)).decode()
        with self.assertRaises(ValueError):
            published_policy(fetch)
