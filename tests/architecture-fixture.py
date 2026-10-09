"""Exercise semantic architecture rules with a tiny actual MSBuild project."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


def run(checker, root, report):
    command = ["dotnet", str(checker), str(root), "--report", str(report)]
    return subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace")


def write(root, name, contents):
    target = root / name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text("namespace Fixture;\n" + contents if name.endswith(".cs") else contents, encoding="utf-8")


def main():
    checker = Path(sys.argv[1]).resolve()
    if not checker.is_file():
        raise ValueError("Built architecture checker is required")
    artifact_root = Path(os.environ["NEXUS_TEST_ARTIFACT_ROOT"]).resolve()
    if not artifact_root.is_dir():
        raise ValueError("Registered external test root is required")
    with tempfile.TemporaryDirectory(prefix="architecture-fixture-", dir=artifact_root) as temporary:
        root = Path(temporary).resolve()
        if artifact_root not in root.parents:
            raise ValueError("Fixture escaped external test root")
        write(root, "src/NexusPipeline.csproj", """<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup><PropertyGroup Condition=\"'$(NexusTestHost)' == 'true'\"><DefineConstants>$(DefineConstants);NEXUS_TEST_HOST</DefineConstants></PropertyGroup></Project>""")
        write(root, "src/Modules/A/A.cs", "using HostAlias = HostThing; public class A {\n#if !NEXUS_TEST_HOST\n public B? Other;\n#endif\n}\n")
        write(root, "src/Modules/B/B.cs", "public class B {\n#if NEXUS_TEST_HOST\n public A? Other;\n#endif\n}\n")
        write(root, "src/Host/HostThing.cs", "public class HostThing {}\n")
        subprocess.run(["dotnet", "restore", str(root / "src/NexusPipeline.csproj"), "-r", "win-x64", "--nologo"],
                       check=True, capture_output=True, text=True, encoding="utf-8", errors="replace")
        report = root / "report.json"
        result = run(checker, root, report)
        if result.returncode != 0 or json.loads(report.read_text())["violations"]:
            raise AssertionError(f"False architecture cycle or unused alias: {result.stderr}")
        baseline = json.loads(report.read_bytes())
        assert {(item["Mode"], item["From"], item["To"]) for item in baseline["edges"]} == {("production", "Modules.A", "Modules.B"), ("test-host", "Modules.B", "Modules.A")}
        write(root, "src/Modules/C/C.cs", "public class C { public B? Other; }\n")
        result = run(checker, root, report)
        current = json.loads(report.read_bytes())
        if result.returncode != 0 or current["status"] != "PASS" or not all(current["sourceFingerprints"].values()):
            raise AssertionError("Legal acyclic dependency must keep architecture PASS")
        baseline_path = root / "baseline.json"
        baseline_path.write_text(json.dumps(baseline), encoding="utf-8")
        tool = Path(__file__).resolve().parents[1] / "tools/governance.mjs"
        program = "const {architectureDelta}=await import(process.argv[1]);const fs=await import('node:fs');const result=architectureDelta(JSON.parse(fs.readFileSync(process.argv[2])),JSON.parse(fs.readFileSync(process.argv[3])));if(result.status!=='REVIEW'||result.addedEdges.length!==2)throw new Error(JSON.stringify(result));"
        subprocess.run(["node", "--input-type=module", "-e", program, tool.as_uri(), str(report), str(baseline_path)], check=True)
        write(root, "src/Modules/A/A.cs", "public class A { public B? Other; public HostThing? Host; public System.IServiceProvider? Provider; }\n")
        write(root, "src/Modules/B/B.cs", "public class B { public A? Other; }\n")
        write(root, "src/Modules/Settings/FooStore.cs", "public class FooStore {}\n")
        write(root, "src/ControlPlane/Controller.cs", "public class Controller { public FooStore? Store; }\n")
        result = run(checker, root, report)
        rules = {item["RuleId"] for item in json.loads(report.read_text())["violations"]}
        if result.returncode == 0 or not {"A01", "A02", "A03", "A04"}.issubset(rules):
            raise AssertionError(f"Semantic violations were missed: {rules}; {result.stderr}")
        print("A01-A04 semantic positive and negative fixtures passed")


if __name__ == "__main__":
    main()
