from copy import deepcopy
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tooling"))
from contract_codegen import ProtocolGenerator
from contract_exports import resolve
from web_schema import schema_closure, web_outputs


class WebSchemaTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="web-schema-", dir=os.environ["NEXUS_TEST_ROOT"])
        self.root = Path(self.directory.name)
        (self.root / "contracts").mkdir()
        (self.root / "contracts/web-roots.json").write_text(json.dumps({
            "format": "nexus.web.schema-roots", "version": 1,
            "types": ["screen.schema.json"], "runtime": ["shared.schema.json#/$defs/choice"]}), encoding="utf-8")
        self.schemas = {
            "screen.schema.json": {"type": "object", "properties": {
                "name": {"type": "string"}, "shared": {"$ref": "shared.schema.json#/$defs/item"}}},
            "shared.schema.json": {"$defs": {
                "item": {"type": "object", "properties": {"choice": {"$ref": "#/$defs/choice"}}},
                "choice": {"type": "string", "enum": ["one", "two"]},
                "unused": {"type": "object", "properties": {"private": {"type": "string"}}}}},
            "internal.schema.json": {"type": "object", "properties": {"journal": {"type": "string"}}},
        }

    def tearDown(self):
        self.directory.cleanup()

    def outputs(self):
        return web_outputs(self.root, self.schemas, ProtocolGenerator)

    def test_keeps_closure(self):
        selected = schema_closure(self.schemas, ["screen.schema.json", "shared.schema.json#/$defs/choice"])
        self.assertEqual({"screen.schema.json", "shared.schema.json"}, set(selected))
        self.assertEqual({"item", "choice"}, set(selected["shared.schema.json"]["$defs"]))
        self.assertEqual(self.schemas["shared.schema.json"]["$defs"]["item"],
                         resolve(selected, "shared.schema.json#/$defs/item")[2])
        generated = self.outputs()
        self.assertIn("readonly shared?: SharedItem", generated["apps/web/src/control/wire.generated.ts"])
        self.assertNotIn("SharedUnused", generated["apps/web/src/control/wire.generated.ts"])

    def test_ignores_internal(self):
        before = self.outputs()
        self.schemas["internal.schema.json"]["properties"]["journal"]["type"] = "integer"
        self.schemas["shared.schema.json"]["$defs"]["unused"]["properties"]["private"]["type"] = "number"
        self.assertEqual(before, self.outputs())

    def test_tracks_dependency(self):
        before = self.outputs()
        self.schemas["shared.schema.json"]["$defs"]["choice"]["enum"].append("three")
        after = self.outputs()
        self.assertNotEqual(before["apps/web/src/control/schemas.generated.ts"], after["apps/web/src/control/schemas.generated.ts"])
        self.assertNotEqual(before["apps/web/src/control/wire.generated.ts"], after["apps/web/src/control/wire.generated.ts"])

    def test_rejects_missing(self):
        self.schemas["screen.schema.json"]["properties"]["shared"]["$ref"] = "missing.schema.json"
        with self.assertRaises(ValueError):
            self.outputs()

    def test_rejects_dynamic(self):
        self.schemas["shared.schema.json"]["$defs"]["choice"]["$dynamicRef"] = "#choice"
        with self.assertRaises(ValueError):
            self.outputs()

    def test_keeps_determinism(self):
        original = deepcopy(self.schemas)
        first = self.outputs()
        self.schemas = dict(reversed(list(self.schemas.items())))
        self.assertEqual(first, self.outputs())
        self.assertEqual(original, self.schemas)


if __name__ == "__main__":
    unittest.main()
