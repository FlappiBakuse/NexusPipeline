import argparse
import json
from pathlib import Path
import re


def check(host: Path, plugins: Path):
    public = host / "frontend" / "src" / "plugin-bridge"
    private = (host / "frontend" / "src").resolve()
    if not public.is_dir() or not (plugins / "plugins").is_dir():
        raise ValueError("Host bridge or fixed Plugins checkout is missing")
    violations = []
    artifacts = []
    policy = json.loads((plugins / "tests/policy.json").read_text(encoding="utf-8"))["plugins"]
    if not policy or len({name.casefold() for name in policy}) != len(policy):
        raise ValueError("Invalid registered Plugins inventory")
    for manifest in sorted((plugins / "plugins").glob("*/*/plugin.json")):
        data = json.loads(manifest.read_text(encoding="utf-8"))
        artifact = data["artifactName"]
        if (artifact not in policy or policy[artifact]["root"] != manifest.parent.relative_to(plugins).as_posix()
                or policy[artifact]["kind"] != data["kind"]):
            raise ValueError("Plugin manifest differs from registered inventory")
        artifacts.append(artifact)
        if data["kind"] == "data-specialized" and data.get("frontend") is not None:
            violations.append({"artifact": artifact, "path": str(manifest),
                               "reason": "specialized plugin declares a frontend"})
        frontend = manifest.parent / "frontend"
        if not frontend.is_dir():
            continue
        for source in frontend.rglob("*"):
            if (not source.is_file() or source.suffix not in {".ts", ".tsx", ".js", ".vue"}
                    or "node_modules" in source.parts or "dist" in source.parts):
                continue
            contents = source.read_text(encoding="utf-8")
            for match in re.finditer(r"\b(?:import|export)\s+(?:[^;\n]*?\s+from\s+)?['\"]([^'\"]+)['\"]", contents):
                target = match.group(1)
                if not target.startswith(".") and not Path(target).is_absolute():
                    continue
                resolved = (source.parent / target).resolve()
                if resolved == private or private in resolved.parents:
                    violations.append({"artifact": artifact,
                                       "path": str(source.relative_to(plugins)).replace("\\", "/"),
                                       "reason": "plugin imports private Host source"})
    if (set(artifacts) != set(policy) or len(artifacts) != len(policy)
            or len({name.casefold() for name in artifacts}) != len(artifacts)):
        raise ValueError("Fixed Plugins inventory is incomplete")
    return {"schemaVersion": 1, "status": "FAIL" if violations else "PASS",
            "hostPublicBridge": str(public), "artifacts": artifacts, "violations": violations}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host-root", type=Path, required=True)
    parser.add_argument("--plugins-root", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    report = check(args.host_root.resolve(), args.plugins_root.resolve())
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if report["status"] != "PASS":
        raise SystemExit(1)


if __name__ == "__main__":
    main()
