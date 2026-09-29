"""Read completed Actions job times after a run has ended; never publish a check."""
import argparse
from datetime import datetime
import json
import os
from pathlib import Path
import urllib.request


def api(route):
    request = urllib.request.Request(os.environ["GITHUB_API_URL"] + route, headers={
        "Authorization": "Bearer " + os.environ["GH_TOKEN"],
        "Accept": "application/vnd.github+json",
    })
    with urllib.request.urlopen(request, timeout=8) as response:
        return json.load(response)


def completed_jobs(repository, run_id, attempt):
    route = f"/repos/{repository}/actions/runs/{run_id}/attempts/{attempt}/jobs"
    first = api(route + "?per_page=100&page=1")
    count = first["total_count"]
    if not isinstance(count, int) or not 1 <= count <= 2000:
        raise ValueError("Invalid Actions job count")
    jobs = list(first["jobs"])
    for page in range(2, (count + 99) // 100 + 1):
        jobs.extend(api(route + f"?per_page=100&page={page}")["jobs"])
    if len(jobs) != count or len({job["id"] for job in jobs}) != count:
        raise ValueError("Incomplete Actions job pagination")
    return jobs


def audit(jobs, run_id, attempt):
    result = []
    for job in jobs:
        if str(job.get("run_id")) != str(run_id) or str(job.get("run_attempt")) != str(attempt):
            raise ValueError("Foreign Actions job")
        if job.get("status") != "completed":
            raise ValueError(f"Job is not completed: {job.get('name')}")
        conclusion = job.get("conclusion")
        if conclusion == "skipped":
            result.append({"id": job["id"], "name": job["name"], "status": "NOT_APPLICABLE", "elapsedMs": None})
            continue
        start, end = job.get("started_at"), job.get("completed_at")
        if not start or not end:
            raise ValueError(f"Missing completed timing: {job.get('name')}")
        duration = (datetime.fromisoformat(end.replace("Z", "+00:00"))
                    - datetime.fromisoformat(start.replace("Z", "+00:00"))).total_seconds() * 1000
        status = "PASS" if conclusion == "success" and 0 <= duration <= 150000 else "FAIL"
        result.append({"id": job["id"], "name": job["name"], "status": status,
                       "elapsedMs": duration, "conclusion": conclusion})
    if len({item["name"] for item in result}) != len(result):
        raise ValueError("Duplicate job names prevent gate identity matching")
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--run-id", required=True, type=int)
    parser.add_argument("--attempt", required=True, type=int)
    parser.add_argument("--output", type=Path)
    options = parser.parse_args()
    if options.run_id <= 0 or options.attempt <= 0:
        raise ValueError("Invalid run identity")
    repository = os.environ["GITHUB_REPOSITORY"]
    jobs = audit(completed_jobs(repository, options.run_id, options.attempt), options.run_id, options.attempt)
    report = {"schemaVersion": 1, "scope": "ACTIONS_COMPLETE_GATE", "runId": options.run_id,
              "attempt": options.attempt, "repository": repository,
              "status": "PASS" if all(item["status"] != "FAIL" for item in jobs) else "FAIL", "jobs": jobs}
    encoded = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    if options.output:
        options.output.write_text(encoded, encoding="utf-8")
    else:
        print(encoded, end="")
    if report["status"] != "PASS":
        raise SystemExit(1)


if __name__ == "__main__":
    main()
