"""Run Host Python gates with native case identities and strict result counts."""
import argparse
import json
from pathlib import Path
import sys
import unittest


class NativeResult(unittest.TextTestResult):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.case_ids = []

    def startTest(self, test):
        self.case_ids.append(test.id())
        super().startTest(test)


def main():
    parser = argparse.ArgumentParser(allow_abbrev=False)
    parser.add_argument("--suite", choices=["ci", "release", "installer"], required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    if args.report.exists():
        parser.error("Native report already exists")
    root = Path(__file__).resolve().parents[2]
    sys.path.insert(0, str(root))
    loader = unittest.TestLoader()
    suite = loader.discover(str(root / "tests"), pattern="test_*.py") if args.suite == "ci" else loader.loadTestsFromNames(
        ["tools.tests.test_host_release", "tools.tests.test_host_candidate_source"] if args.suite == "release"
        else ["tools.tests.test_host_installer"])
    result = unittest.TextTestRunner(stream=sys.stdout, verbosity=2, resultclass=NativeResult).run(suite)
    counts = {"testsRun": result.testsRun, "failures": len(result.failures), "errors": len(result.errors),
              "skipped": len(result.skipped), "unexpectedSuccesses": len(result.unexpectedSuccesses),
              "expectedFailures": len(result.expectedFailures)}
    passed = result.wasSuccessful() and counts["testsRun"] > 0 and not any(
        value for key, value in counts.items() if key != "testsRun") and len(set(result.case_ids)) == counts["testsRun"]
    report = {"schemaVersion": 1, "suite": args.suite, "status": "PASS" if passed else "FAIL",
              "counts": counts, "caseIds": result.case_ids}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    with args.report.open("x", encoding="utf-8") as output:
        output.write(json.dumps(report, indent=2) + "\n")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
