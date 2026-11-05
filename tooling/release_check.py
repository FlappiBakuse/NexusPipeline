"""Create source-bound Host candidate data or verify an original Actions artifact."""
import argparse
import json
import os
from pathlib import Path
import shutil

from ci_plan import source_identity
from release_candidate import extract, inventory, parse, validate
from release_github import GitHub
from release_provenance import POLICIES, positive, require, resolve_candidate, sha
from release_publish import host_plan, publish_host


def external(output, roots):
    require(output.is_absolute(), "Release output requires an explicit absolute path")
    output = output.absolute()
    for path in (output, *output.parents):
        require(not path.is_symlink() and not path.is_junction(), "Release output traverses a link")
    output = output.resolve()
    for root in roots:
        root = root.resolve(strict=True)
        require(root != output and root not in output.parents and output not in root.parents,
                "Release output must be disjoint from all source inputs")
    require(not output.exists(), "Release output requires a new owned directory")
    return output


def producer(kind):
    policy = POLICIES[kind]
    require(os.environ.get("GITHUB_REPOSITORY") == policy["repository"] and
            os.environ.get("GITHUB_EVENT_NAME") == "workflow_dispatch" and os.environ.get("GITHUB_REF") == "refs/heads/main",
            "Candidate creation requires the official manual main workflow")
    workflow = policy["repository"] + "/" + policy["workflow"] + "@refs/heads/main"
    require(os.environ.get("GITHUB_WORKFLOW_REF") == workflow, "Candidate control workflow differs")
    return {"repository": policy["repository"], "workflow": policy["workflow"],
            "workflowCommit": sha(os.environ.get("GITHUB_SHA")), "runId": positive(int(os.environ.get("GITHUB_RUN_ID", "0"))),
            "runAttempt": positive(int(os.environ.get("GITHUB_RUN_ATTEMPT", "0")))}


def create_host(args):
    source = source_identity(args.source_root.resolve(strict=True), sha(args.source_commit))
    partner = source_identity(args.partner_root.resolve(strict=True), sha(args.partner_commit))
    assets = parse(args.assets_receipt.read_bytes())
    require(assets.get("status") == "PASS_THREE_ASSETS_BUILT_NOT_QUALIFIED" and assets.get("profile") == "production",
            "Candidate requires completed production assets")
    assembly_path = Path(assets["assembly"]["path"])
    import hashlib
    require(hashlib.sha256(assembly_path.read_bytes()).hexdigest() == assets["assembly"]["sha256"], "Asset assembly receipt changed")
    assembly = parse(assembly_path.read_bytes())
    require(assembly.get("status") == "PASS_APPLICATION_ASSEMBLED" and assembly.get("profile") == "production" and
            assembly["source"]["sourceSnapshotHash"] == source["sourceSnapshotHash"], "Candidate source differs from compiled assembly")
    output = external(args.output, [args.source_root, args.partner_root, args.assets_receipt.parent])
    control = producer("host")
    output.mkdir(parents=True)
    for item in assets["assets"]:
        from release_candidate import member_path
        require(len(member_path(item["name"]).parts) == 1, "Host asset is not a basename")
        shutil.copyfile(args.assets_receipt.parent / item["name"], output / item["name"])
    files = inventory(output)
    require(sorted(files.values(), key=lambda item: item["path"]) == sorted([
        {"path": item["name"], "sizeBytes": item["sizeBytes"], "sha256": item["sha256"]} for item in assets["assets"]],
        key=lambda item: item["path"]), "Candidate asset bytes differ from their producer receipt")
    candidate = {"format": "nexus.release.candidate", "version": 1, "profile": "production", "kind": "host",
        "producer": control, "source": source, "partner": {"repository": POLICIES["stable"]["repository"], "identity": partner},
        "files": [files[name] for name in sorted(files)], "release": {"tag": "v0.18.0", "assets": sorted(files)}}
    host_plan(output, {"candidate": candidate})
    require(source_identity(args.source_root.resolve(), args.source_commit) == source and
            source_identity(args.partner_root.resolve(), args.partner_commit) == partner, "Candidate inputs changed during assembly")
    (output / "candidate.json").write_text(json.dumps(candidate, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    return {"status": "CANDIDATE_CREATED_NOT_QUALIFIED", "remoteWritten": False, "candidate": str(output / "candidate.json")}


def verify_original(args):
    root = Path(__file__).resolve().parent.parent
    output = external(args.output, [root])
    api = GitHub()
    provenance = resolve_candidate(api.fetch, args.kind, args.run_id, args.writer_id, args.artifact_id)
    if args.publish:
        control = producer(args.kind)
        require(control == provenance["writer"], "Publisher control differs from the current server-owned writer run")
        source_identity(root, control["workflowCommit"])
    output.mkdir(parents=True)
    (output / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    result = {"status": "RUNNING", "remoteWritten": False}
    try:
        archive = output / "candidate.zip"
        api.download(f"repos/{provenance['repository']}/actions/artifacts/{provenance['artifactId']}/zip", archive,
                     provenance["artifactSizeBytes"], provenance["artifactDigest"].removeprefix("sha256:"))
        directory = extract(archive, output / "candidate")
        verified = validate(directory, provenance, api.fetch)
        result = publish_host(api, directory, verified, output / "readback", args.publish)
    except Exception:
        result["status"] = "FAIL"
        raise
    finally:
        (output / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    candidate = commands.add_parser("host-candidate")
    for name in ("source-root", "partner-root", "assets-receipt", "output"):
        candidate.add_argument("--" + name, type=Path, required=True)
    for name in ("source-commit", "partner-commit"):
        candidate.add_argument("--" + name, required=True)
    verify = commands.add_parser("publish-only")
    verify.add_argument("--kind", choices=["host"], required=True)
    for name in ("run-id", "writer-id"):
        verify.add_argument("--" + name, type=int, required=True)
    verify.add_argument("--artifact-id", type=int)
    verify.add_argument("--output", type=Path, required=True)
    verify.add_argument("--publish", action="store_true")
    args = parser.parse_args()
    print(json.dumps(create_host(args) if args.command == "host-candidate" else verify_original(args), ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
