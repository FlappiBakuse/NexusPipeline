import os
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

from test_runner import run_dotnet


class NativeReportTests(unittest.TestCase):
    def test_rejects_incomplete(self):
        root = Path(os.environ["NEXUS_TEST_SOURCE_ROOT"])
        build = Path(os.environ["NEXUS_TEST_BUILD_ROOT"])
        parent = Path(os.environ["NEXUS_TEST_ROOT"]) / "native-reports"
        methods = ["NexusPipeline.CoreTests.ProviderPolicyTests.RejectsInvalidInput",
                   "NexusPipeline.CoreTests.TargetCommandTests.RejectsInvalidInput"]
        expected = dict(zip(methods, [2, 4]))
        for variant in ("complete", "missing-report", "missing-case", "duplicate", "aborted", "failed-exit", "skipped", "missing-summary"):
            with self.subTest(variant=variant):
                output = parent / variant
                output.mkdir(parents=True)

                def launch(*args, **kwargs):
                    if variant != "missing-report":
                        self.report(output / "core.trx", expected, variant)
                    return SimpleNamespace(returncode=1 if variant == "failed-exit" else 0, stdout="", stderr="")

                with patch("test_runner.native_test_launcher", return_value=["fixture"]), patch("test_runner.subprocess.run", side_effect=launch):
                    result = run_dotnet(root, build, output, "core", method=methods)
                self.assertEqual("PASS" if variant == "complete" else "FAIL", result["status"])

    @staticmethod
    def report(path, methods, variant):
        ns = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        root = ET.Element(ns + "TestRun")
        results = ET.SubElement(root, ns + "Results")
        definitions = ET.SubElement(root, ns + "TestDefinitions")
        for method, count in methods.items():
            for index in range(count):
                if variant == "missing-case" and index == count - 1:
                    continue
                identity = method + str(index if variant != "duplicate" else 0)
                result = ET.SubElement(results, ns + "UnitTestResult", testId=identity, testName=identity,
                                       outcome="NotExecuted" if variant == "skipped" else "Passed")
                definition = ET.SubElement(definitions, ns + "UnitTest", id=identity)
                classname, name = method.rsplit(".", 1)
                ET.SubElement(definition, ns + "TestMethod", className=classname, name=name)
        if variant != "missing-summary":
            summary = ET.SubElement(root, ns + "ResultSummary", outcome="Aborted" if variant == "aborted" else "Completed")
            ET.SubElement(summary, ns + "Counters", total="6", executed="6", passed="6", aborted="1" if variant == "aborted" else "0")
        ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
