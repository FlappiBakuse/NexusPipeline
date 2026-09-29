"""Validate current-attempt reports and completed Actions jobs without running product code."""
import hashlib
import json
import os
from pathlib import Path
import sys
import urllib.request
from datetime import datetime

root, reports = Path(sys.argv[1]), Path(sys.argv[2])
kind = sys.argv[3]
policy_bytes = (root / "tests/policy.json").read_bytes()
if policy_bytes.startswith(b"\xef\xbb\xbf"):
    raise ValueError("Policy has a UTF-8 BOM")
policy_bytes = policy_bytes.replace(b"\r\n", b"\n")
policy = json.loads(policy_bytes)
attempt = os.environ["GITHUB_RUN_ATTEMPT"]
run = os.environ["GITHUB_RUN_ID"]
sha = os.environ["GITHUB_SHA"]

def require(value, message):
    if not value:
        raise ValueError(message)

def same(actual, expected):
    return isinstance(actual, list) and len(actual) == len(set(actual)) and sorted(actual) == sorted(expected)

def api(route):
    request = urllib.request.Request(os.environ["GITHUB_API_URL"] + route,
        headers={"Authorization": "Bearer " + os.environ["GH_TOKEN"], "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(request, timeout=8) as response:
        return json.load(response)

def all_jobs():
    route = f"/repos/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{run}/attempts/{attempt}/jobs"
    first = api(route + "?per_page=100&page=1")
    total = first["total_count"]
    require(isinstance(total, int) and 0 <= total <= 2000, "Unexpected job count")
    jobs = list(first["jobs"])
    for page in range(2, (total + 99) // 100 + 1):
        jobs.extend(api(route + f"?per_page=100&page={page}")["jobs"])
    require(len(jobs) == total and len({job["id"] for job in jobs}) == total, "Incomplete job pagination")
    return jobs

needs = json.loads(os.environ["CI_NEEDS"])
if kind == "plugins" and needs.get("plugin_core", {}).get("result") == "skipped":
    scope = json.loads((reports / "scope/scope.json").read_bytes())
    require(scope["selected"] == [] and scope["reason"] == "documentation-only changes", "Unexpected skipped matrix")
    needs.pop("plugin_core")
require(all(value["result"] == "success" for value in needs.values()), "Required predecessor failed, cancelled or skipped")
expected_jobs = []
if kind == "host":
    expected_jobs = ["Host / 执行与配置", "Host / 前端核心状态"]
    for group in ("backend", "frontend"):
        files = list(reports.glob(f"**/{group}/summary.json"))
        require(len(files) == 1, f"Missing or duplicate {group} report")
        report = json.loads(files[0].read_bytes())
        expected = policy["groups"][group]
        require(report["runId"] == f"{run}-{attempt}-{group}", "Foreign run/attempt")
        require(report["source"]["commitSha"] == sha and not report["source"]["workingTreeDirty"], "Foreign or dirty source")
        require(report["policySha256"] == hashlib.sha256(policy_bytes).hexdigest(), "Foreign policy")
        require(report["status"] == "PASS" and report["exitCode"] == 0, "Failed report")
        require(same(report["scope"]["completedCaseIds"], expected["caseIds"]), "Missing cases")
        require(same(report["scope"]["completedScenarioIds"], expected["scenarioIds"]), "Missing scenarios")
        require(report["counts"] == {"passed": len(expected["caseIds"]), "failed": 0, "skipped": 0}, "Invalid counts")
        native = json.loads((files[0].parent / "native-counts.json").read_bytes())
        require(same(native["caseIds"], expected["caseIds"]) and native["passed"] == len(expected["caseIds"])
                and native["failed"] == 0 and native["skipped"] == 0, "Invalid native counts")
        require((files[0].parent / ("native.trx" if group == "backend" else "native.json")).stat().st_size > 0, "Missing native report")
        require(report["cleanup"]["status"] == "complete" and report["cleanup"]["remainingOwnedProcessCount"] == 0, "Incomplete cleanup")
        require(0 <= report["timing"]["elapsedMs"] <= policy["qualificationMs"], "Local qualification exceeded")
else:
    scope = json.loads((reports / "scope/scope.json").read_bytes())
    require(scope["sha"] == sha and scope["run"] == run and scope["attempt"] == attempt, "Foreign scope")
    expected_jobs = ["Plugins / 范围与装配契约"]
    for name in scope["selected"]:
        expected_jobs.append(f"Plugins / 核心功能 · {name}")
        report = json.loads((reports / name / "summary.json").read_bytes())
        require(report["runId"] == f"{run}-{attempt}-{name}", "Foreign run/attempt")
        require(report["source"]["commitSha"] == sha and not report["source"]["workingTreeDirty"], "Foreign source")
        require(report["partner"]["commitSha"] == scope["hostSha"] and not report["partner"]["workingTreeDirty"], "Foreign Host input")
        require(report["policySha256"] == hashlib.sha256(policy_bytes).hexdigest(), "Foreign policy")
        require(report["status"] == "PASS" and report["exitCode"] == 0 and report["cleanup"]["cleanupComplete"], "Failed invocation")
        require(0 <= report["elapsedMs"] <= policy["qualificationMs"], "Invocation qualification exceeded")
        require(len(report["plugins"]) == 1, "Unexpected plugin selection")
        item = report["plugins"][0]
        expected = policy["plugins"][name]
        require(item["plugin"] == name and item["status"] == "PASS" and item["exitCode"] == 0, "Failed plugin")
        require(item["cleanup"] == "complete" and 0 <= item["exclusivePluginMs"] <= policy["qualificationMs"], "Plugin budget or cleanup failure")
        require(same(item["completedCaseIds"], item["expectedCaseIds"]) and len(item["completedCaseIds"]) > 0, "Missing cases")
        require(item["counts"] == {"passed": len(item["completedCaseIds"]), "failed": 0, "skipped": 0}, "Invalid counts")
        require(same(item["completedScenarioIds"], item["expectedScenarioIds"]), "Missing scenarios")
        require(item["profile"] == expected["defaultProfile"], "Unexpected CI profile")
        require(item["completedScenarioIds"] == [expected["realScenario"]], "Wrong capability")
        if expected["kind"] == "data-specialized":
            require(same(item["completedCaseIds"], expected["fixtureIds"]), "Wrong adapter trajectories")
            for fixture in expected["fixtureIds"]:
                native = json.loads((reports / name / name / (fixture + ".json")).read_bytes())
                require(native["completedCaseIds"] == [fixture] and native["passed"] == 1
                        and native["failed"] == 0 and native["skipped"] == 0, "Invalid native trajectory")
        else:
            require(sorted(set(case.split("(")[0] for case in item["completedCaseIds"])) == sorted(expected["expectedMethods"]), "Wrong component methods")
            native = json.loads((reports / name / name / "native.json").read_bytes())
            require(same(native["caseIds"], item["completedCaseIds"]) and native["passed"] == item["counts"]["passed"]
                    and native["failed"] == 0 and native["skipped"] == 0, "Invalid native component report")
            require((reports / name / name / "native.trx").stat().st_size > 0, "Missing native TRX")
            capability = json.loads((reports / name / name / "capability.json").read_bytes())
            require(capability["scenarioId"] == expected["realScenario"] and capability["status"] == "PASS"
                    and capability["cleanup"] == "complete", "Missing capability evidence")

jobs = all_jobs()
durations = {}
for name in expected_jobs:
    matching = [job for job in jobs if job["name"] == name]
    require(len(matching) == 1, f"Missing or duplicate job: {name}")
    job = matching[0]
    require(str(job["run_id"]) == run and str(job["run_attempt"]) == attempt, "Foreign Actions job")
    require(job["status"] == "completed" and job["conclusion"] == "success", f"Job did not succeed: {name}")
    elapsed = (datetime.fromisoformat(job["completed_at"].replace("Z", "+00:00")) - datetime.fromisoformat(job["started_at"].replace("Z", "+00:00"))).total_seconds()
    require(0 <= elapsed <= 150, f"Full job exceeded 150 seconds: {name}")
    durations[name] = elapsed
print(json.dumps({"status": "PASS", "run": run, "attempt": attempt, "sha": sha, "actualJobSeconds": durations}, ensure_ascii=False))
