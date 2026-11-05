"""Verify original candidate attempts against GitHub's server-owned records."""
from datetime import datetime
import re

POLICIES = {
    "host": {"repository": "FlappiBakuse/NexusPipeline", "workflow": ".github/workflows/release.yml",
             "artifact": "host-candidate", "jobs": ["Host / 候选校验"], "buildPrefix": None},
    "stable": {"repository": "FlappiBakuse/NexusPipeline-Plugins", "workflow": ".github/workflows/publish-stable.yml",
               "artifact": "plugins-stable-candidate", "jobs": ["Plugins / 发行装配"], "buildPrefix": None},
    "preview": {"repository": "FlappiBakuse/NexusPipeline-Plugins", "workflow": ".github/workflows/publish-develop.yml",
                "artifact": "plugins-develop-preview", "jobs": ["Plugins / 预览构建"], "buildPrefix": None},
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def positive(value):
    require(type(value) is int and value > 0, "Server identity must be a positive integer")
    return value


def sha(value):
    require(type(value) is str and re.fullmatch("[0-9a-f]{40}", value), "Git identity must be a full lowercase SHA")
    return value


def timestamp(value):
    require(type(value) is str, "Missing server timestamp")
    result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    require(result.utcoffset() is not None, "Server timestamps require an offset")
    return result


def pages(fetch, path, key):
    result = []
    for page in range(1, 101):
        response = fetch(path + f"?per_page=100&page={page}")
        values = response.get(key)
        require(type(values) is list and len(values) <= 100 and all(type(item) is dict for item in values),
                "Invalid server collection")
        result.extend(values)
        if len(values) < 100:
            require(type(response.get("total_count")) is int and response["total_count"] == len(result),
                    "Incomplete server collection")
            return result
    raise ValueError("Server collection exceeds its pagination budget")


def completed_job(jobs, name, run_id, commit):
    matches = [job for job in jobs if job.get("name") == name]
    require(len(matches) == 1, "Candidate job is missing or ambiguous: " + name)
    job = matches[0]
    require(job.get("status") == "completed" and job.get("conclusion") == "success" and
            job.get("run_id") == run_id and job.get("head_sha") == commit, "Original candidate job did not succeed: " + name)
    positive(job.get("id"))
    started, finished = timestamp(job.get("started_at")), timestamp(job.get("completed_at"))
    require(started <= finished, "Candidate job timestamps are inconsistent")
    return started, finished


def resolve_candidate(fetch, kind, run_id, writer_id, artifact_id=None):
    require(kind in POLICIES, "Unknown release channel")
    positive(run_id)
    positive(writer_id)
    require(run_id != writer_id, "Publish-only must consume a separate original candidate run")
    if artifact_id is not None:
        positive(artifact_id)
    policy = POLICIES[kind]
    repository = policy["repository"]
    prefix = "repos/" + repository + "/actions"
    original, writer = [fetch(prefix + "/runs/" + str(identity)) for identity in (run_id, writer_id)]
    for record, identity in ((original, run_id), (writer, writer_id)):
        require(record.get("id") == identity and record.get("repository", {}).get("full_name") == repository,
                "Actions run is not owned by the official repository")
        path = str(record.get("path", "")).split("@", 1)[0]
        require(path in (policy["workflow"], repository + "/" + policy["workflow"]), "Untrusted candidate or writer workflow")
        require(record.get("event") == "workflow_dispatch" and record.get("head_branch") == "main",
                "Candidate and writer require manual protected-main control")
        positive(record.get("workflow_id"))
        sha(record.get("head_sha"))
    require(original["workflow_id"] == writer["workflow_id"], "Candidate and writer workflow identities differ")
    repository_id = positive(original["repository"].get("id"))
    require(writer["repository"].get("id") == repository_id, "Candidate and writer repository identities differ")
    artifacts = pages(fetch, prefix + f"/runs/{run_id}/artifacts", "artifacts")
    matches = []
    for item in artifacts:
        match = re.fullmatch(re.escape(policy["artifact"]) + rf"-{run_id}-([1-9][0-9]*)", str(item.get("name", "")))
        if match and (artifact_id is None or item.get("id") == artifact_id):
            matches.append((item, int(match.group(1))))
    require(len(matches) == 1, "Original candidate artifact is missing or ambiguous; select an explicit artifact ID")
    artifact, attempt = matches[0]
    positive(artifact.get("id"))
    require(original.get("run_attempt") == attempt, "Candidate artifact belongs to a superseded attempt")
    require(artifact.get("expired") is False and type(artifact.get("size_in_bytes")) is int and
            0 < artifact["size_in_bytes"] <= 2 * 1024 * 1024 * 1024, "Candidate artifact is expired or outside its size budget")
    require(type(artifact.get("digest")) is str and re.fullmatch("sha256:[0-9a-f]{64}", artifact["digest"]),
            "Candidate artifact lacks a server SHA256")
    source = artifact.get("workflow_run", {})
    require(source.get("id") == run_id and source.get("repository_id") == repository_id and
            source.get("head_repository_id") == repository_id and source.get("head_sha") == original["head_sha"] and
            source.get("head_branch") == "main", "Artifact workflow source differs from the official candidate")
    jobs = pages(fetch, prefix + f"/runs/{run_id}/attempts/{attempt}/jobs", "jobs")
    windows = [completed_job(jobs, name, run_id, original["head_sha"]) for name in policy["jobs"]]
    if policy["buildPrefix"]:
        names = {job.get("name") for job in jobs if str(job.get("name", "")).startswith(policy["buildPrefix"])}
        require(kind != "preview" or names, "Preview candidate has no package build")
        for name in names:
            completed_job(jobs, name, run_id, original["head_sha"])
    created = timestamp(artifact.get("created_at"))
    require(windows[-1][0] <= created <= windows[-1][1], "Artifact was not created by the verified candidate job")
    return {"kind": kind, "repository": repository, "workflow": policy["workflow"], "workflowCommit": original["head_sha"],
            "runId": run_id, "runAttempt": attempt, "artifactId": artifact["id"], "artifactDigest": artifact["digest"],
            "artifactSizeBytes": artifact["size_in_bytes"], "completedJobsVerified": True,
            "writer": {"repository": repository, "workflow": policy["workflow"], "workflowCommit": writer["head_sha"],
                       "runId": writer_id, "runAttempt": positive(writer.get("run_attempt"))}}


def verify_source(fetch, repository, identity, branch="main"):
    require(repository in {policy["repository"] for policy in POLICIES.values()} and branch in ("main", "develop"),
            "Source must use an official protected repository")
    require(identity.get("sourceMode") == "git-commit", "Local source cannot qualify for publication")
    commit, tree = sha(identity.get("sourceCommit")), sha(identity.get("sourceTree"))
    require(type(identity.get("sourceSnapshotHash")) is str and re.fullmatch("[0-9a-f]{64}", identity["sourceSnapshotHash"]),
            "Source byte manifest identity is missing")
    prefix = "repos/" + repository
    record = fetch(prefix + "/commits/" + commit)
    require(record.get("sha") == commit and record.get("commit", {}).get("tree", {}).get("sha") == tree,
            "Candidate Git tree differs from server source")
    protected = sha(fetch(prefix + "/commits/" + branch).get("sha"))
    comparison = fetch(prefix + f"/compare/{commit}...{protected}")
    require(comparison.get("status") in ("ahead", "identical") and comparison.get("base_commit", {}).get("sha") == commit and
            comparison.get("merge_base_commit", {}).get("sha") == commit, "Candidate source is not reachable from its protected branch")
    return {"repository": repository, "sourceCommit": commit, "sourceTree": tree, "protectedCommit": protected}


def bind_producer(candidate, provenance):
    require(candidate.get("format") == "nexus.release.candidate" and type(candidate.get("version")) is int and
            candidate["version"] == 1 and candidate.get("profile") == "production", "Candidate is not a production release input")
    producer = candidate.get("producer", {})
    expected = {name: provenance[name] for name in ("repository", "workflow", "workflowCommit", "runId", "runAttempt")}
    require(producer == expected and provenance.get("completedJobsVerified") is True,
            "Candidate producer differs from its original successful server attempt")
    return candidate
