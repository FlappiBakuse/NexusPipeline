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
from test_expectations import suite_classes
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
    if method is not None and (not selected or len(set(selected)) != len(selected)):
        raise ValueError("A method selection must contain distinct nonempty identities")
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
    reused = False
    error = None
    try:
        reused = reuse_build(root, build_root, group, projects) if projects else False
        code = 0 if not projects or reused else build_product(root, build_root, "test", projects)
    except (OSError, ValueError) as reason:
        code, error = 1, str(reason)
        print(f"ERROR: {error}", file=sys.stderr)
    build_seconds = time.monotonic() - build_started
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S") + "-" + uuid.uuid4().hex[:8]
    run_root = Path(output).resolve() / run_id
    if root == run_root or root in run_root.parents:
        raise ValueError("Test results must be outside the source tree")
    run_root.mkdir(parents=True)
    receipt = dict(group=group, runId=run_id, methodFilter=method, selection=selection,
                   coverage=("selected-method-only" if len(selected) == 1 else "selected-methods-only") if method else "complete-group",
                   preparation={"status": "FAIL" if code else "PASS" if projects else "NOT_RUN",
                                "exitCode": code if projects else None, "reusedBuild": reused},
                   responsibilities=dict(lint="NOT_RUN", typecheck="NOT_RUN", test="NOT_RUN", build="NOT_RUN"))
    if code:
        receipt.update(status="FAIL", exitCode=code, reason="test_preparation_failed")
        if error is not None:
            receipt["error"] = error
        else:
            receipt["source"] = json.loads((build_root / "source-receipt.json").read_bytes())
        return write_result(run_root, receipt)
    invocation_source = update_export(root, build_root)
    build_receipt = json.loads((build_root / "build-receipt.json").read_text(encoding="utf-8")) if projects else None
    source_root = build_root / "source"
    receipt.update(source=invocation_source, buildSource=build_receipt["source"] if build_receipt else None,
                   buildReceipt=str(build_root / "runs" / build_receipt["runId"] / "build-receipt.json") if build_receipt else None)
    try:
        binary_inputs, binary_records = capture_binary_inputs(build_root, run_root, group, projects)
        bundle_inputs = prepare_bundles(source_root, build_root, run_root, selection["methods"]) if not selection["python"] else None
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        receipt["preparation"].update(status="FAIL", exitCode=1)
        receipt.update(status="FAIL", exitCode=1, reason="test_inputs_failed", error=str(error))
        return write_result(run_root, receipt)
    interpreter = None
    if group in ("storage", "storage-execution", "storage-android", "storage-provider", "storage-judgement", "storage-file-logs"):
        path = Path(sys.executable).resolve()
        interpreter = {"path": str(path), "sizeBytes": path.stat().st_size, "sha256": hash_file(path)}
    started = time.monotonic()
    if selection["python"]:
        result = run_python(source_root, build_root, run_root, group, method, live_root=root)
        receipt["preparation"].update(result.pop("preparation", {}))
        if result.get("deferredMethods"):
            receipt["coverage"] = "default-group"
    else:
        result = run_dotnet(source_root, build_root, run_root, group, fixture_volume_root, method, desktop_bundle,
                            assemblies=selection["assemblies"], bundle_inputs=bundle_inputs)
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
    if receipt["preparation"]["status"] not in ("FAIL", "BLOCKED"):
        receipt["responsibilities"]["test"] = result["status"]
    result.update(receipt, elapsedSeconds=time.monotonic() - started,
                  applicationBinaryInputs={**binary_inputs, "verifiedUnchangedAfterRun": True})
    if interpreter is not None:
        result["judgementFixtureInterpreter"] = {**interpreter, "verifiedUnchangedAfterRun": True}
    result["timings"] = {"buildOrVerifySeconds": build_seconds, "reusedBuild": reused,
                         "setupSeconds": started - build_started - build_seconds,
                         "testSeconds": test_seconds, "verificationSeconds": time.monotonic() - cleanup_started,
                         "wallSeconds": time.monotonic() - wall_started}
    return write_result(run_root, result)


