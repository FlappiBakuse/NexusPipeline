"""Publish the completed CI run's full job budget as a trusted check."""

import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import urllib.request


AUDIT_PATH = Path(__file__).with_name("audit-jobs.py")
SPEC = importlib.util.spec_from_file_location("audit_jobs", AUDIT_PATH)
audit_jobs = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audit_jobs)


def resolve_pulls(run, repository):
    pulls = run.get("pull_requests") or []
    if pulls:
        return pulls
    sha = run.get("head_sha")
    if not isinstance(sha, str) or not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ValueError("Invalid CI head SHA")
    # Actions omits the PR relation from older runs after their PR is merged.
    pulls = audit_jobs.api(f"/repos/{repository}/commits/{sha}/pulls?per_page=100")
    if not isinstance(pulls, list) or len(pulls) >= 100:
        raise ValueError("Ambiguous or incomplete commit PR relation")
    pulls = [pull for pull in pulls if pull.get("head", {}).get("sha") == sha
             and pull.get("base", {}).get("ref") == "main"
             and pull.get("base", {}).get("repo", {}).get("url") == os.environ["GITHUB_API_URL"] + "/repos/" + repository]
    if len(pulls) != 1:
        raise ValueError("Expected one PR associated with the completed commit")
    return pulls


def evaluate(run, jobs, repository, run_id, attempt, required_name):
    if (run.get("id") != run_id or run.get("run_attempt") != attempt
            or run.get("status") != "completed" or run.get("event") != "pull_request"
            or run.get("path") != ".github/workflows/ci.yml"
            or run.get("repository", {}).get("full_name") != repository):
        raise ValueError("Unexpected CI run identity")
    pulls = resolve_pulls(run, repository)
    if len(pulls) != 1 or pulls[0].get("base", {}).get("ref") != "main":
        raise ValueError("Expected one pull request into main")
    if (pulls[0].get("base", {}).get("repo", {}).get("url")
            != os.environ["GITHUB_API_URL"] + "/repos/" + repository):
        raise ValueError("Foreign pull request base")
    sha = run.get("head_sha")
    if not isinstance(sha, str) or not re.fullmatch(r"[0-9a-f]{40}", sha) or pulls[0].get("head", {}).get("sha") != sha:
        raise ValueError("Pull request head mismatch")
    report = audit_jobs.audit(jobs, run_id, attempt)
    required = [item for item in report if item["name"] == required_name]
    if len(required) != 1:
        raise ValueError("Missing or duplicate Required job")
    passed = (run.get("conclusion") == "success"
              and required[0]["status"] == "PASS"
              and all(item["status"] != "FAIL" for item in report))
    prefix = required_name.removesuffix(" / Required")
    if any(item["name"] == prefix+" / Control" or item["name"].startswith(prefix+" / batch-")
           or item["name"] == prefix+" / Batches (not selected)" for item in report):
        names = {item["name"] for item in report}
        batches = sorted(name for name in names if name.startswith(prefix+" / batch-"))
        permitted = {prefix+" / 范围判定",prefix+" / Control",required_name,prefix+" / Batches (not selected)",*batches}
        physical_count = sum(not job.get("runnerlessSkipped") for job in jobs)
        if (not names <= permitted or prefix+" / 范围判定" not in names
                or batches != [prefix+f" / batch-{index:02d}" for index in range(1,len(batches)+1)]
                or len(batches) > 5 or physical_count+2 > 10):
            raise ValueError("Unexpected or oversized physical batch producer graph")
    return sha, report, passed


def write_api(route, body, method):
    request = urllib.request.Request(os.environ["GITHUB_API_URL"] + route,
        data=json.dumps(body).encode("utf-8"), method=method,
        headers={"Authorization": "Bearer " + os.environ["GH_TOKEN"],
                 "Accept": "application/vnd.github+json", "Content-Type": "application/json",
                 "X-GitHub-Api-Version": "2026-03-10"})
    with urllib.request.urlopen(request, timeout=8) as response:
        return json.load(response)


def registration(check):
    value = check.get("external_id", "").split(":")
    if len(value) != 7 or value[0] != "nxp-budget-v2" or not all(v.isdigit() and int(v) > 0 for v in value[1:6]) or not re.fullmatch(r"[a-f0-9]{40}", value[6]):
        raise ValueError("Missing trusted begin registration")
    return dict(zip(["pr", "run", "attempt", "beginRun", "beginAttempt", "controllerSha"],
                    [*map(int, value[1:6]), value[6]]))


