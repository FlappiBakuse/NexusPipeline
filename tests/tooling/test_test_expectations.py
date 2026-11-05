import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tooling"))
from test_expectations import load_expected_methods


class TestExpectationTests(unittest.TestCase):
    def setUp(self):
        configured = os.environ.get("NEXUS_TEST_ROOT")
        if not configured:
            self.fail("NEXUS_TEST_ROOT must specify an external, owned test root")
        self.scope = tempfile.TemporaryDirectory(prefix="test-policies-", dir=Path(configured).resolve(strict=True))
        self.root = Path(self.scope.name)
        self.policies = self.root / "tests" / "policy"
        self.policies.mkdir(parents=True)

    def tearDown(self):
        self.scope.cleanup()

    def write(self, name, methods=None, includes=None, **extra):
        policy = {"group": name, "expectedMethods": methods or {}, **extra}
        if includes is not None:
            policy["includes"] = includes
        (self.policies / (name + ".json")).write_text(json.dumps(policy), encoding="utf-8")

    def test_composes_counts(self):
        """independent suites compose exact native case counts."""
        self.write("all", {"Fixture.Tests.BaseCase": 2}, ["execution", "settings"])
        self.write("execution", {"Fixture.Tests.ActualRootExit": 3})
        self.write("settings", {"Other.Tests.BaseCase": 1})
        self.assertEqual({"Fixture.Tests.BaseCase": 2, "Fixture.Tests.ActualRootExit": 3, "Other.Tests.BaseCase": 1}, load_expected_methods(self.root, "all"))

    def test_rejects_overlap(self):
        """overlapping ownership cannot be hidden by equal counts."""
        self.write("all", {"Fixture.Tests.SharedCase": 2}, ["execution"])
        for count in (1, 2, 3):
            with self.subTest(count=count):
                self.write("execution", {"Fixture.Tests.SharedCase": count})
                with self.assertRaises(ValueError):
                    load_expected_methods(self.root, "all")

    def test_rejects_cycles(self):
        """cycles repeated includes and path escape cannot expand a suite."""
        self.write("execution", {"Fixture.Tests.RealCase": 1}, ["all"])
        for includes in (["execution"], ["execution", "execution"], ["../outside"], ["C:/outside"]):
            with self.subTest(includes=includes):
                self.write("all", includes=includes)
                with self.assertRaises(ValueError):
                    load_expected_methods(self.root, "all")

    def test_rejects_declarations(self):
        """empty unknown or invalid case declarations fail closed."""
        for policy in ({"group": "all"}, {"group": "other", "expectedMethods": {"Fixture.Tests.Case": 1}},
                       {"group": "all", "expectedMethods": {"Fixture.Tests.Case": True}}, {"group": "all", "expectedMethods": {"Fixture.Tests.Case": 0}},
                       {"group": "all", "expectedMethods": {"Case": 1}},
                       {"group": "all", "expectedMethods": {"Fixture.Tests.Case": 1}, "unknown": 1}):
            with self.subTest(policy=policy):
                (self.policies / "all.json").write_text(json.dumps(policy), encoding="utf-8")
                with self.assertRaises(ValueError):
                    load_expected_methods(self.root, "all")
