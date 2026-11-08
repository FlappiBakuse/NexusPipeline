"""Resolve the current solution's compile inputs independently of its audit snapshot."""
import glob
import ast
import json
import os
from pathlib import Path
import platform
import shutil
import sys
import xml.etree.ElementTree as ET

from source_snapshot import digest, source_files


def file_records(root, paths):
    records = []
    for path in sorted(set(paths), key=lambda item: item.relative_to(root).as_posix().encode()):
        if path.is_symlink() or path.is_junction() or root not in path.resolve(strict=True).parents:
            raise ValueError("Component input escaped its source owner")
        data = path.read_bytes()
        records.append({"path": path.relative_to(root).as_posix(), "sizeBytes": len(data), "sha256": digest(data)})
    return records


def python_inputs(root, entries):
    pending = [root / "tooling" / name for name in entries]
    visited = set()
    while pending:
        path = pending.pop()
        if path in visited:
            continue
        visited.add(path)
        tree = ast.parse(path.read_text(encoding="utf-8"))
        for node in ast.walk(tree):
            names = [item.name for item in node.names] if isinstance(node, ast.Import) else \
                [node.module] if isinstance(node, ast.ImportFrom) and node.module else []
            for name in names:
                candidate = root / "tooling" / (name.split('.')[0] + ".py")
                if candidate.is_file():
                    pending.append(candidate)
    return visited


def runtime_identity(executable):
    path = shutil.which(executable)
    if not path:
        return {"executable": executable, "available": False}
    resolved = Path(path).resolve(strict=True)
    return {"executable": executable, "path": str(resolved), "sha256": digest(resolved.read_bytes())}


def build_context(profile, environment=None):
    environment = os.environ if environment is None else environment
    relevant = {key: value for key, value in environment.items()
                if key.upper().startswith(("DOTNET_", "MSBUILD", "NUGET_")) or key.upper() in ("PATH", "PATHEXT")}
    return {"profile": profile, "configuration": "Release", "platform": platform.platform(),
            "machine": platform.machine(), "python": {"version": platform.python_version(),
                "sha256": digest(Path(sys.executable).read_bytes())},
            "dotnet": runtime_identity("dotnet"),
            "environmentHash": digest(json.dumps(relevant, sort_keys=True).encode())}


def conservative_records(root, expected):
    if expected is None:
        return [{key: value for key, value in record.items() if key != "mode"} for record in source_files(root)]
    paths = {root / item['path'] for item in expected}
    actual = {path for path in root.rglob('*') if path.is_file()}
    if actual != paths:
        raise ValueError('Frozen build source file set changed')
    records = file_records(root, paths)
    identity = lambda items: {item['path']: (item['sizeBytes'], item['sha256']) for item in items}
    if identity(records) != identity(expected):
        raise ValueError('Frozen build source bytes changed')
    return records


