import copy
import hashlib
import json
import unittest
from json_parts import load_json_input


class JsonPartsTests(unittest.TestCase):
    def setUp(self):
        self.manifest = {"schemaVersion": 1, "partitionVersion": 1, "parts": [
            {"path": "tests/policy/backend.json", "section": "groups"}]}
        self.files = {"tests/policy/backend.json": b'{"backend":{"caseIds":["Fixture.Runs"]}}\n'}

    def read(self, name):
        if name == "tests/policy.json":
            return json.dumps(self.manifest).encode()
        return self.files[name]

    def test_complete_input_identity_changes_even_when_only_part_whitespace_changes(self):
        before = load_json_input("tests/policy.json", self.read)
        self.files["tests/policy/backend.json"] += b"\n"
        after = load_json_input("tests/policy.json", self.read)
        self.assertEqual(before["value"], after["value"])
        self.assertNotEqual(before["digest"], after["digest"])
        self.assertEqual(after["value"]["groups"]["backend"]["caseIds"], ["Fixture.Runs"])
        inline = b'{"schemaVersion":1}\r\n'
        self.assertEqual(load_json_input("tests/policy.json", lambda _: inline)["digest"],
                         hashlib.sha256(inline.replace(b"\r\n", b"\n")).hexdigest())

    def test_invalid_duplicate_missing_or_escaping_parts_never_fall_back(self):
        original = copy.deepcopy(self.manifest)
        for change in [lambda m: m.update(partitionVersion=True), lambda m: m.update(partitionVersion=2),
                       lambda m: m["parts"].append(m["parts"][0]),
                       lambda m: m["parts"][0].update(path="tests/policy/../outside.json"),
                       lambda m: m["parts"][0].update(path="tests/policy/missing.json"),
                       lambda m: m["parts"][0].update(section="unknown")]:
            self.manifest = copy.deepcopy(original)
            change(self.manifest)
            with self.assertRaises((ValueError, KeyError)):
                load_json_input("tests/policy.json", self.read)

    def test_topic_order_requires_every_unique_identity(self):
        files = {
            "docs/map.json": json.dumps({"schemaVersion": 1, "partitionVersion": 1, "parts": [
                {"path": "docs/map/topics.json", "section": "topics"}], "order": {"topics": ["second", "first"]}}).encode(),
            "docs/map/topics.json": b'[{"id":"first"},{"id":"second"}]',
        }
        result = load_json_input("docs/map.json", files.__getitem__)
        self.assertEqual([item["id"] for item in result["value"]["topics"]], ["second", "first"])
        files["docs/map/topics.json"] = b'[{"id":"first"},{"id":"first"}]'
        with self.assertRaises(ValueError):
            load_json_input("docs/map.json", files.__getitem__)
