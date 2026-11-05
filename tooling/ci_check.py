"""Fixed-scope PR compilation and packaging; no tests or publication."""
import argparse
import json
import os
from pathlib import Path
import sys

sys.dont_write_bytecode = True

from ci_build import run_check
from ci_plan import make_plan

KIND = "host"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    plan = commands.add_parser("plan")
    for name in ("base", "head", "checkout"):
        plan.add_argument("--" + name)
    plan.add_argument("--local", action="store_true", help="Offline uncommitted scope preview; cannot run as a PR check")
    plan.add_argument("--partner-root", type=Path)
    plan.add_argument("--report", type=Path, required=True)
    run = commands.add_parser("run")
    run.add_argument("--plan", type=Path, required=True)
    run.add_argument("--partner-root", type=Path)
    run.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    partner = args.partner_root.resolve(strict=True) if args.partner_root else None
    try:
        if args.command == "plan":
            target = args.report.absolute()
            if target.exists() or any(p.is_symlink() or p.is_junction() for p in (target, *target.parents)):
                raise ValueError("CI plan output is occupied or crosses a link")
            target = target.resolve()
            if root == target or root in target.parents or partner and (partner == target or partner in target.parents):
                raise ValueError("CI plan must be outside both input repositories")
            result = make_plan(root, KIND, args.base, args.head, args.checkout, args.local, partner)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            if not args.local and os.environ.get("GITHUB_OUTPUT"):
                with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as stream:
                    for name in ("partnerRequired", "nodeRequired", "dotnetRequired"):
                        stream.write(name + "=" + str(result[name]).lower() + "\n")
                    if result["partnerRequired"]:
                        stream.write("partnerSha=" + result["partner"]["commit"] + "\n")
            print(json.dumps({"status": "PLAN_PREPARED", "report": str(target), "scope": result["scope"],
                              "qualification": result["qualification"], "forbiddenChanges": len(result["forbiddenChanges"])}))
        else:
            result = run_check(root, KIND, json.loads(args.plan.read_bytes()), partner, args.output)
            print(json.dumps({"status": result["status"], "report": str(args.output / "report.json")}))
        return 0
    except (OSError, ValueError) as error:
        print("ERROR: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
