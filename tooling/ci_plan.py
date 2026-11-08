"""Select production inputs from the complete fixed pull-request difference."""
import hashlib
import json
from pathlib import Path
import re

from git_source import clean_commit, git, git_identity
from source_snapshot import source_files

REPOSITORIES = {"host": "FlappiBakuse/NexusPipeline", "plugins": "FlappiBakuse/NexusPipeline-Plugins"}
DEFAULTS = ["@bundled"]


def source_identity(root, commit=None):
    records = source_files(root)
    manifest = (json.dumps({"manifestAlgorithm": "nexus-source-manifest-sha256-v2-lf", "files": records},
                           ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    identity = git_identity(root, commit, records) if commit else {
        "sourceMode": "local-uncommitted", "sourceCommit": None, "sourceTree": None}
    return {**identity, "sourceSnapshotHash": hashlib.sha256(manifest).hexdigest(), "fileCount": len(records)}


def full_sha(value):
    if not value or not re.fullmatch(r"[0-9a-f]{40}", value):
        raise ValueError("PR identities require full lowercase Git SHAs")
    return value


def difference(root, base, head, checkout, local):
    if local:
        changed = git(root, "diff", "--name-only", "--no-renames", "-z", "HEAD")
        changed += git(root, "ls-files", "--others", "--exclude-standard", "-z")
        return sorted(set(changed.decode("utf-8").split("\0")) - {""}), None
    for value in (base, head, checkout):
        full_sha(value)
    clean_commit(root, checkout)
    parents = git(root, "show", "-s", "--format=%P", checkout).decode().split()
    if checkout != head and (len(parents) != 2 or set(parents) != {base, head}):
        raise ValueError("Checkout is neither the PR head nor its exact base/head merge")
    merge = git(root, "merge-base", base, head).decode().strip()
    paths = git(root, "diff", "--name-only", "--no-renames", "-z", merge, head).decode("utf-8")
    return sorted(set(paths.split("\0")) - {""}), merge


def documentation(path):
    name = Path(path).name.lower()
    return path.startswith(("docs/", "tests/", ".github/ISSUE_TEMPLATE/")) or \
        name.endswith(".md") or name in {"license", "copying"}


def plugins(root):
    result = {}
    for kind in ("managed", "specialized"):
        for file in (root / "plugins" / kind).glob("*/extension.json"):
            manifest = json.loads(file.read_bytes())
            result[manifest["extensionId"]] = {"kind": kind, "frontend": manifest.get("frontend") is not None, "specialized": manifest.get("specialized") is not None,
                                                 "path": file.parent.relative_to(root).as_posix()}
    if not result:
        raise ValueError("Plugin production declarations are missing")
    return result


def host_scope(paths):
    scope = {"projects": [], "web": False, "desktop": False, "extensions": []}
    projects = set()
    for path in paths:
        if documentation(path):
            continue
        if path.startswith("apps/web/"):
            scope["web"] = True
        elif path.startswith("apps/desktop/"):
            scope["desktop"] = True
        elif path.startswith("apps/installer/"):
            projects.add("NexusPipeline.Installer")
        elif path.startswith("apps/launcher/"):
            projects.add("NexusPipeline.Launcher")
        elif path.startswith(("sdk/", "contracts/")):
            projects.update(["NexusPipeline.Host", "NexusPipeline.Launcher", "NexusPipeline.Installer", "NexusPipeline.Maintenance"])
            scope.update(web=True, extensions=DEFAULTS)
        elif path.startswith(("src/", "apps/host/", "apps/maintenance/")):
            projects.update(["NexusPipeline.Host", "NexusPipeline.Launcher", "NexusPipeline.Installer", "NexusPipeline.Maintenance"])
        else:
            projects.update(["NexusPipeline.Host", "NexusPipeline.Launcher", "NexusPipeline.Installer", "NexusPipeline.Maintenance"])
            scope.update(web=True, desktop=True, extensions=DEFAULTS)
    scope["projects"] = sorted(projects)
    scope["web"] |= "NexusPipeline.Host" in projects
    return scope


def plugin_scope(root, paths):
    declarations = plugins(root)
    selected = set()
    for path in paths:
        matched = [name for name, item in declarations.items() if path.startswith(item["path"] + "/")]
        if matched:
            selected.update(matched)
        elif documentation(path):
            continue
        elif path.startswith("plugins/managed/frontend-ui/"):
            selected.update(name for name, item in declarations.items() if item["frontend"])
        elif path.startswith("adapter-runtime/"):
            selected.update(name for name, item in declarations.items() if item["specialized"])
        else:
            selected.update(declarations)
    return {"projects": [], "web": False, "desktop": False, "extensions": sorted(selected)}


def make_plan(root, kind, base, head, checkout, local=False, partner_root=None):
    if kind not in REPOSITORIES:
        raise ValueError("Unknown CI repository kind")
    paths, merge = difference(root, base, head, checkout, local)
    forbidden = [path for path in paths if kind == "plugins" and
                 (path in ("catalog.json", "metadata/catalog/stable.json", "metadata/releases/stable-state.json") or path.startswith("packages/"))]
    scope = host_scope(paths) if kind == "host" else plugin_scope(root, paths)
    partner_kind = "plugins" if kind == "host" else "host"
    partner_required = bool(scope["extensions"])
    partner = None
    if partner_required:
        if local:
            if partner_root is None:
                raise ValueError("Local production plans require an explicit partner root")
            partner = {"repository": REPOSITORIES[partner_kind], "ref": "local-uncommitted",
                       "source": source_identity(partner_root)}
        else:
            url = "https://github.com/" + REPOSITORIES[partner_kind] + ".git"
            values = git(root, "ls-remote", "--exit-code", url, "refs/heads/develop").decode().splitlines()
            if len(values) != 1:
                raise ValueError("Partner develop is unavailable")
            partner = {"repository": REPOSITORIES[partner_kind], "ref": "refs/heads/develop",
                       "commit": full_sha(values[0].split()[0])}
    production = bool(scope["projects"] or scope["web"] or scope["desktop"] or scope["extensions"])
    return {"format": "nexus.ci.plan", "version": 1, "kind": kind, "repository": REPOSITORIES[kind],
            "qualification": "local-plan-only" if local else "pull-request-check-only", "publishable": False,
            "base": base, "head": head, "checkout": checkout, "mergeBase": merge,
            "source": source_identity(root, None if local else checkout), "changedPaths": paths,
            "forbiddenChanges": forbidden, "scope": scope, "partner": partner,
            "partnerRequired": partner_required, "dotnetRequired": bool(scope["projects"] or scope["extensions"]),
            "nodeRequired": production, "productionRequired": production}


def verify_plan(root, plan):
    if plan.get("format") != "nexus.ci.plan" or plan.get("version") != 1 or plan.get("publishable") is not False or \
            plan.get("qualification") != "pull-request-check-only":
        raise ValueError("Only a fixed clean PR plan can execute the PR production check")
    if plan.get("kind") not in REPOSITORIES or plan.get("repository") != REPOSITORIES[plan["kind"]]:
        raise ValueError("PR plan repository identity differs")
    paths, merge = difference(root, plan["base"], plan["head"], plan["checkout"], False)
    forbidden = [path for path in paths if plan["kind"] == "plugins" and
                 (path in ("catalog.json", "metadata/catalog/stable.json", "metadata/releases/stable-state.json") or path.startswith("packages/"))]
    if forbidden or plan.get("forbiddenChanges") != forbidden:
        raise ValueError("Stable generated catalog, state and packages are publisher-owned")
    scope = host_scope(paths) if plan["kind"] == "host" else plugin_scope(root, paths)
    if paths != plan["changedPaths"] or merge != plan["mergeBase"] or scope != plan["scope"] or \
            source_identity(root, plan["checkout"]) != plan["source"]:
        raise ValueError("PR source or production scope changed after planning")
    required = {"productionRequired": bool(scope["projects"] or scope["web"] or scope["desktop"] or scope["extensions"]),
                "partnerRequired": bool(scope["extensions"]),
                "dotnetRequired": bool(scope["projects"] or scope["extensions"])}
    required["nodeRequired"] = required["productionRequired"]
    if any(plan.get(name) is not value for name, value in required.items()) or \
            (plan["partner"] is not None) != required["partnerRequired"]:
        raise ValueError("PR toolchain or partner selection differs from its production scope")
    if required["partnerRequired"]:
        partner = plan["partner"]
        other = "plugins" if plan["kind"] == "host" else "host"
        if set(partner) != {"repository", "ref", "commit"} or partner["repository"] != REPOSITORIES[other] or \
                partner["ref"] != "refs/heads/develop":
            raise ValueError("PR partner does not use the fixed official develop source")
        full_sha(partner["commit"])
