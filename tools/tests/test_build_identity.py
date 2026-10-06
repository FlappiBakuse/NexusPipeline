import copy
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from tools.build_identity import BuildIdentityError, build_id, canonical_json, create_record, parse_json, validate_record

FIXTURES = Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "build-identity"
EXPECTED_ID = "58e6649438efd8f699f8a04291cabcf3718b38721c7b970ef454537894896d71"


class BuildIdentityTests(unittest.TestCase):
    def setUp(self):
        self.inputs = parse_json((FIXTURES / "build-inputs.example.json").read_bytes())

    def test_shared_vector_and_record(self):
        self.assertEqual(canonical_json(self.inputs), (FIXTURES / "build-inputs.canonical.json").read_bytes())
        self.assertEqual(build_id(self.inputs), EXPECTED_ID)
        record = parse_json((FIXTURES / "desktop-build.example.json").read_bytes())
        validate_record(record)
        self.assertEqual(create_record(self.inputs), record)
        self.assertEqual(build_id(dict(reversed(list(self.inputs.items())))), EXPECTED_ID)
        self.assertEqual(canonical_json({"z": '/<>& " \\', "a": 1}), b'{"a":1,"z":"/<>& \\" \\\\"}')

    def test_each_frozen_input_changes_identity(self):
        for key in ("productVersion", "sourceSha", "sourceTreeSha", "partnerSha", "workflowSha", "frontendHash",
                    "frontendPackageLockSha256", "desktopPackageLockSha256", "electronVersion", "electronArchiveSha256"):
            changed = copy.deepcopy(self.inputs)
            changed[key] = "0.17.1" if key.endswith("Version") else "1" * (40 if key.endswith("Sha") else 64)
            self.assertNotEqual(build_id(changed), EXPECTED_ID, key)
        for key in self.inputs["toolchain"]:
            changed = copy.deepcopy(self.inputs)
            changed["toolchain"][key] = "1" * 64 if key.endswith("Sha256") else "24.0.1"
            self.assertNotEqual(build_id(changed), EXPECTED_ID, key)

    def test_invalid_json_types_and_duplicate_keys(self):
        for value in (b'{"a":1,"a":2}', b'{"a":{"b":1,"b":2}}', b'{"a":null}', b'{"a":true}',
                      b'{"a":[]}', b'{"a":-0}', b'{"a":1.0}', b'{"a":1e0}', b'{"a":9007199254740992}',
                      b'{"a":"\\n"}', b'{"a":"\\u4e2d"}', b'{"\\u4e2d":1}', b'\xef\xbb\xbf{}', b'{', b' ' * 65537):
            with self.subTest(value=value[:80]), self.assertRaises(BuildIdentityError):
                parse_json(value)
        with self.assertRaises(BuildIdentityError):
            parse_json(b'{"a":' + b'1' * 5000 + b'}')

    def test_invalid_fields_and_record_projections(self):
        for key in ("schemaVersion", "bootstrapProtocolVersion", "generation", "rid", "sourceSha", "productVersion", "electronVersion"):
            changed = copy.deepcopy(self.inputs)
            changed[key] = "invalid"
            with self.subTest(key=key), self.assertRaises(BuildIdentityError):
                build_id(changed)
        changed = copy.deepcopy(self.inputs)
        changed["unknown"] = "unused"
        with self.assertRaises(BuildIdentityError):
            build_id(changed)
        changed = copy.deepcopy(self.inputs)
        changed["productVersion"] = "1" * 5000 + ".0.0"
        with self.assertRaises(BuildIdentityError):
            build_id(changed)
        for key in ("product", "productVersion", "installationGeneration", "rid", "buildId", "frontendHash"):
            record = create_record(self.inputs)
            record[key] = "wrong"
            with self.subTest(key=key), self.assertRaises(BuildIdentityError):
                validate_record(record)

    def test_cli_preserves_outputs_and_rejects_oversized_inputs(self):
        tool = FIXTURES.parents[2] / "tools" / "build_identity.py"
        with tempfile.TemporaryDirectory(prefix="build-identity-") as directory:
            output = Path(directory) / "desktop-build.json"
            generate = [sys.executable, str(tool), "generate", "--inputs",
                        str(FIXTURES / "build-inputs.example.json"), "--output", str(output)]
            result = subprocess.run(generate, capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout.strip(), EXPECTED_ID)
            frozen = output.read_bytes()
            self.assertEqual(frozen, canonical_json(create_record(self.inputs)))
            verify = subprocess.run([sys.executable, str(tool), "verify", "--record", str(output)],
                                    capture_output=True, text=True, timeout=10)
            self.assertEqual(verify.returncode, 0, verify.stderr)
            overwrite = subprocess.run(generate, capture_output=True, text=True, timeout=10)
            self.assertEqual(overwrite.returncode, 1)
            self.assertEqual(output.read_bytes(), frozen)
            oversized = Path(directory) / "oversized.json"
            oversized.write_bytes(b" " * 65537)
            rejected = subprocess.run([sys.executable, str(tool), "generate", "--inputs", str(oversized),
                                       "--output", str(Path(directory) / "rejected.json")],
                                      capture_output=True, text=True, timeout=10)
            self.assertEqual(rejected.returncode, 1)
            self.assertFalse((Path(directory) / "rejected.json").exists())
