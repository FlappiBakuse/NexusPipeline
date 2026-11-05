"""Execute a fixed PR production closure through the existing product owners."""
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys
import time

from ci_assets import desktop_archive, maa_runtime
from ci_plan import REPOSITORIES, full_sha, plugins, source_identity, verify_plan


def external(root, partner, output):
    output = output.absolute()
    if any(path.is_symlink() or path.is_junction() for path in (output, *output.parents)):
        raise ValueError("CI output cannot cross a link")
    output = output.resolve()
    for source in (root, partner):
        if source and (source == output or source in output.parents or output in source.parents):
            raise ValueError("CI output must be disjoint from both input repositories")
    if output.exists() and any(output.iterdir()):
        raise ValueError("CI output must be new or empty")
    output.mkdir(parents=True, exist_ok=True)
    return output


def run_check(root, kind, plan, partner, output):
    if plan["kind"] != kind or plan["repository"] != REPOSITORIES[kind]:
        raise ValueError("PR plan belongs to a different repository")
    verify_plan(root, plan)
    output = external(root, partner, output)
    environment = os.environ.copy()
    temporary = output / "cache/tmp"
    temporary.mkdir(parents=True)
    environment.update(TEMP=str(temporary), TMP=str(temporary), PYTHONDONTWRITEBYTECODE="1",
                       npm_config_cache=str(output / "cache/npm"))
    report = {"status": "IN_PROGRESS", "qualification": "pull-request-check-only", "publishable": False,
              "plan": plan, "commands": [], "startedAt": datetime.now(timezone.utc).isoformat()}

    def save():
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    def run(label, command, cwd):
        started = time.monotonic()
        log = output / (label + ".log")
        with log.open("w", encoding="utf-8") as stream:
            result = subprocess.run(command, cwd=cwd, env=environment, stdout=stream, stderr=subprocess.STDOUT)
        report["commands"].append({"label": label, "command": command, "cwd": str(cwd),
            "exitCode": result.returncode, "seconds": time.monotonic() - started, "log": str(log)})
        save()
        if result.returncode:
            raise ValueError("Production input failed: " + label + "; see " + str(log))
        return log.read_text(encoding="utf-8")

    def python(label, repository, script, *arguments):
        return run(label, [sys.executable, "-B", str(repository / script), *map(str, arguments)], repository)

    def clone(repository, commit, destination):
        run("consumer-clone", ["git", "clone", "--no-hardlinks", "--no-checkout", "--depth=1", "--no-tags",
                               repository.as_uri(), str(destination)], output)
        run("consumer-checkout", ["git", "-c", "core.autocrlf=false", "checkout", "--detach", commit], destination)
        return destination

    save()
    try:
        if kind == "host" and any(path == "update-policy.json" or path.startswith("metadata/updates/")
                                  for path in plan["changedPaths"]):
            from legacy_updates import check
            report["discovery"] = check(root, plan["base"])
            save()
        if not plan["productionRequired"]:
            report.update(status="PASS", scope="No production input changes; no product compiler or tests invoked")
            return report
        scope = plan["scope"]
        host = root if kind == "host" else partner
        consumer = None
        partner_before = None
        if plan["partnerRequired"]:
            other = "plugins" if kind == "host" else "host"
            expected = plan["partner"]
            if partner is None or expected["repository"] != REPOSITORIES[other] or expected["ref"] != "refs/heads/develop":
                raise ValueError("Fixed partner checkout is required")
            partner_before = source_identity(partner, full_sha(expected["commit"]))
            report["partnerSource"] = partner_before
            plugin_root = partner if kind == "host" else root
            plugin_commit = expected["commit"] if kind == "host" else plan["checkout"]
            consumer = clone(plugin_root, plugin_commit, output / "plugin-consumer")
            if source_identity(consumer, plugin_commit) != (partner_before if kind == "host" else plan["source"]):
                raise ValueError("Isolated consumer differs from its fixed source")
        build = output / "host-build"
        projects = scope["projects"] if kind == "host" else []
        if scope["extensions"] and not projects:
            projects = ["NexusPipeline.Extensibility", "NexusPipeline.Boundaries"]
        if projects:
            arguments = ["build", "--profile", "production", "--output", build]
            for project in projects:
                arguments.extend(["--project", project])
            python("host-production", host, "tooling/nexus.py", *arguments)
        elif scope["web"]:
            python("web-production", host, "tooling/web_build.py", "--output", build)
        if scope["desktop"]:
            archive = desktop_archive(host, output / "binary-inputs")
            python("desktop-production", host, "tooling/nexus.py", "desktop-bundle", "--runtime-archive", archive,
                   "--output", output / "desktop")
        if scope["extensions"]:
            package_plugins(python, run, host, consumer, build, output, scope["extensions"], report)
        verify_plan(root, plan)
        if partner_before is not None and source_identity(partner, plan["partner"]["commit"]) != partner_before:
            raise ValueError("Partner source changed during its production check")
        report.update(status="PASS", scope="Selected production compilation, SDK consumers and package assembly; no project tests")
        return report
    except Exception as failure:
        report.update(status="FAIL", error=str(failure))
        raise
    finally:
        report["completedAt"] = datetime.now(timezone.utc).isoformat()
        save()


def package_plugins(python, run, host, consumer, build, output, selected, report):
    declarations = plugins(consumer)
    if any(name not in declarations for name in selected):
        raise ValueError("Selected plugin is absent from the fixed source")
    python("sdk-packages", host, "tooling/nexus.py", "sdk-packages", "--build-root", build,
           "--consumer-root", consumer / "tests/consumers", "--output", output / "sdk", "--cache", output / "cache", "--compile-only")
    sdk = Path(json.loads((output / "sdk/sdk-consumers-current.json").read_bytes())["receipt"])
    python("sdk-contract-projection", consumer, "tooling/import_sdk.py", "--receipt", sdk)
    frontend_names = [name for name in selected if declarations[name]["frontend"]]
    if frontend_names:
        command = ["node", str(consumer / "tooling/build_frontend.mjs"), "--sdk-root", str(host),
                   "--work-root", str(output / "frontend-work"), "--output", str(output / "frontend")]
        for name in frontend_names:
            command.extend(["--extension", name])
        run("plugin-frontend", command, consumer)
    runtime = maa_runtime(consumer, output / "binary-inputs/maa") if "maa-framework" in selected else None
    packages = []
    for name in selected:
        kind = declarations[name]["kind"]
        text = python(name + "-compile", consumer, "tooling/build_" + kind + "_slice.py", "--extension", name,
                      "--sdk-receipt", sdk, "--output", output / "plugin-build")
        results = [json.loads(line) for line in text.splitlines() if line.startswith("{")]
        result = next(value for value in reversed(results) if "receipt" in value)
        arguments = ["--build-receipt", result["receipt"], "--sdk-receipt", sdk, "--output", output / "packages"]
        if name in frontend_names:
            arguments.extend(["--frontend-receipt", output / "frontend/build-receipt.json"])
        if name == "maa-framework":
            arguments.extend(["--runtime-receipt", runtime])
        text = python(name + "-package", consumer, "tooling/package_extension.py", *arguments)
        result = json.loads(text.splitlines()[-1])
        packages.append({"extensionId": name, **result})
    report["packages"] = packages
    report["sdkReceipt"] = str(sdk)