def current_check(repository, sha, check_name, app_id, pr):
    checks = audit_jobs.paged(f"/repos/{repository}/commits/{sha}/check-runs?filter=all", "check_runs")
    owned = [check for check in checks if check.get("name") == check_name and check.get("head_sha") == sha
             and check.get("app", {}).get("id") == app_id
             and (check.get("external_id", "").startswith(f"nxp-budget-v2:{pr}:")
                  or not check.get("external_id") and check.get("output", {}).get("title") == "Complete CI job budget")]
    return max(owned, key=lambda check: check["id"], default=None)


def is_current(repository, run, pull):
    current = audit_jobs.api(f"/repos/{repository}/pulls/{pull['number']}")
    if (current.get("head", {}).get("sha") != run["head_sha"] or current.get("base", {}).get("ref") != "main"
            or current.get("base", {}).get("repo", {}).get("url") != os.environ["GITHUB_API_URL"]+"/repos/"+repository):
        return False
    latest = audit_jobs.api(f"/repos/{repository}/actions/runs/{run['id']}")
    if latest.get("run_attempt") != run["run_attempt"]:
        return False
    runs = audit_jobs.paged(f"/repos/{repository}/actions/workflows/ci.yml/runs?event=pull_request&head_sha={run['head_sha']}", "workflow_runs")
    candidates = [item for item in runs if item.get("head_sha") == run["head_sha"]
                  and item.get("path") == ".github/workflows/ci.yml"
                  and (not item.get("pull_requests") or any(p.get("number") == pull["number"] for p in item["pull_requests"]))]
    if not candidates or any(type(item.get("run_number")) is not int for item in candidates):
        raise ValueError("Cannot establish latest producer run")
    return max(candidates, key=lambda item: item["run_number"])["id"] == run["id"]


def upsert(repository, sha, check_name, existing, body):
    body = {"name": check_name, **body}
    if existing:
        route, method = f"/repos/{repository}/check-runs/{existing['id']}", "PATCH"
    else:
        route, method = f"/repos/{repository}/check-runs", "POST"
        body["head_sha"] = sha
    result = write_api(route, body, method)
    if result.get("head_sha") != sha or result.get("name") != check_name or existing and result.get("id") != existing["id"]:
        raise ValueError("Published check identity mismatch")
    return result["html_url"]


def require_complete_attempt(repository, run, jobs, check_name, app_id):
    if run["run_attempt"] <= 1:
        return
    original = {**run, "run_attempt": 1}
    expected, _ = audit_jobs.physical_jobs(repository, original,
        audit_jobs.completed_jobs(repository, run["id"], 1), check_name, app_id)
    if {job["name"] for job in expected} != {job["name"] for job in jobs}:
        raise ValueError("INCOMPLETE_ATTEMPT: rerun the complete producer workflow")


