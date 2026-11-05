"""Check handwritten names; compiler syntax handles C# declarations after build."""
import ast
import json
from pathlib import Path
import re
import subprocess


def word_count(name):
    name = re.sub(r"^I(?=[A-Z][a-z])", "", name)
    name = name.replace("BetterGi", "Bettergi").replace("MuMu", "Mumu")
    return len(re.findall(r"[A-Z]+(?=[A-Z][a-z]|[0-9]|$)|[A-Z]?[a-z]+[0-9]*|[0-9]+", name))


def check_names(root, records, generated):
    errors = []
    fixed_files = {
        "sdk/dotnet/Media/IMediaPreviewRegistrationPort.cs",
        "sdk/dotnet/Scheduling/IScheduledWorkPlanningHandler.cs",
    }
    observed = set()
    for record in records:
        relative = record["path"]
        path = Path(relative)
        if relative in generated:
            continue
        if path.suffix in {".cs", ".py", ".mjs", ".cjs", ".ts", ".js", ".vue", ".css"}:
            if word_count(path.stem) > 3:
                if relative not in fixed_files:
                    errors.append(f"{relative}: long handwritten filename")
                observed.add(relative)
        if path.suffix == ".py":
            for node in ast.walk(ast.parse((root / path).read_text(encoding="utf-8"))):
                if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)) and word_count(node.name) > 3:
                    errors.append(f"{relative}:{node.lineno}: long declaration {node.name}")
    if observed != fixed_files:
        errors.append("Frozen SDK filename exception set changed")
    return errors


def verify_names(root, output, run_root, env, records):
    from contract_codegen import expected_outputs
    from source_snapshot import write_json

    generated = expected_outputs(root)
    files = [item["path"] for item in records if item["path"] not in generated]
    exceptions = json.loads((root / "tooling/naming-exceptions.json").read_bytes())
    inputs = run_root / "naming-inputs.json"
    write_json(inputs, {"files": files, "exceptionFiles": exceptions})
    command = ["dotnet", str(output / "build/bin/NexusPipeline.Boundaries/release/NexusPipeline.Boundaries.dll"),
               "--source-names", str(root), str(inputs)]
    report = run_root / "source-names.json"
    with report.open("w", encoding="utf-8", newline="\n") as log:
        code = subprocess.run(command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT).returncode
    return {"command": command, "exitCode": code, "nativeReport": str(report)}
