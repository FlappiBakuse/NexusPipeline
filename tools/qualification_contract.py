"""Qualification 远端证明的纯结构规则；不执行 GitHub API 或产品代码。"""

from __future__ import annotations

import json
import re
from typing import Any, Iterable


FULL_SHA = re.compile(r"^[0-9a-f]{40}$")
HOST_REPOSITORY = "FlappiBakuse/NexusPipeline"
PLUGINS_REPOSITORY = "FlappiBakuse/NexusPipeline-Plugins"


class ContractError(ValueError):
    """远端对象不符合资格证明合同。"""


def require_sha(value: Any, label: str) -> str:
    if not isinstance(value, str) or FULL_SHA.fullmatch(value) is None:
        raise ContractError(f"{label} 必须是完整 40 位小写 SHA")
    return value


def make_external_id(repository: str, head_sha: str, base_sha: str, workflow_sha: str, run_id: int, run_attempt: int) -> str:
    require_sha(head_sha, "H")
    require_sha(base_sha, "B")
    require_sha(workflow_sha, "C")
    if not isinstance(run_id, int) or isinstance(run_id, bool) or run_id <= 0:
        raise ContractError("runId 必须是正整数")
    if not isinstance(run_attempt, int) or isinstance(run_attempt, bool) or run_attempt <= 0:
        raise ContractError("runAttempt 必须是正整数")
    if not repository or ":" in repository:
        raise ContractError("repository 无效")
    return f"{repository}:qualification:{head_sha}:{base_sha}:{workflow_sha}:{run_id}:{run_attempt}"