def solution_inputs(root, profile, environment=None, projects=None, source_records=None):
    root = Path(root).resolve(strict=True)
    paths = {root / name for name in ("NexusPipeline.slnx", "Directory.Build.props", "Directory.Build.targets",
                                      "NuGet.Config", "global.json")}
    paths.update(root / name for name in ("Directory.Packages.props", ".editorconfig", ".globalconfig") if (root / name).is_file())
    paths.update(python_inputs(root, ["build_product.py"]))
    listed = [root / node.attrib["Path"] for node in ET.parse(root / "NexusPipeline.slnx").findall("Project")]
    if projects is not None and not set(projects).issubset({path.stem for path in listed}):
        raise ValueError("Requested build project is not in the solution")
    pending = [path for path in listed if projects is None or path.stem in projects]
    visited, unknown = set(), []

    def expand(base, pattern):
        if '$' in pattern or ';' in pattern or '@' in pattern:
            raise ValueError("Unresolved project input: " + pattern)
        matches = [Path(name).resolve(strict=True) for name in glob.glob(str(base / pattern), recursive=True) if Path(name).is_file()]
        if not matches and not any(char in pattern for char in '*?['):
            raise ValueError("Missing declared project input: " + pattern)
        return matches

    while pending:
        project = pending.pop().resolve(strict=True)
        if project in visited:
            continue
        visited.add(project)
        paths.add(project)
        tree = ET.parse(project)
        compile_items = set(project.parent.rglob("*.cs"))
        paths.update(project.parent.rglob("*.resx"))
        if (project.parent / "packages.lock.json").exists():
            paths.add(project.parent / "packages.lock.json")
        try:
            for node in tree.iter():
                tag = node.tag
                if tag not in {"Project", "PropertyGroup", "ItemGroup", "Target", "Error", "Exec", "LogicalName",
                        "TargetFramework", "RuntimeIdentifiers", "Nullable", "ImplicitUsings", "TreatWarningsAsErrors", "Version", "AssemblyName",
                        "IsPackable", "IsTestProject", "GenerateDocumentationFile", "NexusSdkApiTool", "OutputType",
                        "ServerGarbageCollection", "PackageReference", "ProjectReference", "Reference", "Compile",
                        "EmbeddedResource", "Content", "None", "InternalsVisibleTo", "ApplicationManifest", "ApplicationIcon", "Using",
                        "PublishReadyToRunExclude", "PublishSingleFile", "SelfContained", "StaticWebAssetsEnabled"}:
                    raise ValueError("Unresolved project evaluation: " + tag)
                if tag == "Import" or tag == "Target" and node.attrib.get("BeforeTargets") != "GenerateNuspec":
                    raise ValueError("Custom project evaluation requires a complete source comparison")
                if tag == "EnableDefaultCompileItems" and node.text != "true":
                    raise ValueError("Custom default compile evaluation")
                if tag == "ProjectReference":
                    pending.extend(expand(project.parent, node.attrib["Include"]))
                elif tag == "Compile":
                    if "Include" in node.attrib:
                        included = node.attrib["Include"]
                        if included.startswith("$(NexusPluginFixturesRoot)/"):
                            continue
                        compile_items.update(expand(project.parent, included))
                    if "Remove" in node.attrib:
                        compile_items.difference_update(expand(project.parent, node.attrib["Remove"]))
                elif tag in ("EmbeddedResource", "Content", "None") and "Include" in node.attrib:
                    if node.attrib["Include"] == "$(NexusWebRoot)/**/*":
                        continue
                    copied = node.attrib.get("CopyToOutputDirectory") or node.find("CopyToOutputDirectory") is not None
                    if tag != "None" or copied:
                        paths.update(expand(project.parent, node.attrib["Include"]))
                elif tag == "ApplicationManifest" and node.text:
                    paths.update(expand(project.parent, node.text))
                elif tag == "ApplicationIcon" and node.text:
                    paths.update(expand(project.parent, node.text))
                elif tag == "Reference" and node.attrib.get("HintPath", "").startswith("$(MSBuildSDKsPath)/../Roslyn/bincore/"):
                    continue
                elif tag == "Reference" and "HintPath" in node.attrib:
                    paths.update(expand(project.parent, node.attrib["HintPath"]))
        except (ValueError, KeyError) as reason:
            unknown.append(str(reason))
        paths.update(compile_items)
        parent = project.parent
        while parent != root:
            for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config",
                         ".editorconfig", ".globalconfig"):
                if (parent / name).is_file():
                    paths.add(parent / name)
            parent = parent.parent
    for path in list(paths):
        if path.suffix in (".props", ".targets"):
            tree = ET.parse(path)
            if tree.findall(".//Import") or any(node.tag not in {"Project", "PropertyGroup", "ItemGroup", "Target", "Error",
                    "TargetFramework", "Nullable", "ImplicitUsings", "TreatWarningsAsErrors", "Deterministic", "Version",
                    "LangVersion", "DefineConstants", "UseArtifactsOutput", "ArtifactsPath", "RestorePackagesPath",
                    "EnableDefaultCompileItems", "RestorePackagesWithLockFile", "NuGetLockFilePath", "RestoreLockedMode",
                    "Using", "AssemblyMetadata", "PublishReadyToRunExclude", "FilesToBundle", "RelativePath"} for node in tree.iter()):
                unknown.append("Unresolved shared build evaluation: " + path.relative_to(root).as_posix())
    if unknown:
        records = conservative_records(root, source_records)
    else:
        records = file_records(root, paths)
    fixture_records = []
    supplied = (environment or os.environ).get("NexusPluginFixturesRoot")
    fixture_kinds = {"OwnedWriter": "owned-writer", "AndroidTool": "android-tool", "NexusPipeline.StorageTests": "storage"}
    if supplied and any(path.stem in fixture_kinds for path in visited):
        fixture_root = Path(supplied).resolve(strict=True)
        if fixture_root == root or root in fixture_root.parents:
            raise ValueError("Plugin test sources must be an explicit external producer input")
        for project in visited:
            if project.stem not in fixture_kinds:
                continue
            for file in sorted((fixture_root / fixture_kinds[project.stem]).glob("*")):
                if file.suffix not in (".cs", ".json"):
                    continue
                if file.is_symlink() or file.is_junction():
                    raise ValueError("Plugin fixture source is linked")
                data = file.read_bytes()
                fixture_records.append(dict(path=str(file), sizeBytes=len(data), sha256=digest(data)))
        if not fixture_records:
            raise ValueError("Explicit plugin fixture source is empty")
    value = {"algorithm": "nexus-solution-inputs-v1", "files": records, "pluginFixtureInputs": fixture_records,
             "projectNames": sorted(path.stem for path in visited),
             "context": build_context(profile, environment), "conservativeReasons": sorted(set(unknown))}
    return {**value, "sha256": digest(json.dumps(value, sort_keys=True, separators=(",", ":")).encode())}


