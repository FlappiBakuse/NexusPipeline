"""Host Release Qualification 的 fail-closed 控制器策略。

远端 App、token 和规则启用属于独立权限工作。本模块只固定官方 API 主机、官方
双仓库、H1-H5 聚合和 squash merge 证明；单测使用注入 transport，不代表线上已启用。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
from collections.abc import Callable, Mapping
from datetime import datetime, timezone
from typing import Any


API_ROOT = "https://api.github.com"
OFFICIAL_REPOSITORIES = frozenset({"FlappiBakuse/NexusPipeline", "FlappiBakuse/NexusPipeline-Plugins"})
OFFICIAL_HOST_REPOSITORY = "FlappiBakuse/NexusPipeline"
OFFICIAL_PLUGINS_REPOSITORY = "FlappiBakuse/NexusPipeline-Plugins"
CHECK_NAME = "Release Qualification"
REQUIRED_GATES = ("H1", "H2", "H3", "H4", "H5")
FULL_SHA = re.compile(r"^[0-9a-f]{40}$")
EXTERNAL_ID = "host-release-qualification-v1"


class QualificationError(RuntimeError):
    """资格证明不完整或候选状态发生变化。"""


RequestFn = Callable[[str, str, str | None, Mapping[str, Any] | None], Any]
GitRunner = Callable[[list[str]], subprocess.CompletedProcess[str]]


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise QualificationError(message)


def _sha(value: Any, label: str) -> str:
    _require(isinstance(value, str) and FULL_SHA.fullmatch(value) is not None, f"{label} 必须是完整 40 位 SHA")
    return value


def _repository(repository: str) -> str:
    _require(repository in OFFICIAL_REPOSITORIES, f"仓库不在官方允许范围：{repository}")
    return repository


def _path(path: str) -> str:
    _require(path.startswith("/") and not path.startswith("//"), "GitHub API path 必须是绝对路径")
    _require(".." not in path and not re.search(r"https?://", path, re.IGNORECASE), "禁止 API path URL 或越界段")
    return path


def _default_request(method: str, path: str, token: str | None, payload: Mapping[str, Any] | None) -> Any:
    body = None if payload is None else json.dumps(payload, ensure_ascii=False).encode("utf-8")
    headers = {
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "nexuspipeline-qualification",
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(f"{API_ROOT}{_path(path)}", data=body, headers=headers, method=method)
    with urllib.request.urlopen(request, timeout=30) as response:
        raw = response.read()
    return {} if not raw else json.loads(raw.decode("utf-8"))


def github_request(
    method: str,
    path: str,
    token: str | None,
    payload: Mapping[str, Any] | None = None,
    *,
    request_fn: RequestFn | None = None,
    sleep: Callable[[float], None] = time.sleep,
) -> Any:
    """固定 api.github.com；只对 429/5xx 做最多三次有界重试。"""

    _require(method in {"GET", "POST", "PATCH"}, f"不允许的 GitHub API method：{method}")
    path = _path(path)
    request_fn = request_fn or _default_request
    last_error: Exception | None = None
    for attempt in range(3):
        try:
            return request_fn(method, path, token, payload)
        except urllib.error.HTTPError as exc:
            if exc.code not in {429, 500, 502, 503, 504} or attempt == 2:
                raise QualificationError(f"GitHub API {method} {path} 失败：HTTP {exc.code}") from exc
            last_error = exc
            sleep(float(2**attempt))
        except (TimeoutError, urllib.error.URLError) as exc:
            raise QualificationError(f"GitHub API {method} {path} 网络失败") from exc
    raise QualificationError(f"GitHub API {method} {path} 重试失败") from last_error


def read_pull_request(repository: str, number: int | str, expected_head_sha: str, token: str | None, *, request_fn: RequestFn | None = None) -> dict[str, Any]:
    repository = _repository(repository)
    _sha(expected_head_sha, "expected_head_sha")
    _require(str(number).isdigit() and int(number) > 0, "pr_number 必须是正整数")
    pull = github_request("GET", f"/repos/{repository}/pulls/{int(number)}", token, request_fn=request_fn)
    _require(isinstance(pull, dict), "PR API 返回不是对象")
    _require(pull.get("state") == "open", "PR 必须保持 open")
    _require((pull.get("base") or {}).get("ref") == "main", "PR base 必须是 main")
    base_repo = ((pull.get("base") or {}).get("repo") or {}).get("full_name")
    head_repo = ((pull.get("head") or {}).get("repo") or {}).get("full_name")
    _require(base_repo == repository and head_repo == repository, "PR 必须来自同一官方仓库，不能使用 fork")
    _require(((pull.get("head") or {}).get("sha")) == expected_head_sha, "PR head 已变化")
    return pull


def _ref_sha(value: Mapping[str, Any], label: str) -> str:
    return _sha((value.get("object") or {}).get("sha"), label)


def resolve_candidate(repository: str, expected_head_sha: str, workflow_sha: str, token: str | None, *, request_fn: RequestFn | None = None, git_runner: GitRunner | None = None) -> dict[str, str]:
    repository = _repository(repository)
    h = _sha(expected_head_sha, "H")
    c = _sha(workflow_sha, "C")
    ref = github_request("GET", f"/repos/{repository}/git/ref/heads/main", token, request_fn=request_fn)
    b = _ref_sha(ref, "B")
    _require(c == b, "可信 qualification workflow C 必须等于启动时 main B")
    runner = git_runner or (lambda args: subprocess.run(args, check=False, capture_output=True, text=True))
    _require(runner(["git", "merge-base", "--is-ancestor", b, h]).returncode == 0, "main B 必须是候选 head H 的祖先")
    return {"headSha": h, "baseSha": b, "workflowSha": c}


def resolve_contract_input(repository: str, token: str | None, *, candidate_sha: str | None = None, request_fn: RequestFn | None = None, contract_validator: Callable[[str], None] | None = None) -> dict[str, str]:
    _repository(repository)
    partner = OFFICIAL_HOST_REPOSITORY if repository == OFFICIAL_PLUGINS_REPOSITORY else OFFICIAL_PLUGINS_REPOSITORY
    if candidate_sha is None:
        ref = github_request("GET", f"/repos/{partner}/git/ref/heads/main", token, request_fn=request_fn)
        resolved = _ref_sha(ref, "contractSourceSha")
    else:
        resolved = _sha(candidate_sha, "contractSourceSha")
    if contract_validator is not None:
        contract_validator(resolved)
    return {"repository": partner, "contractSourceSha": resolved}


def begin_check(repository: str, head_sha: str, app_id: int | str, token: str, association: Mapping[str, Any], *, request_fn: RequestFn | None = None) -> str:
    repository = _repository(repository)
    head_sha = _sha(head_sha, "H")
    _require(str(app_id).isdigit(), "Qualification App ID 必须是数字")
    listing = github_request("GET", f"/repos/{repository}/commits/{head_sha}/check-runs?per_page=100", token, request_fn=request_fn)
    runs = listing.get("check_runs", []) if isinstance(listing, dict) else []
    same_name = [run for run in runs if run.get("name") == CHECK_NAME]
    _require(not [run for run in same_name if str((run.get("app") or {}).get("id")) != str(app_id)], "发现其他 App 使用同名资格 check")
    ours = [run for run in same_name if run.get("external_id") == EXTERNAL_ID]
    _require(len(ours) <= 1, "本控制器同一 H 存在重复 external_id")
    payload = {
        "name": CHECK_NAME,
        "head_sha": head_sha,
        "status": "in_progress",
        "started_at": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "external_id": EXTERNAL_ID,
        "output": {"title": "Release Qualification running", "summary": json.dumps(dict(association), ensure_ascii=False, sort_keys=True)},
    }
    if ours:
        check_id = ours[0].get("id")
        _require(isinstance(check_id, int), "已有资格 check 缺少合法 id")
        response = github_request("PATCH", f"/repos/{repository}/check-runs/{check_id}", token, payload, request_fn=request_fn)
    else:
        response = github_request("POST", f"/repos/{repository}/check-runs", token, payload, request_fn=request_fn)
    _require(isinstance(response, dict) and isinstance(response.get("id"), int), "GitHub 未返回资格 check id")
    return str(response["id"])


def collect_gate_results(repository: str, run_id: int | str, token: str, *, gate_names: tuple[str, ...] = REQUIRED_GATES, request_fn: RequestFn | None = None) -> dict[str, dict[str, Any]]:
    repository = _repository(repository)
    jobs: list[dict[str, Any]] = []
    page = 1
    while True:
        response = github_request("GET", f"/repos/{repository}/actions/runs/{run_id}/attempts/1/jobs?per_page=100&page={page}", token, request_fn=request_fn)
        _require(isinstance(response, dict) and isinstance(response.get("jobs"), list), "Actions jobs API 返回无效")
        page_jobs = [job for job in response["jobs"] if isinstance(job, dict)]
        jobs.extend(page_jobs)
        if len(page_jobs) < 100:
            break
        page += 1
    result: dict[str, dict[str, Any]] = {}
    for name in gate_names:
        matches = [job for job in jobs if job.get("name") == name]
        _require(len(matches) == 1, f"Gate {name} 必须恰有一个 job")
        job = matches[0]
        _require(job.get("run_attempt") == 1, f"Gate {name} 必须来自 attempt 1")
        _require(job.get("status") == "completed" and job.get("conclusion") == "success", f"Gate {name} 未成功完成")
        result[name] = job
    return result


def finish_check(repository: str, number: int | str, expected_head_sha: str, expected_base_sha: str, workflow_sha: str, run_id: int | str, check_id: str, app_id: int | str, sdk_source_sha: str, contract_source_sha: str, token: str, *, request_fn: RequestFn | None = None, gate_names: tuple[str, ...] = REQUIRED_GATES) -> dict[str, Any]:
    repository = _repository(repository)
    h, b, c = _sha(expected_head_sha, "H"), _sha(expected_base_sha, "B"), _sha(workflow_sha, "C")
    _sha(sdk_source_sha, "sdkSourceSha")
    _sha(contract_source_sha, "contractSourceSha")
    outcome, reason, gates, pull = "success", "全部 H1-H5 Gate 成功", {}, None
    try:
        pull = read_pull_request(repository, number, h, token, request_fn=request_fn)
        current_b = _ref_sha(github_request("GET", f"/repos/{repository}/git/ref/heads/main", token, request_fn=request_fn), "当前 main")
        _require(current_b == b, "main 在资格期间前进")
        _require(c == b, "可信 workflow C 与 B 不一致")
        gates = collect_gate_results(repository, run_id, token, gate_names=gate_names, request_fn=request_fn)
    except QualificationError as exc:
        outcome, reason = "failure", str(exc)
    payload = {
        "name": CHECK_NAME,
        "status": "completed",
        "conclusion": outcome,
        "completed_at": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "output": {
            "title": "Release Qualification " + ("passed" if outcome == "success" else "failed"),
            "summary": reason,
            "text": json.dumps({"schemaVersion": 1, "prNumber": int(number), "H": h, "B": b, "C": c, "run_id": run_id, "run_attempt": 1, "sdkSourceSha": sdk_source_sha, "contractSourceSha": contract_source_sha, "gates": sorted(gates)}, ensure_ascii=False, sort_keys=True),
        },
    }
    updated = github_request("PATCH", f"/repos/{repository}/check-runs/{check_id}", token, payload, request_fn=request_fn)
    _require(isinstance(updated, dict), "完成资格 check 未返回对象")
    return {"conclusion": outcome, "reason": reason, "pull": pull, "gates": gates, "response": updated}


def verify_merged_candidate(repository: str, number: int | str, merged_sha: str, expected_base_sha: str, expected_head_sha: str, check_id: str, app_id: int | str, run_id: int | str, token: str, *, request_fn: RequestFn | None = None) -> dict[str, Any]:
    repository = _repository(repository)
    m, b, h = _sha(merged_sha, "M"), _sha(expected_base_sha, "B"), _sha(expected_head_sha, "H")
    pull = github_request("GET", f"/repos/{repository}/pulls/{int(number)}", token, request_fn=request_fn)
    _require(pull.get("merged") is True and pull.get("merge_commit_sha") == m, "PR 未按预期合并到 M")
    merged = github_request("GET", f"/repos/{repository}/commits/{m}", token, request_fn=request_fn)
    head = github_request("GET", f"/repos/{repository}/commits/{h}", token, request_fn=request_fn)
    parents = merged.get("parents") if isinstance(merged, dict) else None
    _require(isinstance(parents, list) and len(parents) == 1 and parents[0].get("sha") == b, "M 必须只有一个父提交 B")
    _require((merged.get("commit") or {}).get("tree", {}).get("sha") == (head.get("commit") or {}).get("tree", {}).get("sha"), "M tree 必须与 H tree 相同")
    checks = github_request("GET", f"/repos/{repository}/commits/{h}/check-runs?per_page=100", token, request_fn=request_fn)
    matches = [run for run in checks.get("check_runs", []) if run.get("id") == int(check_id) and run.get("name") == CHECK_NAME]
    _require(len(matches) == 1, "M 缺少唯一 Qualification App check 关联")
    check = matches[0]
    _require(str((check.get("app") or {}).get("id")) == str(app_id), "Qualification check 来源 App 不匹配")
    _require(check.get("external_id") == EXTERNAL_ID and check.get("conclusion") == "success", "Qualification check 关联无效")
    _require(f'"run_id": {run_id}' in str((check.get("output") or {}).get("text", "")), "Qualification check 未关联本次 run")
    return {"mergedSha": m, "baseSha": b, "headSha": h, "checkId": check_id, "runId": run_id}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Host Release Qualification 的远端控制器策略入口")
    parser.add_argument("--print-policy", action="store_true", help="输出固定 gate 和官方仓库策略，不执行网络请求")
    sub = parser.add_subparsers(dest="command")
    preflight = sub.add_parser("preflight")
    preflight.add_argument("--repository", default=OFFICIAL_HOST_REPOSITORY)
    preflight.add_argument("--pr-number", required=True)
    preflight.add_argument("--expected-head-sha", required=True)
    preflight.add_argument("--workflow-sha", required=True)
    preflight.add_argument("--contract-sha")
    preflight.add_argument("--app-id")
    preflight.add_argument("--token-env", default="QUALIFICATION_TOKEN")
    begin = sub.add_parser("begin")
    begin.add_argument("--repository", default=OFFICIAL_HOST_REPOSITORY)
    begin.add_argument("--head-sha", required=True)
    begin.add_argument("--app-id", required=True)
    begin.add_argument("--association-json", default="{}")
    begin.add_argument("--token-env", default="QUALIFICATION_TOKEN")
    finish = sub.add_parser("finish")
    finish.add_argument("--repository", default=OFFICIAL_HOST_REPOSITORY)
    finish.add_argument("--pr-number", required=True)
    finish.add_argument("--head-sha", required=True)
    finish.add_argument("--base-sha", required=True)
    finish.add_argument("--workflow-sha", required=True)
    finish.add_argument("--run-id", required=True)
    finish.add_argument("--check-id", required=True)
    finish.add_argument("--app-id", required=True)
    finish.add_argument("--sdk-source-sha", required=True)
    finish.add_argument("--contract-source-sha", required=True)
    finish.add_argument("--token-env", default="QUALIFICATION_TOKEN")
    args = parser.parse_args(argv)
    if args.print_policy:
        print(json.dumps({"checkName": CHECK_NAME, "gates": REQUIRED_GATES, "repositories": sorted(OFFICIAL_REPOSITORIES)}, ensure_ascii=False))
    elif args.command == "preflight":
        token = os.environ.get(args.token_env)
        candidate = resolve_candidate(args.repository, args.expected_head_sha, args.workflow_sha, token)
        contract = resolve_contract_input(args.repository, token, candidate_sha=args.contract_sha)
        result = {**candidate, **contract, "prNumber": int(args.pr_number)}
        read_pull_request(args.repository, args.pr_number, args.expected_head_sha, token)
        if args.app_id:
            result["checkId"] = begin_check(args.repository, args.expected_head_sha, args.app_id, token or "", result)
        print(json.dumps(result, ensure_ascii=False, sort_keys=True))
    elif args.command == "begin":
        try:
            association = json.loads(args.association_json)
        except json.JSONDecodeError as exc:
            raise QualificationError(f"association JSON 无效：{exc}") from exc
        check_id = begin_check(args.repository, args.head_sha, args.app_id, os.environ.get(args.token_env, ""), association)
        print(check_id)
    elif args.command == "finish":
        result = finish_check(
            args.repository,
            args.pr_number,
            args.head_sha,
            args.base_sha,
            args.workflow_sha,
            args.run_id,
            args.check_id,
            args.app_id,
            args.sdk_source_sha,
            args.contract_source_sha,
            os.environ.get(args.token_env, ""),
        )
        print(json.dumps(result, ensure_ascii=False, sort_keys=True, default=str))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except QualificationError as exc:
        print(f"[qualification-control] 错误：{exc}", file=sys.stderr)
        raise SystemExit(1) from exc
