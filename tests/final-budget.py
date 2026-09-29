"""Publish the completed CI run's full job budget as a trusted check."""

import argparse
import importlib.util
import json
import os
from pathlib import Path
import urllib.request


AUDIT_PATH = Path(__file__).with_name("audit-jobs.py")
SPEC = importlib.util.spec_from_file_location("audit_jobs", AUDIT_PATH)
audit_jobs = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audit_jobs)


def evaluate(run, jobs, repository, run_id, attempt, required_name):
    if (run.get("id") != run_id or run.get("run_attempt") != attempt
            or run.get("status") != "completed" or run.get("event") != "pull_request"
            or run.get("path") != ".github/workflows/ci.yml"
            or run.get("repository", {}).get("full_name") != repository):
        raise ValueError("Unexpected CI run identity")
    pulls = run.get("pull_requests") or []
    if len(pulls) != 1 or pulls[0].get("base", {}).get("ref") != "main":
        raise ValueError("Expected one pull request into main")
    if (pulls[0].get("base", {}).get("repo", {}).get("url")
            != os.environ["GITHUB_API_URL"] + "/repos/" + repository):
        raise ValueError("Foreign pull request base")
    sha = run.get("head_sha")
    if not isinstance(sha, str) or len(sha) != 40 or pulls[0].get("head", {}).get("sha") != sha:
        raise ValueError("Pull request head mismatch")
    report = audit_jobs.audit(jobs, run_id, attempt)
    required = [item for item in report if item["name"] == required_name]
    if len(required) != 1:
        raise ValueError("Missing or duplicate Required job")
    passed = (run.get("conclusion") == "success"
              and required[0]["status"] == "PASS"
              and all(item["status"] != "FAIL" for item in report))
    return sha, report, passed


def publish(repository, sha, check_name, run_id, attempt, report, passed):
    failing = [item for item in report if item["status"] == "FAIL"]
    summary = (f"Run {run_id}, attempt {attempt}: {len(report)} completed jobs; "
               f"{len(failing)} failed the complete 150000 ms budget or Actions result.\n\n")
    summary += "\n".join(f"- {item['name']}: {item['status']} "
                         f"({item['elapsedMs']} ms)" for item in failing[:30])
    body = json.dumps({
        "name": check_name,
        "head_sha": sha,
        "status": "completed",
        "conclusion": "success" if passed else "failure",
        "details_url": f"https://github.com/{repository}/actions/runs/{run_id}/attempts/{attempt}",
        "output": {"title": "Complete CI job budget", "summary": summary},
    }).encode("utf-8")
    request = urllib.request.Request(
        os.environ["GITHUB_API_URL"] + f"/repos/{repository}/check-runs",
        data=body,
        method="POST",
        headers={"Authorization": "Bearer " + os.environ["GH_TOKEN"],
                 "Accept": "application/vnd.github+json",
                 "Content-Type": "application/json",
                 "X-GitHub-Api-Version": "2026-03-10"},
    )
    with urllib.request.urlopen(request, timeout=8) as response:
        result = json.load(response)
    if (result.get("head_sha") != sha or result.get("name") != check_name):
        raise ValueError("Published check identity mismatch")
    return result["html_url"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--run-id", type=int, required=True)
    parser.add_argument("--attempt", type=int, required=True)
    parser.add_argument("--required-name", required=True)
    parser.add_argument("--check-name", required=True)
    args = parser.parse_args()
    if args.run_id <= 0 or args.attempt <= 0:
        raise ValueError("Invalid run identity")
    repository = os.environ["GITHUB_REPOSITORY"]
    run = audit_jobs.api(f"/repos/{repository}/actions/runs/{args.run_id}")
    jobs = audit_jobs.completed_jobs(repository, args.run_id, args.attempt)
    sha, report, passed = evaluate(run, jobs, repository, args.run_id,
                                   args.attempt, args.required_name)
    print(publish(repository, sha, args.check_name, args.run_id,
                  args.attempt, report, passed))
    if not passed:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
