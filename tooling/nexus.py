"""NexusPipeline local source and build entry point."""
import argparse
import json
from pathlib import Path
import sys

sys.dont_write_bytecode = True

from build_product import build_product
from desktop_bundle import bundle_desktop
from source_checks import check_source
from source_snapshot import export_source
from contract_checks import check_contracts
from test_runner import run_tests
from dependency_locks import update_locks
from storage_experiment import run_experiment
from sdk_packages import build_sdk
from product_publish import publish_product
from product_assembly import assemble_product


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    snapshot = commands.add_parser("snapshot", help="Export original source bytes and identity")
    snapshot.add_argument("--output", type=Path, required=True)
    snapshot.add_argument("--git-commit", help="Export only the exact clean commit; this grants no publication qualification")
    check = commands.add_parser("check", help="Check implemented static constraints")
    check.add_argument("--scope", choices=["source", "contracts"], required=True)
    build = commands.add_parser("build", help="Compile a frozen export with external outputs")
    build.add_argument("--profile", choices=["production", "test"], required=True)
    build.add_argument("--output", type=Path, required=True)
    build.add_argument("--project", action="append", help="Compile only explicitly selected project closures")
    test = commands.add_parser("test", help="Run implemented real suites with verified native reports")
    test.add_argument("--group", choices=["contracts", "core", "windows", "windows-secrets", "storage", "storage-checkin", "storage-wal", "storage-startup", "storage-automation", "storage-execution", "storage-android", "storage-provider", "storage-judgement", "storage-file-logs", "storage-settings", "storage-secrets", "storage-config-revisions", "storage-config-output", "storage-config-controller", "storage-config-cross-volume", "tooling", "host-boundary"], required=True)
    test.add_argument("--build-root", type=Path, required=True)
    test.add_argument("--output", type=Path, required=True)
    test.add_argument("--method", action="append", help="Select exact native methods within the suite; repeat to batch them. Python accepts one exact ID.")
    test.add_argument("--desktop-bundle", type=Path, help="Owned complete Electron bundle; required only by the exact desktop-maintenance method")
    test.add_argument("--fixture-volume-root", type=Path, help="Explicit existing second-volume root; required only by storage-config-cross-volume")
    dependencies = commands.add_parser("dependencies", help="Update locks from current explicit project pins")
    dependencies.add_argument("--update-locks", action="store_true", required=True)
    dependencies.add_argument("--build-root", type=Path, required=True)
    dependencies.add_argument("--project", action="append", help="Resolve only explicitly selected project closures")
    experiment = commands.add_parser("storage-experiment", help="Run the actual bounded history and image dataset fixture")
    experiment.add_argument("--build-root", type=Path, required=True)
    experiment.add_argument("--output", type=Path, required=True)
    desktop = commands.add_parser("desktop-bundle", help="Assemble the pinned Electron runtime and selected desktop shell")
    desktop.add_argument("--runtime-archive", type=Path, required=True)
    desktop.add_argument("--output", type=Path, required=True)
    sdk = commands.add_parser("sdk-packages", help="Package the current SDK, compile consumers and optionally test lifecycle")
    sdk.add_argument("--build-root", type=Path, required=True)
    sdk.add_argument("--consumer-root", type=Path, required=True)
    sdk.add_argument("--output", type=Path, required=True)
    sdk.add_argument("--cache", type=Path, required=True)
    sdk.add_argument("--compile-only", action="store_true", help="Package and compile consumers without lifecycle tests")
    product = commands.add_parser("product-publish", help="Publish framework-dependent single-file Windows product inputs")
    product.add_argument("--build-root", type=Path, required=True)
    product.add_argument("--output", type=Path, required=True)
    product.add_argument("--runtime-archive", type=Path, required=True)
    product.add_argument("--profile", choices=["production", "test"], required=True)
    assembly = commands.add_parser("product-assemble", help="Assemble verified publications into the normal application layout")
    assembly.add_argument("--publication-root", type=Path, required=True)
    assembly.add_argument("--output", type=Path, required=True)
    assembly.add_argument("--bundle-root", type=Path)
    assembly.add_argument("--replace-application", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    try:
        if args.command == "product-assemble":
            print(json.dumps(assemble_product(args.publication_root, args.output, args.bundle_root, args.replace_application), ensure_ascii=False, indent=2))
            return 0
        if args.command == "product-publish":
            print(json.dumps(publish_product(root, args.build_root, args.output, args.runtime_archive, args.profile), ensure_ascii=False, indent=2))
            return 0
        if args.command == "sdk-packages":
            print(json.dumps(build_sdk(root, args.build_root, args.consumer_root, args.output, args.cache, args.compile_only), ensure_ascii=False, indent=2))
            return 0
        if args.command == "desktop-bundle":
            print(json.dumps(bundle_desktop(root, args.runtime_archive, args.output), ensure_ascii=False, indent=2))
            return 0
        if args.command == "snapshot":
            print(json.dumps(export_source(root, args.output, args.git_commit), ensure_ascii=False, indent=2))
            return 0
        if args.command == "check":
            return check_source(root) if args.scope == "source" else check_contracts(root)
        if args.command == "test":
            return run_tests(root, args.build_root, args.output, args.group, args.fixture_volume_root, args.method, args.desktop_bundle)
        if args.command == "dependencies":
            return update_locks(root, args.build_root, args.project)
        if args.command == "storage-experiment":
            return run_experiment(root, args.build_root.resolve(), args.output)
        return build_product(root, args.output, args.profile, args.project)
    except (OSError, ValueError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    raise SystemExit(main())