def publish(repository, sha, check_name, run_id, attempt, report, passed, existing=None, external_id=None):
    failing = [item for item in report if item["status"] == "FAIL"]
    summary = (f"Run {run_id}, attempt {attempt}: {len(report)} completed jobs; "
               f"{len(failing)} failed the complete 150000 ms budget or Actions result.\n\n")
    summary += "\n".join(f"- {item['name']}: {item['status']} "
                         f"({item['elapsedMs']} ms)" for item in failing[:30])
    body = {
        "status": "completed",
        "conclusion": "success" if passed else "failure",
        "details_url": f"https://github.com/{repository}/actions/runs/{run_id}/attempts/{attempt}",
        "output": {"title": "Complete CI job budget", "summary": summary},
    }
    if external_id:
        body["external_id"] = external_id
    return upsert(repository, sha, check_name, existing, body)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--run-id", type=int, required=True)
    parser.add_argument("--attempt", type=int, required=True)
    parser.add_argument("--required-name", required=True)
    parser.add_argument("--check-name", required=True)
    parser.add_argument("--phase", choices=["begin", "finalize"], default="finalize")
    args = parser.parse_args()
    if args.run_id <= 0 or args.attempt <= 0:
        raise ValueError("Invalid run identity")
    repository = os.environ["GITHUB_REPOSITORY"]
    run = audit_jobs.api(f"/repos/{repository}/actions/runs/{args.run_id}/attempts/{args.attempt}")
    if (run.get("id") != args.run_id or run.get("run_attempt") != args.attempt
            or run.get("event") != "pull_request" or run.get("path") != ".github/workflows/ci.yml"
            or run.get("repository", {}).get("full_name") != repository):
        raise ValueError("Unexpected producer identity")
    pulls = resolve_pulls(run, repository)
    sha = run["head_sha"]
    if (len(pulls) != 1 or pulls[0].get("head", {}).get("sha") != sha
            or pulls[0].get("base", {}).get("ref") != "main"
            or pulls[0].get("base", {}).get("repo", {}).get("url") != os.environ["GITHUB_API_URL"] + "/repos/" + repository):
        raise ValueError("Unexpected pull request identity")
    pull = pulls[0]
    if not is_current(repository, run, pull):
        print("SUPERSEDED: no check update")
        return
    controller = audit_jobs.api(f"/repos/{repository}/actions/runs/{os.environ['GITHUB_RUN_ID']}")
    controller_sha = os.environ["GITHUB_SHA"]
    if (str(controller.get("id")) != os.environ["GITHUB_RUN_ID"] or controller.get("path") != ".github/workflows/final-budget.yml"
            or controller.get("head_sha") != controller_sha or controller.get("head_branch") != "main"
            or controller.get("event") not in (["workflow_run", "workflow_dispatch"] if args.phase == "finalize" else ["workflow_run"])
            or controller.get("repository",{}).get("full_name") != repository):
        raise ValueError("Unexpected trusted controller")
    suite = audit_jobs.api(f"/repos/{repository}/check-suites/{controller['check_suite_id']}")
    app_id = suite["app"]["id"]
    existing = current_check(repository, sha, args.check_name, app_id, pull["number"])
    if args.phase == "begin":
        # Actions can return queued while later jobs wait after the trusted start event.
        if run.get("status") not in ["queued", "in_progress", "completed"]:
            raise ValueError("Producer has not started")
        if existing:
            try:
                old = registration(existing)
                if (old["run"], old["attempt"]) == (args.run_id, args.attempt):
                    print("IDEMPOTENT: begin already registered")
                    return
            except ValueError:
                pass
        external = f"nxp-budget-v2:{pull['number']}:{args.run_id}:{args.attempt}:{controller['id']}:{controller['run_attempt']}:{controller_sha}"
        if not is_current(repository, run, pull):
            print("SUPERSEDED: no check update")
            return
        print(upsert(repository, sha, args.check_name, existing, {
            "status": "in_progress", "external_id": external,
            "details_url": f"https://github.com/{repository}/actions/runs/{args.run_id}/attempts/{args.attempt}",
            "output": {"title": "Complete CI job budget", "summary": "Trusted begin registered; final audit pending."}}))
        return
    if not existing:
        raise ValueError("Missing trusted begin registration")
    identity = registration(existing)
    if (identity["run"], identity["attempt"]) != (args.run_id, args.attempt):
        print("SUPERSEDED: no check update")
        return
    begin = audit_jobs.api(f"/repos/{repository}/actions/runs/{identity['beginRun']}/attempts/{identity['beginAttempt']}")
    if (begin.get("id") != identity["beginRun"] or begin.get("run_attempt") != identity["beginAttempt"]
            or begin.get("path") != ".github/workflows/final-budget.yml" or begin.get("head_sha") != identity["controllerSha"]
            or begin.get("event") != "workflow_run" or begin.get("head_branch") != "main"
            or begin.get("repository", {}).get("full_name") != repository or begin.get("conclusion") != "success"):
        raise ValueError("Trusted begin did not complete successfully")
    begin_report = audit_jobs.audit(audit_jobs.completed_jobs(repository, identity["beginRun"], identity["beginAttempt"]), identity["beginRun"], identity["beginAttempt"])
    raw_jobs = audit_jobs.completed_jobs(repository, args.run_id, args.attempt)
    jobs, synthetic = audit_jobs.physical_jobs(repository, run, raw_jobs, args.check_name, app_id)
    require_complete_attempt(repository, run, jobs, args.check_name, app_id)
    sha, report, passed = evaluate(run, jobs, repository, args.run_id, args.attempt, args.required_name)
    passed = passed and all(item["status"] == "PASS" for item in begin_report)
    if not is_current(repository, run, pull):
        print("SUPERSEDED: no check update")
        return
    current = current_check(repository, sha, args.check_name, app_id, pull["number"])
    if not current or current["id"] != existing["id"] or current.get("external_id") != existing.get("external_id"):
        raise ValueError("Trusted check changed during audit")
    print(publish(repository, sha, args.check_name, args.run_id,
                  args.attempt, report, passed, current, existing["external_id"]))
    if not passed:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
