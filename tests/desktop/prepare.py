"""Create an owned instrumented client from this run's actual application."""
import argparse
from pathlib import Path
import shutil
import subprocess

parser = argparse.ArgumentParser(allow_abbrev=False)
for name in ("application", "inputs", "output"):
    parser.add_argument("--" + name, type=Path, required=True)
args = parser.parse_args()
if args.output.exists():
    parser.error("Desktop observation output already exists")
args.output.mkdir(parents=True)
software = args.output / "software"
software.mkdir()
for name in ("NexusPipeline.exe", "README.md", "resources"):
    source = args.application / name
    if source.is_dir():
        shutil.copytree(source, software / name)
    else:
        shutil.copy2(source, software / name)
source = Path(__file__).resolve().parent
fixture = args.output / "client-fixture"
shutil.copytree(args.inputs / "desktop" / "application", fixture)
shutil.copy2(source / "client-driver.cjs", fixture / "client-driver.cjs")
subprocess.run(["node", str(source / "pack-client.mjs"), str(args.inputs), str(fixture),
                str(software / "resources/desktop/resources/app.asar")], check=True)
