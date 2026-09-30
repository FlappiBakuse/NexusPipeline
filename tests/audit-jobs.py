"""Read completed Actions job times after a run has ended; never publish a check."""
import argparse
from datetime import datetime
import json
import os
import re
from pathlib import Path
import urllib.request


NAMES = json.loads(Path(__file__).with_name("ci-names.json").read_text(encoding="utf-8"))


def job_name(prefix, key):
    if prefix not in ("Host", "Plugins"):
        raise ValueError("Unknown CI repository prefix")
    return prefix + " / " + NAMES[key]


def batch_index(name, prefix):
    pattern = re.escape(job_name(prefix, "batch")) + r" (0[1-5]) · (.+)"
    match = re.fullmatch(pattern, name)
    return int(match[1]) if match and re.search(r"[\u4e00-\u9fff]", match[2]) else None


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
        result = api(route + f"?per_page=100&page={page}")
        if result.get("total_count") != count:
            raise ValueError("Actions collection changed during pagination")
        jobs.extend(result["jobs"])
    if len(jobs) != count or len({job["id"] for job in jobs}) != count:
        raise ValueError("Incomplete Actions job pagination")
    return jobs


def paged(route, field):
    separator = "&" if "?" in route else "?"
    first = api(route + separator + "per_page=100&page=1")
    count = first.get("total_count")
    if type(count) is not int or not 0 <= count <= 2000:
        raise ValueError("Invalid API collection size")
    values = list(first[field])
    for page in range(2, (count + 99) // 100 + 1):
        current = api(route + separator + f"per_page=100&page={page}")
        if current.get("total_count") != count:
            raise ValueError("API collection changed during pagination")
        values.extend(current[field])
    if len(values) != count or len({value["id"] for value in values}) != count:
        raise ValueError("Incomplete API pagination")
    return values


def physical_jobs(repository, run, jobs, check_name, app_id):
    physical, synthetic = [], []
    for job in jobs:
        if str(job.get("run_id")) != str(run["id"]) or str(job.get("run_attempt")) != str(run["run_attempt"]):
            raise ValueError("Foreign Actions record")
        optional_names = {job_name(prefix, "control"): job_name(prefix, "control") for prefix in ["Host", "Plugins"]}
        for prefix in ["Host", "Plugins"]:
            optional_names[prefix+" / Control"] = prefix+" / Control"
            legacy = prefix+" / Batches (not selected)"
            optional_names.update({legacy: legacy, "matrix.name || '"+legacy+"'": legacy})
            canonical = job_name(prefix, "unselected")
            optional_names.update({canonical: canonical, "matrix.name || '"+canonical+"'": canonical})
        # Labels can be selectors on a skipped job without an assigned runner.
        if (job.get("status") == "completed" and job.get("conclusion") == "skipped"
                and not job.get("runner_id") and not job.get("steps") and job.get("name") in optional_names):
            physical.append({**job, "name": optional_names[job["name"]], "runnerlessSkipped": True})
            continue
        if job.get("labels") or job.get("runner_id") or job.get("steps"):
            physical.append(job)
            continue
        check = api(f"/repos/{repository}/check-runs/{job['id']}")
        external = check.get("external_id") or ""
        legacy = (not external and check.get("output", {}).get("title") == "Complete CI job budget"
                  and check.get("output", {}).get("summary", "").startswith(f"Run {run['id']}, attempt "))
        if (check.get("id") != job["id"] or check.get("head_sha") != run["head_sha"]
                or check.get("app", {}).get("id") != app_id or check.get("name") != check_name
                or job.get("check_run_url") != os.environ["GITHUB_API_URL"] + f"/repos/{repository}/check-runs/{job['id']}"
                or not (re.fullmatch(r"nxp-budget-v2:(?:[1-9]\d*:){5}[a-f0-9]{40}",external) or legacy)):
            raise ValueError("Unclassified runnerless Actions record")
        synthetic.append(check)
    if not physical:
        raise ValueError("No physical producer jobs")
    return physical, synthetic


def audit(jobs, run_id, attempt):
    if not jobs or len({job["id"] for job in jobs}) != len(jobs):
        raise ValueError("Empty or duplicate Actions jobs")
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
        start_time = datetime.fromisoformat(start.replace("Z", "+00:00"))
        end_time = datetime.fromisoformat(end.replace("Z", "+00:00"))
        if start_time.tzinfo is None or end_time.tzinfo is None:
            raise ValueError("Job timestamps require timezones")
        delta = end_time - start_time
        duration = delta.days * 86400000 + delta.seconds * 1000 + delta.microseconds / 1000
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
    run = api(f"/repos/{repository}/actions/runs/{options.run_id}/attempts/{options.attempt}")
    if (run.get("id") != options.run_id or run.get("run_attempt") != options.attempt
            or run.get("repository", {}).get("full_name") != repository
            or run.get("path") != ".github/workflows/ci.yml"):
        raise ValueError("Unexpected producer identity")
    suite = api(f"/repos/{repository}/check-suites/{run['check_suite_id']}")
    name = job_name("Plugins" if repository.endswith("/NexusPipeline-Plugins") else "Host", "finalBudget")
    physical, checks = physical_jobs(repository, run, completed_jobs(repository, options.run_id, options.attempt), name, suite["app"]["id"])
    jobs = audit(physical, options.run_id, options.attempt)
    report = {"schemaVersion": 1, "scope": "ACTIONS_COMPLETE_GATE", "runId": options.run_id,
              "attempt": options.attempt, "repository": repository,
              "status": "PASS" if all(item["status"] != "FAIL" for item in jobs) else "FAIL", "jobs": jobs,
              "syntheticCheckIds": [check["id"] for check in checks]}
    encoded = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    if options.output:
        options.output.write_text(encoded, encoding="utf-8")
    else:
        print(encoded, end="")
    if report["status"] != "PASS":
        raise SystemExit(1)


if __name__ == "__main__":
    main()