def write_result(run_root, result):
    path = run_root / "test-receipt.json"
    write_json(path, result)
    print(json.dumps({"group": result["group"], "status": result["status"], "receipt": str(path)}))
    return result["exitCode"]


def run_dotnet(source_root, build_root, run_root, group, fixture_volume_root, method, desktop_bundle, *, assemblies, bundle_inputs):
    binaries = [str(build_root / f"build/bin/{assembly}/release/{assembly}.dll") for assembly in assemblies]
    report = run_root / (group + ".trx")
    command = native_test_launcher(source_root, group, method) + binaries + [f"/Logger:trx;LogFileName={group}.trx",
               f"/ResultsDirectory:{run_root}"]
    selection = suite_filter(source_root, group, method, DESKTOP_MAINTENANCE_METHOD)
    if selection is not None:
        command.append("/TestCaseFilter:" + selection)
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
                   NEXUS_TEST_INSTALLER_EXE=str(build_root / "build/bin/NexusPipeline.Installer/release/NexusPipeline.Installer.exe"),
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
    requested = [method] if isinstance(method, str) else list(method or [])
    excluded = [DESKTOP_MAINTENANCE_METHOD] if group == "storage" and method is None else []
    classes = suite_classes(source_root, group)
    valid = bool(actual) and all(item.attrib["outcome"] == "Passed" for item in results)
    valid = valid and (set(actual) == set(requested) if requested else
        all(any(identity.startswith(owner + '.') for owner in classes) for identity in actual))
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
            "command": command, "requestedMethods": requested, "actualMethods": dict(actual),
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
    if not discovered_ids or len(discovered_ids) != len(set(discovered_ids)):
        raise ValueError('Python discovery must select unique nonempty tests')
    preparations = {}
    for item in discovered:
        identity = item.id()
        method_name = identity.rsplit(".", 1)[-1]
        prepare = getattr(getattr(item, method_name, None), "prepare_inputs", None)
        if prepare is not None:
            preparations[identity] = prepare
    if method is not None:
        selected = [item for item in discovered if item.id() == method]
        if len(selected) != 1:
            raise ValueError("A Python method filter must match exactly one discovered test ID")
    else:
        selected = [item for item in discovered if item.id() not in preparations]
    if not selected:
        raise ValueError("Python selection must include at least one test")
    selected_ids = [item.id() for item in selected]
    scope = dict(selectedCaseIds=selected_ids, discoveredMethods=discovered_ids)
    if method is None and preparations:
        scope["deferredMethods"] = dict.fromkeys(preparations, "NOT_RUN")
    if method in preparations:
        try:
            inputs = preparations[method](source_root)
        except (OSError, ValueError) as error:
            status = "BLOCKED" if isinstance(error, FileNotFoundError) else "FAIL"
            print(f"ERROR: {error}", file=sys.stderr)
            return dict(status=status, exitCode=1, reason="test_inputs_failed", error=str(error),
                        actualCaseCount=0, actualMethods={}, preparation=dict(status=status, exitCode=1), **scope)
        selected[0].fixture_inputs = inputs
        scope["preparation"] = dict(status="PASS", exitCode=0, inputs=inputs)
    expected = dict.fromkeys(selected_ids, 1)
    expected_count = len(selected_ids)
    buffer = io.StringIO()
    result = unittest.TextTestRunner(stream=buffer, verbosity=2, resultclass=PythonResult).run(unittest.TestSuite(selected))
    native_output = buffer.getvalue()
    log = run_root / "native.log"
    log.write_bytes(native_output.encode())
    print(native_output, flush=True)
    actual = Counter(result.identities)
    valid = result.wasSuccessful() and result.testsRun == expected_count and not result.skipped and dict(actual) == expected
    return {"status": "PASS" if valid else "FAIL", "exitCode": 0 if valid else 1,
            "expectedCaseCount": expected_count, "actualCaseCount": result.testsRun,
            **scope, "actualMethods": dict(actual),
            "skipped": [test.id() for test, _ in result.skipped], "nativeReport": str(log),
            "nativeReportSha256": digest(log.read_bytes())}