def project_inputs(root, profile, environment, projects, web_component, artifacts, source_records=None):
    result = {}
    for project in projects:
        component = solution_inputs(root, profile, environment, [project], source_records)
        if "NexusPipeline.Host" in component["projectNames"]:
            component.update(webInputs=web_component, webArtifacts=artifacts)
        result[project] = component
    return result


def web_inputs(root, environment=None):
    root = Path(root).resolve(strict=True)
    paths = {path for folder in (root / "apps/web", root / "sdk/frontend/src") for path in folder.rglob("*")
             if path.is_file() and path.suffix != ".md"}
    paths.update(root / 'apps/desktop' / name for name in ('startup-content.css', 'nexuspipeline.ico'))
    paths.update(python_inputs(root, ["web_build.py"]))
    paths.update(root / "tooling" / name for name in ("frontend_names.mjs", "frontend_boundaries.mjs",
                                                     "frontend-exceptions.json", "generated_sources.json"))
    value = {"algorithm": "nexus-web-inputs-v2", "files": file_records(root, paths),
             "node": runtime_identity("node"), "context": web_context(environment)}
    return {**value, "sha256": digest(json.dumps(value, sort_keys=True, separators=(",", ":")).encode())}


def web_context(environment):
    environment = os.environ if environment is None else environment
    relevant = {key: value for key, value in environment.items()
                if key.upper().startswith(("NODE_", "NPM_", "VITE_", "ROLLUP_", "BROWSERSLIST_")) or
                key.upper() in ("PATH", "PATHEXT", "CI", "TZ", "LANG", "LC_ALL", "BROWSERSLIST")}
    return {"profile": "web", "platform": platform.platform(), "machine": platform.machine(),
            "python": {"version": platform.python_version(), "sha256": digest(Path(sys.executable).read_bytes())},
            "environmentHash": digest(json.dumps(relevant, sort_keys=True).encode())}
