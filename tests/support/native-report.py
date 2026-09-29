"""Convert native framework results without treating a zero exit code as evidence."""
from __future__ import annotations

import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def read_report(kind: str, source: Path) -> dict:
    if kind == "trx":
        root = ET.parse(source).getroot()
        cases = [node.attrib for node in root.iter() if node.tag.rsplit("}", 1)[-1] == "UnitTestResult"]
        counters = [node.attrib for node in root.iter() if node.tag.rsplit("}", 1)[-1] == "Counters"]
        if len(counters) != 1:
            raise ValueError("TRX counters missing or ambiguous")
        result = {"caseIds": [case["testName"] for case in cases],
                  "passed": sum(case.get("outcome") == "Passed" for case in cases),
                  "failed": sum(case.get("outcome") == "Failed" for case in cases),
                  "skipped": sum(case.get("outcome") not in {"Passed", "Failed"} for case in cases)}
        count = counters[0]
        if int(count["total"]) != len(cases) or int(count["executed"]) != len(cases) \
                or int(count["passed"]) != result["passed"] or int(count["failed"]) != result["failed"] \
                or int(count.get("notExecuted", "0")):
            raise ValueError("TRX native counters disagree")
    elif kind == "vitest":
        value = json.loads(source.read_text(encoding="utf-8"))
        cases = [case for suite in value["testResults"] for case in suite["assertionResults"]]
        result = {"caseIds": [case["fullName"] for case in cases],
                  "passed": sum(case["status"] == "passed" for case in cases),
                  "failed": sum(case["status"] == "failed" for case in cases),
                  "skipped": sum(case["status"] not in {"passed", "failed"} for case in cases)}
        if value["numTotalTests"] != len(cases) or value["numPassedTests"] != result["passed"] \
                or value["numFailedTests"] != result["failed"] or value.get("numPendingTests", 0) \
                or value.get("numTodoTests", 0) or not value["success"]:
            raise ValueError("Vitest native counters disagree or test failed")
    else:
        raise ValueError("Unknown native report kind")
    if not cases or len(set(result["caseIds"])) != len(cases) or result["failed"] or result["skipped"]:
        raise ValueError("Empty, duplicate, failed or skipped native cases")
    return result


if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit("Usage: native-report.py trx|vitest <input> <output>")
    Path(sys.argv[3]).write_text(json.dumps(read_report(sys.argv[1], Path(sys.argv[2])), ensure_ascii=False), encoding="utf-8")
