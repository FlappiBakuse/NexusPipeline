"""Run real native suites and validate their declared, nonempty test selection."""
from collections import Counter
from datetime import datetime, timezone
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import unittest
import uuid
import xml.etree.ElementTree as ET

from build_product import build_product, update_export
from source_snapshot import digest, write_json, external_directory
from binary_inputs import capture_binary_inputs, verify_binary_inputs, hash_file, PROJECTS
from test_expectations import load_expected_methods
from test_registry import suite_filter
from desktop_bundle import require_no_links, read_desktop_bundle
from frozen_build import reuse_build
from source_snapshot import source_files
from test_selection import select_tests
from native_bundles import prepare_bundles, verify_bundles


DESKTOP_MAINTENANCE_METHOD = "NexusPipeline.StorageTests.MaintenanceBinaryTests.RestartsOwnedDesktop"


def prepare_output(root, build_root, output):
    marker = output / "test-output-owner.json"
    expected = (json.dumps({"format": "nexus.test.output", "version": 1,
        "repositoryRoot": str(Path(root).resolve(strict=True)), "buildRoot": str(build_root)},
        ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode("utf-8")
    require_no_links(marker)
    if output.exists():
        if not output.is_dir():
            raise ValueError("Test output must be a directory")
        if any(output.iterdir()) and (not marker.is_file() or marker.read_bytes() != expected):
            raise ValueError("Test output is not owned by this repository and build root")
    output.mkdir(parents=True, exist_ok=True)
    if not marker.exists():
        with marker.open("xb") as stream:
            stream.write(expected)


def run_tests(root, build_root, output, group, fixture_volume_root=None, method=None, desktop_bundle=None):
    wall_started = time.monotonic()
    build_root = external_directory(root, build_root)
    output = external_directory(root, output)
    selected = [method] if isinstance(method, str) else list(method or [])
    if method is not None and (not 1 <= len(selected) <= 64 or len(set(selected)) != len(selected)):
        raise ValueError("A method selection must contain one to sixty-four distinct identities")
    if group in ("tooling", "host-boundary") and len(selected) > 1:
        raise ValueError("Python accepts one exact test ID")
    method = selected[0] if len(selected) == 1 else tuple(selected) if selected else None
    selection = select_tests(root, group, selected)
    requires_desktop = group == "storage" and DESKTOP_MAINTENANCE_METHOD in selected
    if requires_desktop and len(selected) != 1:
        raise ValueError("Desktop maintenance requires its own exact invocation")
    if requires_desktop != (desktop_bundle is not None):
        raise ValueError("Only the exact desktop-maintenance method requires --desktop-bundle")
    desktop_input = read_desktop_bundle(root, desktop_bundle) if requires_desktop else None
    if selected and group not in ("tooling", "host-boundary") and not set(selected).issubset(load_expected_methods(root, group)):
        raise ValueError("A method filter requires an existing declared native method in this group")
    if (group == "storage-config-cross-volume") != (fixture_volume_root is not None):
        raise ValueError("Only storage-config-cross-volume requires an explicit --fixture-volume-root")
    if fixture_volume_root is not None:
        if not fixture_volume_root.is_absolute() or not fixture_volume_root.is_dir():
            raise ValueError("The explicit second-volume fixture root must already exist")
        for path in (fixture_volume_root, *fixture_volume_root.parents):
            if path.is_symlink() or path.is_junction():
                raise ValueError("The fixture volume root cannot traverse a reparse point")
    prepare_output(root, build_root, output)
    initial_source = source_files(root)
    build_started = time.monotonic()
    projects = selection['projects']
    reused = reuse_build(root, build_root, group, projects) if projects else False
    code = 0 if not projects or reused else build_product(root, build_root, "test", projects)
    build_seconds = time.monotonic() - build_started
    if code:
        return code
    invocation_source = update_export(root, build_root)
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S") + "-" + uuid.uuid4().hex[:8]
    run_root = Path(output).resolve() / run_id
    if root == run_root or root in run_root.parents:
        raise ValueError("Test results must be outside the source tree")
    run_root.mkdir(parents=True)
    build_receipt = json.loads((build_root / "build-receipt.json").read_text(encoding="utf-8")) if projects else None
    source_root = build_root / "source"
    binary_inputs, binary_records = capture_binary_inputs(build_root, run_root, group, projects)
    interpreter = None
    if group in ("storage", "storage-execution", "storage-android", "storage-provider", "storage-judgement", "storage-file-logs"):
        path = Path(sys.executable).resolve()
        interpreter = {"path": str(path), "sizeBytes": path.stat().st_size, "sha256": hash_file(path)}
    started = time.monotonic()
    if group in ("contracts", "core", "windows", "windows-secrets", "storage", "storage-checkin", "storage-wal", "storage-startup", "storage-automation", "storage-execution", "storage-android", "storage-provider", "storage-judgement", "storage-file-logs", "storage-settings", "storage-secrets", "storage-config-revisions", "storage-config-output", "storage-config-controller", "storage-config-cross-volume"):
        result = run_dotnet(source_root, build_root, run_root, group, fixture_volume_root, method, desktop_bundle)
    else:
        result = run_python(source_root, build_root, run_root, group, method, live_root=root)
    test_seconds = time.monotonic() - started
    cleanup_started = time.monotonic()
    verify_binary_inputs(build_root, binary_records)
    if source_files(root) != initial_source:
        raise ValueError("Source changed during the native invocation")
    if desktop_input is not None:
        if read_desktop_bundle(source_root, desktop_bundle) != desktop_input:
            raise ValueError("Desktop bundle changed during the native method")
        result["desktopBundleInputs"] = {**desktop_input, "verifiedUnchangedAfterRun": True}
    if interpreter is not None:
        path = Path(interpreter["path"])
        if path.stat().st_size != interpreter["sizeBytes"] or hash_file(path) != interpreter["sha256"]:
            raise ValueError("Judgement fixture interpreter changed during the native suite")
    result.update(group=group, runId=run_id, elapsedSeconds=time.monotonic() - started,
                  coverage=("declared-method-only" if len(selected) == 1 else "declared-methods-only") if method else "complete-group", methodFilter=method,
                  source=invocation_source, buildSource=build_receipt["source"] if build_receipt else None,
                  buildReceipt=str(build_root / "runs" / build_receipt["runId"] / "build-receipt.json") if build_receipt else None,
                  selection=selection,
                  applicationBinaryInputs={**binary_inputs, "verifiedUnchangedAfterRun": True})
    if interpreter is not None:
        result["judgementFixtureInterpreter"] = {**interpreter, "verifiedUnchangedAfterRun": True}
    result["timings"] = {"buildOrVerifySeconds": build_seconds, "reusedBuild": reused,
                         "setupSeconds": started - build_started - build_seconds,
                         "testSeconds": test_seconds, "verificationSeconds": time.monotonic() - cleanup_started,
                         "wallSeconds": time.monotonic() - wall_started}
    write_json(run_root / "test-receipt.json", result)
    print(json.dumps({"group": group, "status": result["status"], "receipt": str(run_root / "test-receipt.json")}))
    return result["exitCode"]


def run_dotnet(source_root, build_root, run_root, group, fixture_volume_root=None, method=None, desktop_bundle=None):
    methods = [method] if isinstance(method, str) else list(method or [])
    assemblies = select_tests(source_root, group, methods)['assemblies']
    binaries = [str(build_root / f"build/bin/{assembly}/release/{assembly}.dll") for assembly in assemblies]
    report = run_root / (group + ".trx")
    command = native_test_launcher(source_root, group, method) + binaries + [f"/Logger:trx;LogFileName={group}.trx",
               f"/ResultsDirectory:{run_root}"]
    selection = suite_filter(source_root, group, method, DESKTOP_MAINTENANCE_METHOD)
    if selection is not None:
        command.append("/TestCaseFilter:" + selection)
    bundle_inputs = prepare_bundles(source_root, build_root, run_root, select_tests(source_root, group, methods)['methods'])
    env = os.environ.copy()
    env.pop("NEXUS_TEST_BUNDLE_ROOT", None)
    if bundle_inputs is not None:
        env["NEXUS_TEST_BUNDLE_ROOT"] = bundle_inputs['root']
    if "exec" in command[:2]:
        env.update(VSTEST_DOTNET_ROOT_PATH=str(Path(command[0]).parent),
                   VSTEST_DOTNET_ROOT_ARCHITECTURE="X64")
    env.pop("NEXUS_TEST_DESKTOP_BUNDLE", None)
    if desktop_bundle is not None:
        env["NEXUS_TEST_DESKTOP_BUNDLE"] = str(desktop_bundle.resolve(strict=True))
    env.pop("NEXUS_TEST_VOLUME_ROOT", None)
    if fixture_volume_root is not None:
        volume_runtime = fixture_volume_root / run_root.name
        volume_runtime.mkdir()
        env["NEXUS_TEST_VOLUME_ROOT"] = str(volume_runtime)
    env.update(TEMP=str(run_root), TMP=str(run_root), DOTNET_CLI_UI_LANGUAGE="en-US",
               DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_HOME=str(build_root.parent / "cache/dotnet-home"))
    if group in ("windows", "windows-secrets", "storage", "storage-checkin", "storage-wal", "storage-startup", "storage-automation", "storage-execution", "storage-android", "storage-provider", "storage-judgement", "storage-file-logs", "storage-settings", "storage-secrets", "storage-config-revisions", "storage-config-output", "storage-config-controller", "storage-config-cross-volume"):
        runtime = run_root / "runtime"
        runtime.mkdir()
        env.update(NEXUS_TEST_ROOT=str(runtime),
                   NEXUS_MANAGED_EXTENSION_FIXTURE_DLL=str(build_root / "build/bin/ManagedExtension/release/NexusPipeline.ControlledExtension.dll"),
                   NEXUS_ANDROID_TOOL_FIXTURE_EXE=str(build_root / "build/bin/AndroidTool/release/AndroidTool.exe"),
                   NEXUS_TARGET_GAME_FIXTURE_EXE=str(build_root / "build/bin/TargetGame/release/TargetGame.exe"),
                   NEXUS_PROCESS_FIXTURE_EXE=str(build_root / "build/bin/OwnedWriter/release/OwnedWriter.exe"),
                   NEXUS_TEST_HOST_EXE=str(build_root / "build/bin/NexusPipeline.Host/release/NexusPipeline.Host.exe"),
                   NEXUS_TEST_MAINTENANCE_EXE=str(build_root / "build/bin/NexusPipeline.Maintenance/release/NexusPipeline.Maintenance.exe"),
                   NEXUS_TEST_PYTHON_EXE=str(Path(sys.executable).resolve()))
    process = subprocess.run(command, env=env, cwd=source_root, capture_output=True,
                             encoding="utf-8", errors="replace")
    verify_bundles(bundle_inputs)
    native_output = process.stdout + process.stderr
    (run_root / "native.log").write_bytes(native_output.encode())
    print(native_output, flush=True)
    if not report.exists():
        return {"status": "FAIL", "exitCode": process.returncode or 1, "reason": "missing_native_report", "command": command}
    xml = ET.parse(report).getroot()
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    results = xml.findall("t:Results/t:UnitTestResult", ns)
    definitions = {item.attrib["id"]: item.find("t:TestMethod", ns).attrib["className"].split(",", 1)[0] + "." + item.find("t:TestMethod", ns).attrib["name"]
                   for item in xml.findall("t:TestDefinitions/t:UnitTest", ns)}
    actual = Counter(definitions[item.attrib["testId"]] for item in results)
    expected = load_expected_methods(source_root, group)
    excluded = [DESKTOP_MAINTENANCE_METHOD] if group == "storage" and method is None else []
    for name in excluded:
        expected.pop(name)
    if method is not None:
        methods = [method] if isinstance(method, str) else method
        expected = {name: expected[name] for name in methods}
    valid = bool(expected) and dict(actual) == expected and all(item.attrib["outcome"] == "Passed" for item in results)
    valid = valid and process.returncode == 0 and len({item.attrib["testId"] for item in results}) == len(results)
    summary = xml.find("t:ResultSummary", ns)
    counters = xml.find("t:ResultSummary/t:Counters", ns)
    valid = valid and summary is not None and summary.get("outcome") in {"Completed", "Passed"} and counters is not None
    if counters is not None:
        valid = valid and all(counters.get(name) == str(len(results)) for name in ("total", "executed", "passed"))
        valid = valid and all(counters.get(name, "0") == "0" for name in
                              ("failed", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted",
                               "notRunnable", "notExecuted", "disconnected", "inProgress", "pending"))
    return {"status": "PASS" if valid else "FAIL", "exitCode": 0 if valid else process.returncode or 1,
            "maintenanceBundleInputs": bundle_inputs,
            "command": command, "expectedMethods": expected, "actualMethods": dict(actual),
            "externalInputMethodsExcluded": excluded,
            "fixtureVolumeRoot": str(fixture_volume_root) if fixture_volume_root is not None else None,
            "fixtureVolumeRuntime": env.get("NEXUS_TEST_VOLUME_ROOT"),
            "inheritedPowerShellProcessExecutionPolicy": next((value for key, value in env.items()
                if key.casefold() == "psexecutionpolicypreference"), None),
            "actualCaseIds": [item.attrib["testName"] for item in results], "nativeReport": str(report),
            "nativeReportSha256": digest(report.read_bytes())}


def native_test_launcher(source_root, group, method=None):
    methods = [method] if isinstance(method, str) else method or []
    if group != "windows" and not set(methods).intersection({
        "NexusPipeline.StorageTests.HostRestartTests.RestartsSavedPort",
        "NexusPipeline.StorageTests.MaintenanceBinaryTests.ConsumesDeferredStartup",
        "NexusPipeline.StorageTests.MaintenanceBinaryTests.CommitsPreparedCandidate",
        "NexusPipeline.StorageTests.MaintenanceBinaryTests.AppliesPublishedArchive",
        "NexusPipeline.StorageTests.MaintenanceBinaryTests.RestoresPublishedPayload",
        "NexusPipeline.StorageTests.MaintenanceBinaryTests.RestoresDerivedPayload",
        "NexusPipeline.StorageTests.MaintenanceBinaryTests.RestoresFailedCandidate",
        "NexusPipeline.StorageTests.MaintenanceBinaryTests.ConsumesStartupUpdate",
        "NexusPipeline.StorageTests.InstalledUpdateTests.CommitsInstalledIdentity",
        "NexusPipeline.StorageTests.InstalledUpdateTests.RestoresInstalledIdentity",
        DESKTOP_MAINTENANCE_METHOD,
    }):
        return ["dotnet", "vstest"]
    # The SDK's command forwarder creates a Job; startup-origin tests must observe the caller's real Job boundary.
    executable = shutil.which("dotnet")
    if executable is None:
        raise ValueError("The pinned .NET SDK is not installed")
    host = Path(executable).resolve(strict=True)
    version = json.loads((source_root / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]
    installed = subprocess.run([str(host), "--list-sdks"], cwd=source_root, capture_output=True,
                               text=True, encoding="utf-8", check=True)
    roots = [line[len(version) + 2:-1] for line in installed.stdout.splitlines()
             if line.startswith(version + " [") and line.endswith("]")]
    if len(roots) != 1:
        raise ValueError("The exact pinned SDK must identify one native test runner")
    runner = (Path(roots[0]) / version / "vstest.console.dll").resolve(strict=True)
    return [str(host), "exec", str(runner)]


class PythonResult(unittest.TextTestResult):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.identities = []

    def startTest(self, test):
        super().startTest(test)
        self.identities.append(test.id())


def run_python(source_root, build_root, run_root, group, method=None, *, live_root):
    os.environ.pop("NEXUS_TEST_DESKTOP_BUNDLE", None)
    suite_directory = {"tooling": "tooling", "host-boundary": "integration"}[group]
    declared = load_expected_methods(source_root, group)
    expected = {method: declared[method]} if method else declared
    expected_count = sum(expected.values())
    os.environ["NEXUS_TEST_ROOT"] = str(run_root / "runtime")
    os.environ["TEMP"] = str(run_root)
    os.environ["TMP"] = str(run_root)
    Path(os.environ["NEXUS_TEST_ROOT"]).mkdir()
    os.environ["NEXUS_TEST_HOST_EXE"] = str(build_root / "build/bin/NexusPipeline.Host/release/NexusPipeline.Host.exe")
    os.environ["NEXUS_TEST_BUILD_ROOT"] = str(build_root)
    os.environ["NEXUS_TEST_SOURCE_ROOT"] = str(live_root)
    os.environ["NEXUS_PROCESS_FIXTURE_EXE"] = str(build_root / "build/bin/OwnedWriter/release/OwnedWriter.exe")
    suite = unittest.defaultTestLoader.discover(str(source_root / "tests" / suite_directory))
    def cases(value):
        for item in value:
            if isinstance(item, unittest.TestSuite):
                yield from cases(item)
            else:
                yield item
    discovered = list(cases(suite))
    discovered_ids = [item.id() for item in discovered]
    if len(discovered_ids) != len(set(discovered_ids)) or set(discovered_ids) != set(declared):
        raise ValueError('Python discovered methods differ from their independently declared set')
    if method is not None:
        selected = [item for item in discovered if item.id() == method]
        if len(selected) != 1:
            raise ValueError("A Python method filter must match exactly one discovered test ID")
        suite = unittest.TestSuite(selected)
        expected_count = 1
    else:
        suite = unittest.TestSuite(discovered)
    selected_ids = [item.id() for item in cases(suite)]
    buffer = io.StringIO()
    result = unittest.TextTestRunner(stream=buffer, verbosity=2, resultclass=PythonResult).run(suite)
    native_output = buffer.getvalue()
    log = run_root / "native.log"
    log.write_bytes(native_output.encode())
    print(native_output, flush=True)
    actual = Counter(result.identities)
    valid = result.wasSuccessful() and result.testsRun == expected_count and not result.skipped and dict(actual) == expected
    return {"status": "PASS" if valid else "FAIL", "exitCode": 0 if valid else 1,
            "expectedCaseCount": expected_count, "actualCaseCount": result.testsRun,
            "selectedCaseIds": selected_ids,
            "declaredMethods": declared, "discoveredMethods": discovered_ids, "actualMethods": dict(actual),
            "skipped": [test.id() for test, _ in result.skipped], "nativeReport": str(log),
            "nativeReportSha256": digest(log.read_bytes())}