def parse_strict_json(text: str) -> dict[str, Any]:
    def pairs(items: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in items:
            if key in result:
                raise ContractError(f"证明 JSON 存在重复键：{key}")
            result[key] = value
        return result

    if not isinstance(text, str) or len(text.encode("utf-8")) > 32768:
        raise ContractError("证明 JSON 类型或大小无效")
    try:
        value = json.loads(text, object_pairs_hook=pairs, parse_constant=lambda constant: (_ for _ in ()).throw(ContractError(f"JSON 常量无效：{constant}")))
    except (TypeError, json.JSONDecodeError) as exc:
        raise ContractError("证明 JSON 无效") from exc
    if not isinstance(value, dict):
        raise ContractError("证明 JSON 根节点必须是对象")
    return value


def validate_proof(
    value: dict[str, Any],
    *,
    repository: str,
    head_sha: str,
    base_sha: str,
    workflow_sha: str,
    run_id: int,
    run_attempt: int,
    app_id: int,
    gate_names: Iterable[str],
    sdk_source_sha: str | None = None,
    contract_source_sha: str | None = None,
) -> dict[str, Any]:
    gates = tuple(gate_names)
    expected_keys = {"schemaVersion", "repository", "headSha", "baseSha", "workflowSha", "runId", "runAttempt", "appId", "sdkSourceSha", "contractSourceSha", "gates"}
    if set(value) != expected_keys:
        raise ContractError("证明 JSON 字段集合不正确")
    if type(value.get("schemaVersion")) is not int or value.get("schemaVersion") != 1 or value.get("repository") != repository:
        raise ContractError("证明 JSON schema/repository 不正确")
    for field, expected in (("headSha", head_sha), ("baseSha", base_sha), ("workflowSha", workflow_sha)):
        if value.get(field) != expected:
            raise ContractError(f"证明 JSON {field} 不匹配")
        require_sha(value[field], field)
    for field, expected in (("runId", run_id), ("runAttempt", run_attempt), ("appId", app_id)):
        actual = value.get(field)
        if not isinstance(actual, int) or isinstance(actual, bool) or actual != expected:
            raise ContractError(f"证明 JSON {field} 不匹配")
    if repository == HOST_REPOSITORY:
        if sdk_source_sha != "" or value.get("sdkSourceSha") != "":
            raise ContractError("Host 证明不得携带外部 managed SDK SHA")
        if not isinstance(contract_source_sha, str) or value.get("contractSourceSha") != contract_source_sha:
            raise ContractError("证明 JSON contractSourceSha 不匹配")
        require_sha(value["contractSourceSha"], "contractSourceSha")
    elif repository == PLUGINS_REPOSITORY:
        if not isinstance(sdk_source_sha, str) or not isinstance(contract_source_sha, str):
            raise ContractError("Plugins 证明缺少 SDK/契约 SHA")
        if value.get("sdkSourceSha") != sdk_source_sha or value.get("contractSourceSha") != contract_source_sha:
            raise ContractError("证明 JSON SDK/契约 SHA 不匹配")
        require_sha(value["sdkSourceSha"], "sdkSourceSha")
        require_sha(value["contractSourceSha"], "contractSourceSha")
        if value["sdkSourceSha"] != value["contractSourceSha"]:
            raise ContractError("Plugins 证明的 SDK 与契约 SHA 必须一致")
    else:
        raise ContractError("证明 JSON repository 不在官方范围")
    if value.get("workflowSha") != value.get("baseSha"):
        raise ContractError("可信 workflow SHA 必须等于资格启动时的 main SHA")
    actual_gates = value.get("gates")
    if not isinstance(actual_gates, dict) or set(actual_gates) != set(gates) or any(actual_gates[name] != "success" for name in gates):
        raise ContractError("证明 JSON gates 不完整或未全部 success")
    return value


def validate_run(
    run: dict[str, Any],
    *,
    repository: str,
    run_id: int,
    run_attempt: int,
    workflow_sha: str,
    workflow_path: str,
    require_completed: bool = False,
) -> dict[str, Any]:
    if not isinstance(run, dict):
        raise ContractError("Actions run 必须是对象")
    if run.get("id") != run_id or run.get("run_attempt") != run_attempt:
        raise ContractError("Actions run id/attempt 不匹配")
    repo = (run.get("repository") or {}).get("full_name")
    if repo != repository:
        raise ContractError("Actions run repository 不匹配")
    if run.get("event") != "workflow_dispatch" or run.get("head_branch") != "main":
        raise ContractError("Qualification run 必须由 main 上的 workflow_dispatch 启动")
    if run.get("head_sha") != workflow_sha or run.get("path") != workflow_path:
        raise ContractError("Actions run workflow 来源不匹配")
    if require_completed:
        if run.get("status") != "completed" or run.get("conclusion") != "success":
            raise ContractError("Qualification run 尚未以 success 完成")
    elif run.get("status") not in {"in_progress", "completed"} or run.get("conclusion") not in {None, "success"}:
        raise ContractError("Qualification run 已失败或被取消")
    return run


def validate_jobs(
    jobs: list[dict[str, Any]],
    *,
    request_run_id: int,
    request_attempt: int,
    gate_names: Iterable[str],
    execute_step_name: str = "Execute gate",
) -> dict[str, dict[str, Any]]:
    result: dict[str, dict[str, Any]] = {}
    for name in gate_names:
        matches = [job for job in jobs if job.get("name") == name]
        if len(matches) != 1:
            raise ContractError(f"Gate {name} 必须恰有一个 job")
        job = matches[0]
        if job.get("run_id") != request_run_id:
            raise ContractError(f"Gate {name} run_id 不匹配")
        if job.get("run_attempt") not in (None, request_attempt):
            raise ContractError(f"Gate {name} run_attempt 不匹配")
        if job.get("status") != "completed" or job.get("conclusion") != "success":
            raise ContractError(f"Gate {name} 未成功完成")
        steps = job.get("steps")
        if not isinstance(steps, list):
            raise ContractError(f"Gate {name} 缺少 steps")
        execute = [step for step in steps if isinstance(step, dict) and step.get("name") == execute_step_name]
        if len(execute) != 1 or execute[0].get("status") != "completed" or execute[0].get("conclusion") != "success":
            raise ContractError(f"Gate {name} 的 Execute gate 未真实成功")
        result[name] = job
    return result


def validate_squash(merged: dict[str, Any], *, base_sha: str, head_tree_sha: str) -> None:
    parents = merged.get("parents")
    tree = merged.get("tree")
    if not isinstance(parents, list) or len(parents) != 1 or not isinstance(parents[0], dict) or parents[0].get("sha") != base_sha:
        raise ContractError("M 必须只有一个父提交 B")
    if not isinstance(tree, dict) or tree.get("sha") != head_tree_sha:
        raise ContractError("M tree 必须与 H tree 相同")
