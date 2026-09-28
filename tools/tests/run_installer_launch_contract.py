"""Run the compiled Inno ShellExec boundary against an asInvoker Test Host in owned paths."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid
import winreg

sys.path.insert(0, str(Path(__file__).parents[2]))
from tools.host_installer import INNO_COMPILER_SHA256
from tools.pe_manifest import verify_embedded_manifest


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(command: list[str], *, env: dict[str, str], log: Path, expected: int = 0) -> int:
    completed = subprocess.run(command, env=env, capture_output=True, text=True,
                               encoding="utf-8", errors="replace", check=False)
    log.write_text(json.dumps({"command": command, "exitCode": completed.returncode,
                               "stdout": completed.stdout, "stderr": completed.stderr},
                              ensure_ascii=False, indent=2), encoding="utf-8")
    if completed.returncode != expected:
        raise RuntimeError(f"{log.name}: expected {expected}, got {completed.returncode}")
    return completed.returncode


def set_arp(path: str, install: Path, version: str, size: int) -> None:
    with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, path, 0, winreg.KEY_WRITE) as key:
        winreg.SetValueEx(key, "DisplayVersion", 0, winreg.REG_SZ, version)
        winreg.SetValueEx(key, "InstallLocation", 0, winreg.REG_SZ, str(install))
        winreg.SetValueEx(key, "EstimatedSize", 0, winreg.REG_DWORD, size)


def read_arp(path: str) -> dict | None:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as key:
            result = {}
            index = 0
            while True:
                try:
                    name, value, kind = winreg.EnumValue(key, index)
                except OSError:
                    break
                result[name] = {"value": value, "kind": kind}
                index += 1
            return result
    except FileNotFoundError:
        return None


def remove_test_key(path: str) -> None:
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
    except FileNotFoundError:
        pass


def fixture_case(name: str, work: Path, setup: Path, helper: Path,
                 source_hash: str, binary_hash: str) -> dict:
    case = work / name
    case.mkdir()
    scope = case / "scope"
    scope.mkdir()
    identifier = uuid.uuid4().hex
    (scope / ".nxp-installer-lab").write_text(identifier, encoding="utf-8")
    install = scope / "instance"
    install.mkdir()
    manager = scope / "manager"
    arp = rf"Software\NexusPipeline\Tests\Uninstall\{identifier}"
    owner = rf"Software\NexusPipeline\Tests\Installer\{identifier}"
    env = os.environ.copy()
    env.update({
        "NEXUS_INSTALLER_TEST_SCOPE": identifier,
        "NEXUS_INSTALLER_TEST_ROOT": str(scope),
        "NXP_CONTRACT_CASE": name.split("-", 1)[0],
        "NXP_CONTRACT_HELPER": str(helper),
        "NXP_CONTRACT_ROOT": str(install),
        "NXP_CONTRACT_RECEIPT": str(case / "pascal-receipt.txt"),
        "TEMP": str(case / "runtime"),
        "TMP": str(case / "runtime"),
    })
    Path(env["TEMP"]).mkdir()
    if read_arp(arp) is not None:
        raise RuntimeError("test ARP key already exists")
    original = b"fixture version A"
    target = b"fixture version B"
    executable = install / "nexus-pipeline.exe"
    executable.write_bytes(original)
    manifest = case / "manifest.json"
    manifest.write_text(json.dumps([{
        "Path": "nexus-pipeline.exe", "Sha256": hashlib.sha256(original).hexdigest()
    }]), encoding="utf-8")
    transaction = uuid.uuid4().hex
    env["NXP_CONTRACT_TX"] = transaction
    result_path = install / ".nxp" / "state" / "updates" / f"{transaction}.result.json"
    report: dict = {"case": name, "transactionId": transaction,
                    "evidenceKind": "OS_BOUNDARY_CONTRACT",
                    "pascalSourceSha256": source_hash, "setupSha256": binary_hash,
                    "testHelperSha256": digest(helper), "testRoot": str(case)}
    try:
        run([str(helper), "installer-state", "register", "--root", str(install),
             "--version", "0.16.9001", "--manifest", str(manifest)],
            env=env, log=case / "register-a.json")
        set_arp(arp, install, "0.16.9001", 12)
        (manager / "unins000.exe").write_bytes(b"fixture uninstaller A")
        (manager / "unins000.dat").write_bytes(b"fixture data A")
        user_files = [install / "config" / "fixture-setting.txt",
                      install / "data" / "fixture-user.bin"]
        for path, contents in zip(user_files, (b"user setting A", b"user data A")):
            path.parent.mkdir(parents=True)
            path.write_bytes(contents)
        report["before"] = {"arp": read_arp(arp),
                            "uninstaller": digest(manager / "unins000.exe"),
                            "uninstallData": digest(manager / "unins000.dat"),
                            "identity": digest(manager / "identity.protected"),
                            "image": digest(executable),
                            "userData": {path.relative_to(install).as_posix(): digest(path)
                                         for path in user_files}}
        run([str(helper), "installer-state", "metadata-begin", "--root", str(install),
             "--transaction", transaction, "--version", "0.16.9002",
             "--image-hash", hashlib.sha256(target).hexdigest(), "--upgrade", "true"],
            env=env, log=case / "metadata-begin.json")
        set_arp(arp, install, "0.16.9002", 42)
        (manager / "unins000.exe").write_bytes(b"fixture uninstaller B")
        (manager / "unins000.dat").write_bytes(b"fixture data B")
        run([str(helper), "installer-state", "metadata-observe", "--root", str(install),
             "--transaction", transaction], env=env, log=case / "metadata-observe.json")
        scenario = env["NXP_CONTRACT_CASE"]
        if scenario == "T1" and name == "T1-rolled-back":
            task = install / ".nxp-update" / "task.json"
            task.parent.mkdir()
            task.write_text(json.dumps({"Mode": "apply", "Version": "0.16.9002",
                "StagedDir": str(install / "stage"), "Phase": "RollbackConfirmed",
                "TransactionId": transaction,
                "TargetImageHash": hashlib.sha256(target).hexdigest()}), encoding="utf-8")
            result_path.parent.mkdir(parents=True, exist_ok=True)
            result_path.write_text(json.dumps({"TransactionId": transaction, "Version": "0.16.9002",
                "Succeeded": False, "Code": "apply_failed"}), encoding="utf-8")
        if name in ("T0", "T0-stale", "T1-committed"):
            executable.write_bytes(target)
            manifest.write_text(json.dumps([{
                "Path": "nexus-pipeline.exe", "Sha256": hashlib.sha256(target).hexdigest()
            }]), encoding="utf-8")
            run([str(helper), "installer-state", "register", "--root", str(install),
                 "--version", "0.16.9002", "--manifest", str(manifest)],
                env=env, log=case / "register-b-fixture.json")
            result_path.parent.mkdir(parents=True, exist_ok=True)
            result_path.write_text(json.dumps({
                "TransactionId": uuid.uuid4().hex if name == "T0-stale" else transaction,
                "Version": "0.16.9002", "Succeeded": name == "T0",
                "Code": "committed" if name == "T0" else "committed_cleanup_pending"
            }), encoding="utf-8")
            if name == "T1-committed":
                (install / ".nxp-version").write_text("0.16.9002", encoding="utf-8")
        log = case / "setup-process.json"
        command = [str(setup), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                   f"/DIR={install}", f"/LOG={case / 'inno.log'}"]
        completed = subprocess.run(command, env=env, capture_output=True, text=True,
                                   encoding="utf-8", errors="replace", timeout=90, check=False)
        log.write_text(json.dumps({"command": command, "exitCode": completed.returncode,
                                   "stdout": completed.stdout, "stderr": completed.stderr},
                                  ensure_ascii=False, indent=2), encoding="utf-8")
        receipt = (case / "pascal-receipt.txt").read_text(encoding="utf-8")
        parsed = dict(line.split("=", 1) for line in receipt.splitlines() if "=" in line)
        report["setupExitCode"] = completed.returncode
        report["pascalReceipt"] = parsed
        report["after"] = {"arp": read_arp(arp),
                           "uninstaller": digest(manager / "unins000.exe"),
                           "uninstallData": digest(manager / "unins000.dat"),
                           "identity": digest(manager / "identity.protected"),
                           "image": digest(executable),
                           "userData": {path.relative_to(install).as_posix(): digest(path)
                                        for path in user_files},
                           "checkpointPending": (manager / "metadata-checkpoint").exists()}
        (case / "case.json").write_text(json.dumps(report, ensure_ascii=False, indent=2),
                                        encoding="utf-8")
        committed = name in ("T0", "T1-committed")
        unknown = name == "T0-stale"
        expected_version = "0.16.9002" if committed or unknown else "0.16.9001"
        expected_uninstaller = b"fixture uninstaller B" if committed or unknown else b"fixture uninstaller A"
        expected_code = "0" if name == "T0" else "12"
        expected_native = {"F1223": "1223", "F5": "5", "F2": "2", "F32": "32",
                           "REAL2": "2", "T1": "1", "T0": "0"}[scenario]
        if parsed.get("code") != expected_native or parsed.get("qualified") != ("1" if name == "T0" else "0"):
            raise RuntimeError(f"{name}: Pascal receipt mismatch: {parsed}")
        if str(completed.returncode) != expected_code:
            raise RuntimeError(f"{name}: Setup exit {completed.returncode}, expected {expected_code}")
        if report["after"]["arp"]["DisplayVersion"]["value"] != expected_version:
            raise RuntimeError(f"{name}: ARP version mismatch")
        if (manager / "unins000.exe").read_bytes() != expected_uninstaller:
            raise RuntimeError(f"{name}: uninstaller bytes mismatch")
        if report["after"]["userData"] != report["before"]["userData"]:
            raise RuntimeError(f"{name}: user data changed")
        if committed or unknown:
            if report["after"]["identity"] == report["before"]["identity"] or \
               report["after"]["image"] == report["before"]["image"]:
                raise RuntimeError(f"{name}: committed identity or image did not change")
        elif report["after"]["identity"] != report["before"]["identity"] or \
             report["after"]["image"] != report["before"]["image"]:
            raise RuntimeError(f"{name}: uncommitted identity or image changed")
        if report["after"]["checkpointPending"] != unknown:
            raise RuntimeError(f"{name}: checkpoint state mismatch")
        return report
    finally:
        remove_test_key(arp)
        remove_test_key(owner)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--compiler", type=Path, required=True)
    parser.add_argument("--test-helper", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    arguments = parser.parse_args()
    compiler = arguments.compiler.resolve()
    helper = arguments.test_helper.resolve()
    work = arguments.work.resolve()
    if not compiler.is_file() or digest(compiler) != INNO_COMPILER_SHA256:
        raise RuntimeError("locked ISCC compiler mismatch")
    if not helper.is_file():
        raise RuntimeError("asInvoker Test Host output missing")
    verify_embedded_manifest(helper, "asInvoker")
    if work.exists():
        raise RuntimeError("work directory must be new")
    work.mkdir(parents=True)
    source = Path(__file__).parents[1] / "installer-launch.iss"
    template = Path(__file__).with_name("installer-launch-contract.iss.in")
    output = work / "setup"
    output.mkdir()
    script = template.read_text(encoding="utf-8").replace("@@ROOT@@", str(work / "default")) \
        .replace("@@OUTPUT@@", str(output)).replace("@@LAUNCH_CODE@@", source.read_text(encoding="utf-8"))
    if "@@" in script:
        raise RuntimeError("contract script has unresolved placeholder")
    script_path = work / "installer-launch-contract.iss"
    script_path.write_text(script, encoding="utf-8")
    run([str(compiler), "/Q", str(script_path)], env=os.environ.copy(),
        log=work / "iscc.json")
    setup = output / "nxp-launch-contract.exe"
    if not setup.is_file():
        raise RuntimeError("compiled contract Setup missing")
    cases = ["F1223", "F5", "F2", "F32", "REAL2", "T1", "T1-rolled-back",
             "T1-committed", "T0", "T0-stale", "F1223-repeat"]
    results = []
    for name in cases:
        print(f"[installer-launch-contract] {name}", flush=True)
        results.append(fixture_case(name, work, setup, helper, digest(source), digest(setup)))
    (work / "results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2),
                                        encoding="utf-8")
    print(f"PASS {len(results)} compiled Pascal contract cases", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
