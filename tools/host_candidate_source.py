"""Resolve an original Host candidate from GitHub's server-side run records.

The candidate sidecar is data. This module does not download or execute it, and
does not require the overall workflow conclusion to be success: a later writer
failure must not invalidate an earlier successful candidate job.
"""

from __future__ import annotations

import argparse
from datetime import datetime
import json
import os
import re
import time
import urllib.error
import urllib.request
from collections.abc import Callable
from pathlib import Path
from typing import Any


REPOSITORY = "FlappiBakuse/NexusPipeline"
WORKFLOW_PATH = ".github/workflows/release.yml"
REQUIRED_JOBS = (
    "Host / 候选输入", "Host / 生产前端", "Host / 预装插件", "Host / 生产宿主",
    "Host / 包校验", "Host / 安装器构建", "Host / 候选校验",
)


class CandidateSourceError(ValueError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise CandidateSourceError(message)


def positive_int(value: Any, label: str) -> int:
    require(type(value) is int and value > 0, f"{label} 必须是正整数")
    return value


def full_sha(value: Any, label: str) -> str:
    require(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{40}", value) is not None,
            f"{label} 必须是完整 SHA")
    return value


def completed_job(entries: list[dict[str, Any]], name: str) -> None:
    matches = [job for job in entries if job.get("name") == name]
    require(len(matches) == 1 and matches[0].get("conclusion") == "success"
            and matches[0].get("status") == "completed", f"原 attempt {name} job 未真实成功")
    try:
        started = datetime.fromisoformat(matches[0]["started_at"].replace("Z", "+00:00"))
        completed = datetime.fromisoformat(matches[0]["completed_at"].replace("Z", "+00:00"))
        elapsed = (completed - started).total_seconds()
    except (KeyError, AttributeError, TypeError, ValueError) as exc:
        raise CandidateSourceError(f"{name} 缺少完整服务端时间") from exc
    require(0 <= elapsed, f"{name} 完整耗时无效")


def list_attempt_jobs(fetch: Callable[[str], dict[str, Any]], prefix: str, run_id: int, attempt: int) -> list[dict[str, Any]]:
    entries = []
    for page in range(1, 101):
        response = fetch(f"{prefix}/runs/{run_id}/attempts/{attempt}/jobs?per_page=100&page={page}")
        batch = response.get("jobs")
        require(isinstance(batch, list) and all(isinstance(job, dict) for job in batch), "原 attempt jobs 列表无效")
        entries.extend(batch)
        if len(batch) < 100:
            break
    else:
        raise CandidateSourceError("原 attempt jobs 超过有界分页上限")
    return entries


def resolve_candidate(
    fetch: Callable[[str], dict[str, Any]],
    *,
    candidate_run_id: int,
    current_run_id: int,
    artifact_id: int | None = None,
) -> dict[str, Any]:
    positive_int(candidate_run_id, "candidate run ID")
    positive_int(current_run_id, "current run ID")
    if artifact_id is not None:
        positive_int(artifact_id, "artifact ID")
    prefix = f"repos/{REPOSITORY}/actions"
    original = fetch(f"{prefix}/runs/{candidate_run_id}")
    current = fetch(f"{prefix}/runs/{current_run_id}")
    require(original.get("id") == candidate_run_id and current.get("id") == current_run_id,
            "Actions run ID 与服务端记录不符")
    require(original.get("repository", {}).get("full_name") == REPOSITORY
            and current.get("repository", {}).get("full_name") == REPOSITORY,
            "candidate 或 writer 不属于官方 Host 仓库")
    require(original.get("workflow_id") == current.get("workflow_id"),
            "candidate 与 writer 的 workflow 身份不一致")
    workflow_path = str(original.get("path", "")).split("@", 1)[0]
    require(workflow_path == WORKFLOW_PATH or workflow_path == f"{REPOSITORY}/{WORKFLOW_PATH}",
            "candidate workflow 路径不可信")
    require(original.get("event") in {"push", "workflow_dispatch"} and original.get("head_branch") == "main",
            "candidate 必须由 main push 或 main 控制的手动工作流产生")
    controller_sha = full_sha(original.get("head_sha"), "candidate controller")
    artifacts: list[dict[str, Any]] = []
    for page in range(1, 101):
        response = fetch(f"{prefix}/runs/{candidate_run_id}/artifacts?per_page=100&page={page}")
        batch = response.get("artifacts")
        require(isinstance(batch, list), "artifact 服务端列表无效")
        artifacts.extend(batch)
        if len(batch) < 100:
            break
    else:
        raise CandidateSourceError("artifact 列表超过有界分页上限")
    matches = []
    for item in artifacts:
        if not isinstance(item, dict):
            continue
        match = re.fullmatch(rf"host-candidate-{candidate_run_id}-([1-9][0-9]*)", str(item.get("name", "")))
        if match and (artifact_id is None or item.get("id") == artifact_id):
            matches.append((item, int(match.group(1))))
    require(len(matches) == 1, "IDENTITY_UNVERIFIABLE：candidate artifact 不唯一或不存在；请指定 artifact ID")
    artifact, attempt = matches[0]
    require(original.get("run_attempt", attempt) == attempt, "candidate artifact 属于过期 attempt")
    require(type(artifact.get("id")) is int and artifact["id"] > 0
            and artifact.get("expired") is False
            and type(artifact.get("size_in_bytes")) is int and artifact["size_in_bytes"] > 0
            and artifact.get("workflow_run", {}).get("id") == candidate_run_id,
            "candidate artifact 身份、有效期或大小无效")
    digest = artifact.get("digest")
    require(isinstance(digest, str) and re.fullmatch(r"sha256:[0-9a-f]{64}", digest) is not None,
            "candidate artifact 缺少服务端 SHA256")
    entries = list_attempt_jobs(fetch, prefix, candidate_run_id, attempt)
    names = {job.get("name") for job in entries}
    require("Build and validate Host candidate" not in names, "candidate producer schema 已退役")
    for name in REQUIRED_JOBS:
        completed_job(entries, name)
    return {
        "sourceSha": controller_sha,
        "workflowSha": controller_sha,
        "runId": candidate_run_id,
        "runAttempt": attempt,
        "artifactId": artifact["id"],
        "artifactDigest": digest,
        "producerSchema": "staged-v2",
        "completedJobsVerified": True,
        "executionLimitMs": None,
    }


def github_fetch(token: str, path: str) -> dict[str, Any]:
    request = urllib.request.Request(
        f"https://api.github.com/{path}",
        headers={"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
                 "X-GitHub-Api-Version": "2022-11-28"},
    )
    for attempt in range(3):
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                return json.load(response)
        except urllib.error.HTTPError as exc:
            if (exc.code == 429 or exc.code >= 500) and attempt < 2:
                retry_after = exc.headers.get("Retry-After", "1")
                delay = min(5, max(1, int(retry_after))) if retry_after.isdecimal() else 1
                time.sleep(delay)
                continue
            raise CandidateSourceError(f"读取 Actions 服务端事实失败：HTTP {exc.code}") from exc
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            if attempt < 2:
                time.sleep(2 ** attempt)
                continue
            raise CandidateSourceError(f"读取 Actions 服务端事实失败：{type(exc).__name__}") from exc
        except ValueError as exc:
            raise CandidateSourceError("读取 Actions 服务端事实失败：响应 JSON 无效") from exc
    raise CandidateSourceError("读取 Actions 服务端事实失败：超过有界重试")


def main() -> None:
    parser = argparse.ArgumentParser(description="Resolve original Host candidate producer and artifact")
    parser.add_argument("--candidate-run-id", type=int, required=True)
    parser.add_argument("--current-run-id", type=int, required=True)
    parser.add_argument("--artifact-id", type=int)
    parser.add_argument("--github-output", type=Path, required=True)
    args = parser.parse_args()
    token = os.environ.get("GITHUB_TOKEN", "")
    require(bool(token), "缺少 Actions read token")
    result = resolve_candidate(lambda path: github_fetch(token, path),
                               candidate_run_id=args.candidate_run_id,
                               current_run_id=args.current_run_id, artifact_id=args.artifact_id)
    keys = {"sourceSha": "source_sha", "workflowSha": "workflow_sha", "runId": "run_id",
            "runAttempt": "run_attempt", "artifactId": "artifact_id", "artifactDigest": "artifact_digest"}
    with args.github_output.open("a", encoding="utf-8") as stream:
        for key, output in keys.items():
            stream.write(f"{output}={result[key]}\n")
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()
