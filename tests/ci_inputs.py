"""Validate immutable cross-repository PR inputs using read-only GitHub evidence."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import urllib.request


REPOSITORIES = ("FlappiBakuse/NexusPipeline", "FlappiBakuse/NexusPipeline-Plugins")
MARKER = "nexus-ci-pair"
FIELDS = {"repository", "prNumber", "baseSha", "headSha", "testedSha", "treeSha",
          "policyDigest", "inputLockDigest", "controllerSha", "controllerDigest"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def digest(value):
    return hashlib.sha256(canonical(value)).hexdigest()


def parse(text, limit=65536):
    def unique(items):
        result = {}
        for key, value in items:
            require(key not in result, "Duplicate input property")
            result[key] = value
        return result
    require(isinstance(text, str) and len(text.encode("utf-8")) <= limit, "Oversized input")
    return json.loads(text, object_pairs_hook=unique,
                      parse_constant=lambda _: require(False, "Nonfinite input"))


def source_pair(body):
    body = body or ""
    blocks = re.findall(r"(?m)^```nexus-ci-pair\r?\n([\s\S]*?)^```[ \t]*\r?$", body)
    if not blocks:
        require(MARKER not in body, "Malformed pair block")
        return None
    require(len(blocks) == 1 and body.count(MARKER) == 1, "Duplicate pair block")
    pair = parse(blocks[0])
    require(isinstance(pair, dict) and set(pair) == {"schemaVersion", "purpose", "sources", "pairDigest"}, "Invalid pair fields")
    require(type(pair["schemaVersion"]) is int and pair["schemaVersion"] == 1
            and pair["purpose"] == "cross-repository-contract", "Unsupported pair")
    require(isinstance(pair["sources"], list) and len(pair["sources"]) == 2, "Expected Host and Plugins sources")
    for repo, source in zip(REPOSITORIES, pair["sources"]):
        require(isinstance(source, dict) and set(source) == FIELDS and source["repository"] == repo, "Invalid pair source")
        require(type(source["prNumber"]) is int and source["prNumber"] > 0, "Invalid PR number")
        for field in FIELDS - {"repository", "prNumber"}:
            length = 64 if field.endswith("Digest") else 40
            require(isinstance(source[field], str) and re.fullmatch("[a-f0-9]{%d}" % length, source[field]), "Invalid source " + field)
    require(pair["pairDigest"] == digest({key: value for key, value in pair.items() if key != "pairDigest"}), "Pair digest mismatch")
    return pair


def api(route):
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    require(token and os.environ.get("GITHUB_API_URL", "https://api.github.com") == "https://api.github.com", "Official read-only API required")
    request = urllib.request.Request("https://api.github.com" + route,
        headers={"Authorization": "Bearer " + token, "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(request, timeout=8) as response:
        data = response.read(8 * 1024 * 1024 + 1)
    require(len(data) <= 8 * 1024 * 1024, "Oversized API response")
    return json.loads(data)


def file_digest(read, repo, sha, name):
    blob = read(f"/repos/{repo}/contents/{name}?ref={sha}")
    require(blob.get("type") == "file" and blob.get("encoding") == "base64", "Invalid input blob")
    raw = base64.b64decode(blob["content"])
    require(not raw.startswith(b"\xef\xbb\xbf"), "Input BOM")
    return hashlib.sha256(raw.decode("utf-8").replace("\r\n", "\n").encode("utf-8")).hexdigest()


def control_digest(read, repo, sha):
    tree = read(f"/repos/{repo}/git/trees/{sha}?recursive=1")
    require(tree.get("truncated") is False and isinstance(tree.get("tree"), list), "Incomplete controller tree")
    entries = [{"path": row["path"], "blobSha": row["sha"]} for row in tree["tree"]
               if row["type"] == "blob" and row["path"].startswith(("tests/", "tools/", ".github/workflows/"))]
    require(entries, "Empty controller")
    return digest(sorted(entries, key=lambda item: item["path"]))


def validate_pair(pair, read=api):
    for source in pair["sources"]:
        repo = source["repository"]
        pull = read(f"/repos/{repo}/pulls/{source['prNumber']}")
        require(pull.get("state") == "open" and pull.get("number") == source["prNumber"]
                and pull.get("base", {}).get("repo", {}).get("full_name") == repo
                and pull.get("head", {}).get("repo", {}).get("full_name") == repo
                and pull.get("base", {}).get("ref") == "main"
                and pull.get("base", {}).get("sha") == source["baseSha"]
                and pull.get("head", {}).get("sha") == source["headSha"]
                and pull.get("merge_commit_sha") == source["testedSha"], "Pair PR changed or is foreign")
        require(source_pair(pull.get("body")) == pair, "PR pair descriptions differ")
        ref = read(f"/repos/{repo}/git/ref/pull/{source['prNumber']}/merge")
        require(ref.get("object", {}).get("sha") == source["testedSha"], "Pair merge ref changed")
        commit = read(f"/repos/{repo}/git/commits/{source['testedSha']}")
        require(commit.get("sha") == source["testedSha"] and commit.get("tree", {}).get("sha") == source["treeSha"]
                and [parent.get("sha") for parent in commit.get("parents", [])] == [source["baseSha"], source["headSha"]], "Pair merge tree or parents differ")
        require(file_digest(read, repo, source["testedSha"], "tests/policy.json") == source["policyDigest"], "Pair policy changed")
        lock = "plugins.lock.json" if repo == REPOSITORIES[0] else "tests/inputs.lock.json"
        require(file_digest(read, repo, source["testedSha"], lock) == source["inputLockDigest"], "Pair input lock changed")
        ancestry = read(f"/repos/{repo}/compare/{source['controllerSha']}...main")
        require(ancestry.get("status") in ("ahead", "identical")
                and ancestry.get("merge_base_commit", {}).get("sha") == source["controllerSha"], "Controller is not reviewed main history")
        require(control_digest(read, repo, source["controllerSha"]) == source["controllerDigest"], "Controller digest mismatch")
    return pair


def resolve(repository, pr, *, head=None, base=None, tested=None, read=api):
    pull = read(f"/repos/{repository}/pulls/{pr}")
    pair = source_pair(pull.get("body"))
    if pair is None:
        return None
    validate_pair(pair, read)
    require(repository in REPOSITORIES, "Foreign repository")
    own = pair["sources"][REPOSITORIES.index(repository)]
    require(own["prNumber"] == pr, "Pair PR mismatch")
    for name, value in (("headSha", head), ("baseSha", base), ("testedSha", tested)):
        require(value is None or own[name] == value, "Producer " + name + " differs from pair")
    return pair


def binding(pair, repository, run_id, attempt, controller_sha):
    source = pair["sources"][REPOSITORIES.index(repository)]
    require(source["controllerSha"] == controller_sha, "Running controller differs from pair")
    require(type(run_id) is int and run_id > 0 and type(attempt) is int and attempt > 0, "Invalid producer attempt")
    return digest({"pairDigest": pair["pairDigest"], "repository": repository,
                   "runId": run_id, "attempt": attempt, "controllerSha": controller_sha,
                   "producerPath": ".github/workflows/ci.yml", "event": "pull_request"})


def create_pair(numbers, read=api):
    sources = []
    for repo, number in zip(REPOSITORIES, numbers):
        require(type(number) is int and number > 0, "Invalid PR number")
        pull = read(f"/repos/{repo}/pulls/{number}")
        tested = read(f"/repos/{repo}/git/ref/pull/{number}/merge")["object"]["sha"]
        commit = read(f"/repos/{repo}/git/commits/{tested}")
        controller = read(f"/repos/{repo}/git/ref/heads/main")["object"]["sha"]
        lock = "plugins.lock.json" if repo == REPOSITORIES[0] else "tests/inputs.lock.json"
        sources.append({"repository": repo, "prNumber": number, "baseSha": pull["base"]["sha"],
                        "headSha": pull["head"]["sha"], "testedSha": tested, "treeSha": commit["tree"]["sha"],
                        "policyDigest": file_digest(read, repo, tested, "tests/policy.json"),
                        "inputLockDigest": file_digest(read, repo, tested, lock), "controllerSha": controller,
                        "controllerDigest": control_digest(read, repo, controller)})
    pair = {"schemaVersion": 1, "purpose": "cross-repository-contract", "sources": sources}
    pair["pairDigest"] = digest(pair)
    source_pair("```nexus-ci-pair\n" + canonical(pair).decode("utf-8") + "\n```")
    return pair


def merged_host(sha, read=api):
    require(isinstance(sha, str) and re.fullmatch(r"[a-f0-9]{40}", sha), "Invalid default Host SHA")
    result = read(f"/repos/{REPOSITORIES[0]}/compare/{sha}...main")
    require(result.get("status") in ("ahead", "identical")
            and result.get("merge_base_commit", {}).get("sha") == sha, "Default Host input is not merged main history")
    return sha


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--create-pair", nargs=2, type=int, metavar=("HOST_PR", "PLUGINS_PR"))
    parser.add_argument("--default-host-sha")
    parser.add_argument("--plan")
    parser.add_argument("--repository", choices=REPOSITORIES)
    parser.add_argument("--pr", type=int)
    parser.add_argument("--head")
    parser.add_argument("--base")
    parser.add_argument("--tested")
    args = parser.parse_args()
    if args.default_host_sha:
        require(not any((args.create_pair, args.plan, args.repository, args.pr, args.head, args.base, args.tested)), "Conflicting input modes")
        print(merged_host(args.default_host_sha))
        return
    if args.create_pair:
        require(not any((args.plan, args.repository, args.pr, args.head, args.base, args.tested)), "Conflicting input modes")
        print(canonical(create_pair(args.create_pair)).decode("utf-8"))
        return
    if args.plan:
        require(not any((args.repository, args.pr, args.head, args.base, args.tested)), "Conflicting input modes")
        plan = parse(Path(args.plan).read_text(encoding="utf-8"), limit=4 * 1024 * 1024)
        pair = plan.get("inputPair")
        if pair is not None:
            source_pair("```nexus-ci-pair\n" + canonical(pair).decode("utf-8") + "\n```")
        print(canonical(pair).decode("utf-8"))
        return
    require(all((args.repository, args.pr, args.head, args.base, args.tested)), "Complete PR identity required")
    print(canonical(resolve(args.repository, args.pr, head=args.head, base=args.base, tested=args.tested)).decode("utf-8"))


if __name__ == "__main__":
    main()
