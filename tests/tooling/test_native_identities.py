import json
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tooling"))
from test_expectations import load_expected_methods
from test_runner import run_tests


class NativeIdentityTests(unittest.TestCase):
    def test_exact_identities(self):
        build = Path(os.environ["NEXUS_TEST_BUILD_ROOT"]).resolve(strict=True)
        source = Path(os.environ["NEXUS_TEST_SOURCE_ROOT"]).resolve(strict=True)
        expected = load_expected_methods(ROOT, "core")
        methods = ["NexusPipeline.CoreTests.ProviderPolicyTests.RejectsInvalidInput",
                   "NexusPipeline.CoreTests.TargetCommandTests.RejectsInvalidInput"]
        self.assertEqual(1, len({method.rsplit(".", 1)[1] for method in methods}))
        parent = Path(os.environ["NEXUS_TEST_ROOT"]).resolve(strict=True)
        output = parent.parent / "native-identities"
        output.mkdir()
        for index, method in enumerate(methods):
            target = output / str(index)
            with patch.dict(os.environ):
                code = run_tests(source, build, target, "core", method=method)
            self.assertEqual(0, code)
            reports = list(target.glob("*/test-receipt.json"))
            self.assertEqual(1, len(reports))
            result = json.loads(reports[0].read_bytes())
            self.assertEqual("PASS", result["status"])
            self.assertEqual({method: expected[method]}, result["actualMethods"])
            self.assertEqual("declared-method-only", result["coverage"])
            self.assertTrue(result["timings"]["reusedBuild"])
            self.assertEqual(expected[method], len(set(result["actualCaseIds"])))
        records = [json.loads(path.read_bytes()) for path in output.glob("*/*/test-receipt.json")]
        self.assertEqual(2, len(records))
        self.assertNotEqual(records[0]["actualMethods"], records[1]["actualMethods"])
        with self.assertRaises(ValueError):
            run_tests(source, build, output / "duplicate", "core", method=[methods[0], methods[0]])
        with patch.dict(os.environ):
            self.assertEqual(0, run_tests(source, build, output / "batch", "core", method=methods))
        batch = list((output / "batch").glob("*/test-receipt.json"))
        self.assertEqual(1, len(batch))
        result = json.loads(batch[0].read_bytes())
        self.assertEqual({name: expected[name] for name in methods}, result["actualMethods"])
        self.assertEqual("declared-methods-only", result["coverage"])
        self.assertEqual(sum(expected[name] for name in methods), len(result["actualCaseIds"]))


if __name__ == "__main__":
    unittest.main()
