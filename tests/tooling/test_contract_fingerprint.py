import json
import os
from pathlib import Path
import shutil
import sys
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tooling"))
from contract_codegen import expected_outputs
from contract_exports import public_schemas


class ContractFingerprintTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(os.environ["NEXUS_TEST_ROOT"]) / ("fingerprint-" + uuid.uuid4().hex)
        for relative in ("contracts/schemas", "sdk/dotnet/Media", "sdk/frontend/src", "tooling/boundaries"):
            (self.root / relative).mkdir(parents=True)
        self.write("contracts/schemas/example.schema.json", {
            "$schema": "https://json-schema.org/draft/2020-12/schema",
            "$id": "https://schemas.nexuspipeline.invalid/1/example.schema.json",
            "type": "object", "properties": {"value": {"type": "string"},
                "shared": {"$ref": "internal.schema.json#/$defs/publicPart"},
                "outcome": {"$ref": "internal.schema.json#/properties/businessOutcome"}}, "required": ["value"]})
        self.write("contracts/schemas/internal.schema.json", {
            "$schema": "https://json-schema.org/draft/2020-12/schema",
            "$id": "https://schemas.nexuspipeline.invalid/1/internal.schema.json",
            "type": "object", "properties": {"privateValue": {"type": "string"},
                "businessOutcome": {"enum": ["succeeded", "failed"]}},
            "$defs": {"publicPart": {"type": "object", "properties": {"name": {"type": "string"}}}}})
        self.write("contracts/sdk-exports.json", {"format": "nexus.sdk.exports", "version": 1,
            "pluginRoots": ["example.schema.json"], "controlRoots": [], "internalRoots": ["internal.schema.json"],
            "sharedDocuments": [], "sharedFragments": ["internal.schema.json#/$defs/publicPart",
                "internal.schema.json#/properties/businessOutcome"]})
        self.write("contracts/web-roots.json", {"format": "nexus.web.schema-roots", "version": 1,
            "types": ["example.schema.json"], "runtime": ["example.schema.json"]})
        for name, kind in (("sdk-public-api.json", "dotnet"), ("sdk-frontend-api.json", "frontend")):
            self.write("contracts/" + name, {"format": "nexus.sdk." + kind + "-public-api", "version": 1,
                "assembly": "fixture-shape-only", "declarations": ["initial-declaration"]})
        source = Path(__file__).resolve().parents[2]
        for relative in ("contracts/ui-elements.json", "contracts/ui-types.json", "tooling/contract_codegen.py",
                         "tooling/contract_exports.py", "tooling/contract_identity.py", "tooling/web_schema.py", "tooling/ui_codegen.py",
                         "tooling/sdk_frontend_surface.mjs", "tooling/boundaries/Program.cs",
                         "tooling/boundaries/PublicSdkSurface.cs", "tooling/boundaries/PublicSdkSignatures.cs",
                         "sdk/dotnet/NexusPipeline.Extensibility.csproj", "sdk/frontend/package.json",
                         "sdk/frontend/tsconfig.json", "sdk/frontend/build.mjs", "Directory.Build.props"):
            target = self.root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source / relative, target)
        for relative in json.loads((source / "contracts/ui-elements.json").read_bytes())["elements"]:
            target = self.root / "contracts" / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source / "contracts" / relative, target)

    def write(self, relative, value):
        (self.root / relative).write_text(json.dumps(value), encoding="utf-8")

    def identities(self):
        outputs = expected_outputs(self.root)
        return (json.loads(outputs["contracts/fingerprint.json"])["contractFingerprint"],
                json.loads(outputs["contracts/source-fingerprint.json"])["sourceFingerprint"])

    def test_invalidates_handshake(self):
        """public sdk changes invalidate the handshake without a version bump."""
        before = self.identities()[0]
        descriptor = self.root / "contracts/sdk-public-api.json"
        api = json.loads(descriptor.read_bytes())
        api["declarations"].append("public-method:System.String Added(Int32)")
        self.write("contracts/sdk-public-api.json", api)
        dotnet_changed = self.identities()[0]
        self.assertNotEqual(before, dotnet_changed)
        api = json.loads((self.root / "contracts/sdk-frontend-api.json").read_bytes())
        api["declarations"].append("export interface Added { value: string; }")
        self.write("contracts/sdk-frontend-api.json", api)
        frontend_changed = self.identities()[0]
        self.assertNotEqual(dotnet_changed, frontend_changed)
        element = json.loads((self.root / "contracts/ui-elements/nexus-button.json").read_bytes())
        element["events"]["contract-test"] = "string"
        self.write("contracts/ui-elements/nexus-button.json", element)
        self.assertNotEqual(frontend_changed, self.identities()[0])
        outputs = expected_outputs(self.root)
        self.assertNotIn("sdk/dotnet/Protocol/Internal.g.cs", outputs)
        self.assertIn("sdk/dotnet/Protocol/InternalPublicPart.g.cs", outputs)
        mirror = json.loads(outputs["contracts/sdk-public/schemas/internal.schema.json"])
        self.assertEqual({"businessOutcome"}, set(mirror["properties"]))
        self.assertEqual(["succeeded", "failed"], mirror["properties"]["businessOutcome"]["enum"])
        self.assertEqual({}, mirror["not"])
        self.assertEqual({"publicPart"}, set(mirror["$defs"]))
        self.assertIn("src/platform/Serialization/Protocol/Internal.g.cs", outputs)

    def test_separates_identity(self):
        """generation is stable and internal or nonsemantic inputs change only source identity."""
        before = self.identities()
        for relative, text in expected_outputs(self.root).items():
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8", newline="\n")
        self.assertEqual(before, self.identities())
        with (self.root / "tooling/contract_codegen.py").open("a", encoding="utf-8") as output:
            output.write("# no generated behavior changes\n")
        current = self.identities()
        self.assertEqual(before[0], current[0]); self.assertNotEqual(before[1], current[1])
        with (self.root / "sdk/frontend/build.mjs").open("a", encoding="utf-8") as output:
            output.write("// build provenance only\\n")
        after_build = self.identities()
        self.assertEqual(current[0], after_build[0]); self.assertNotEqual(current[1], after_build[1])
        current = after_build
        internal = json.loads((self.root / "contracts/schemas/internal.schema.json").read_bytes())
        internal["properties"]["privateValue"]["maxLength"] = 37
        self.write("contracts/schemas/internal.schema.json", internal)
        after_internal = self.identities()
        self.assertEqual(current[0], after_internal[0]); self.assertNotEqual(current[1], after_internal[1])
        public = json.loads((self.root / "contracts/schemas/example.schema.json").read_bytes())
        public["description"] = "Author explanation only"
        self.write("contracts/schemas/example.schema.json", public)
        after_comment = self.identities()
        self.assertEqual(after_internal[0], after_comment[0]); self.assertNotEqual(after_internal[1], after_comment[1])
        public["properties"]["value"]["maxLength"] = 12
        self.write("contracts/schemas/example.schema.json", public)
        self.assertNotEqual(after_comment[0], self.identities()[0])

    def test_preserves_document(self):
        schemas = {path.name: json.loads(path.read_bytes()) for path in (self.root / "contracts/schemas").glob("*.json")}
        public = schemas["example.schema.json"]
        properties = public["properties"]
        properties["metadata"] = {"$ref": "internal.schema.json"}
        schemas["internal.schema.json"]["required"] = ["businessOutcome"]
        exports = json.loads((self.root / "contracts/sdk-exports.json").read_bytes())
        exports["sharedFragments"].append("internal.schema.json")
        results = []
        for order in (list(properties), list(reversed(properties))):
            public["properties"] = {key: properties[key] for key in order}
            result = public_schemas(schemas, exports)["internal.schema.json"]
            with self.subTest(order=order):
                self.assertNotIn("not", result)
                self.assertEqual("object", result["type"])
                self.assertEqual(["businessOutcome"], result["required"])
                self.assertEqual(schemas["internal.schema.json"]["properties"], result["properties"])
                self.assertEqual({"publicPart"}, set(result["$defs"]))
            results.append(result)
        self.assertEqual(results[0], results[1])
