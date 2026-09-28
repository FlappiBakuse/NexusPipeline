"""Run the real A -> built-in B update -> A uninstaller experiment inside an approved VM."""

from __future__ import annotations

import argparse
import base64
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
from urllib.request import Request, urlopen
from urllib.error import HTTPError, URLError
import winreg


ARP = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusPipeline.PerUser_is1"


def sha(path: Path) -> str:
    state = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            state.update(chunk)
    return state.hexdigest()


def registry_values() -> dict:
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, ARP) as key:
        result = {}
        for index in range(winreg.QueryInfoKey(key)[1]):
            name, value, kind = winreg.EnumValue(key, index)
            result[name] = {"value": value, "kind": kind}
        return result


def report(work: Path, name: str, document: dict) -> None:
    (work / f"{name}.json").write_text(json.dumps(document, ensure_ascii=False, indent=2,
                                             default=str), encoding="utf-8")


def wait_for(predicate, seconds: int, what: str):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            value = predicate()
        except (OSError, TimeoutError, HTTPError, URLError):
            value = None
        if value:
            return value
        time.sleep(0.2)
    raise TimeoutError(what)


def api(port: int, method: str, route: str) -> dict:
    with urlopen(Request(f"http://127.0.0.1:{port}{route}", method=method,
                         data=b"{}" if method == "POST" else None,
                         headers={"Content-Type": "application/json"}), timeout=10) as reply:
        return json.load(reply)


def run_logged(command: list[str], work: Path, name: str, timeout: int = 300) -> int:
    with (work / f"{name}.log").open("wb") as log:
        completed = subprocess.run(command, cwd=work, stdin=subprocess.DEVNULL,
                                   stdout=log, stderr=subprocess.STDOUT, timeout=timeout,
                                   check=False)
        log.write(f"\nEXIT_CODE={completed.returncode}\n".encode())
        return completed.returncode


def stop_owned_updated_host(image: Path, work: Path) -> None:
    expected = str(image).replace("'", "''")
    script = f"""
$ErrorActionPreference = 'Stop'
$expected = '{expected}'
$items = @(Get-CimInstance Win32_Process -Filter "Name = 'nexus-pipeline.exe'" |
    Where-Object {{ $_.ExecutablePath -eq $expected }})
if ($items.Count -ne 1) {{ throw 'expected exactly one B Host process at the installed image' }}
$process = Get-Process -Id $items[0].ProcessId -ErrorAction Stop
if ($process.Path -ne $expected) {{ throw 'B Host process path changed' }}
$identity = [ordered]@{{ ProcessId = $process.Id; Path = $process.Path;
    StartTime = $process.StartTime.ToString('o') }}
Stop-Process -Id $process.Id -ErrorAction Stop
$process.WaitForExit(10000)
if (-not $process.HasExited) {{ throw 'B Host did not exit' }}
$identity | ConvertTo-Json -Compress
"""
    encoded = base64.b64encode(script.encode("utf-16le")).decode("ascii")
    completed = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive",
                                "-EncodedCommand", encoded], capture_output=True,
                               text=True, timeout=20, check=False)
    report(work, "b-host-stop", {"exitCode": completed.returncode,
                                 "stdout": completed.stdout, "stderr": completed.stderr})
    if completed.returncode != 0:
        raise RuntimeError("owned B Host could not be stopped before native uninstall")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pair", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True,
                        help="New, empty directory on a local fixed disk inside the isolated VM")
    parser.add_argument("--vm-attestation", type=Path, required=True,
                        help="VM owner JSON: isolatedVm=true, computerName, approvedForNativeSetup=true")
    args = parser.parse_args()
    if os.name != "nt":
        raise RuntimeError("Windows VM required")
    attestation = json.loads(args.vm_attestation.read_text(encoding="utf-8"))
    if (attestation.get("isolatedVm") is not True
        or attestation.get("approvedForNativeSetup") is not True
        or attestation.get("computerName", "").casefold() != os.environ.get("COMPUTERNAME", "").casefold()):
        raise RuntimeError("isolated VM attestation does not match this machine")
    model = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                            "(Get-CimInstance Win32_ComputerSystem).Model"],
                           capture_output=True, text=True, check=True).stdout.strip()
    if not any(token in model.casefold() for token in ("virtual", "vmware", "kvm", "qemu")):
        raise RuntimeError("machine model is not an isolated VM: " + model)
    work = args.work.resolve()
    if work.exists() or work.is_symlink():
        raise RuntimeError("work directory must not exist; preserved runs are never overwritten")
    if not work.drive or work == Path(work.anchor):
        raise RuntimeError("work must be a named local directory")
    import ctypes
    if ctypes.windll.kernel32.GetDriveTypeW(str(Path(work.anchor))) != 3:
        raise RuntimeError("work must reside on a local fixed disk")
    try:
        registry_values()
    except FileNotFoundError:
        pass
    else:
        raise RuntimeError("this Windows user already has a NexusPipeline ARP registration")
    manager = Path(os.environ["LOCALAPPDATA"]) / "NexusPipeline" / "installer"
    if manager.exists():
        raise RuntimeError("installer ownership directory already exists")
    pair = json.loads(args.pair.read_text(encoding="utf-8"))
    if pair.get("evidenceKind") != "REAL_NATIVE_VERSION_FIXTURE_BUILD":
        raise RuntimeError("wrong fixture identity")
    a, b = pair["A"], pair["B"]
    if a["version"] == b["version"] or a["imageSha256"] == b["imageSha256"]:
        raise RuntimeError("not two native versions")
    source = args.pair.resolve().parent
    setup_a = source / "A" / "setup" / f"NexusPipeline-v{a['version']}-win-x64-setup.exe"
    zip_b = source / "B" / "build" / f"NexusPipeline-v{b['version']}-win-x64.zip"
    if sha(setup_a) != a["setupSha256"] or sha(zip_b) != b["zipSha256"]:
        raise RuntimeError("A/B fixture bytes differ from build receipt")
    a_marker = a["fixtureAsset"]["path"]
    b_marker = b["fixtureAsset"]["path"]
    if a_marker == b_marker:
        raise RuntimeError("fixture application marker collision")
    work.mkdir(parents=True)
    install = work / "instance"
    report(work, "inputs", {"pair": str(args.pair.resolve()), "attestation": attestation,
                            "A": a, "B": b, "setupA": str(setup_a), "zipB": str(zip_b)})
    code = run_logged([str(setup_a), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                       f"/DIR={install}", f"/LOG={work / 'setup-a-inno.log'}"], work, "setup-a")
    if code != 0:
        raise RuntimeError(f"A Setup exit={code}; retain VM and raw log")
    image = install / "nexus-pipeline.exe"
    if sha(image) != a["imageSha256"] or not (install / a_marker).is_file():
        raise RuntimeError("A installation payload mismatch")
    unins = {path.name: sha(path) for path in manager.glob("unins*") if path.is_file()}
    if not any(name.endswith(".exe") for name in unins) or not any(name.endswith(".dat") for name in unins):
        raise RuntimeError("A native uninstaller incomplete")
    before = {"arp": registry_values(), "uninstaller": unins,
              "identitySha256": sha(manager / "identity.protected"),
              "helperSha256": sha(manager / "nexus-installer-helper.exe"),
              "imageSha256": sha(image)}
    report(work, "before", before)
    for folder in ("config", "data", "history", "logs", "plugins", "outputs", "user-assets"):
        destination = install / folder / "nxp-native-fixture-sentinel.txt"
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text("fixture-owned user data " + folder + "\n", encoding="utf-8")
    unknown = install / "unknown-native-fixture-sentinel.txt"
    unknown.write_text("fixture-owned unknown file\n", encoding="utf-8")
    port = 59000 + os.getpid() % 5000
    zip_bytes = zip_b.read_bytes()
    zip_name = zip_b.name
    package_digest = hashlib.sha256(zip_bytes).hexdigest()
    class Feed(BaseHTTPRequestHandler):
        def do_GET(self):
            origin = f"http://127.0.0.1:{self.server.server_port}"
            release = [{"draft": False, "prerelease": True, "tag_name": "v" + b["version"],
                        "name": "isolated native fixture", "body": "test fixture",
                        "assets": [{"name": name, "browser_download_url": origin + "/" + name}
                                   for name in (zip_name, zip_name + ".sha256")]}]
            payload = ({"/releases": json.dumps(release).encode(),
                        "/update-policy.json": json.dumps({"schemaVersion": 1,
                            "repository": "FlappiBakuse/NexusPipeline", "barriers": []}).encode(),
                        "/" + zip_name: zip_bytes,
                        "/" + zip_name + ".sha256": (package_digest + "\n").encode()}).get(self.path)
            if payload is None:
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
        def log_message(self, fmt, *values):
            with (work / "feed.log").open("a", encoding="utf-8") as log:
                log.write(fmt % values + "\n")
    server = ThreadingHTTPServer(("127.0.0.1", 0), Feed)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        settings = install / "config" / "settings.json"
        settings.write_text(json.dumps({"WebPort": port, "UpdateCheckEnabled": False,
                                        "UpdateAutoApplyEnabled": False,
                                        "PluginAutoUpdateEnabled": False,
                                        "AutoOpenBrowser": False}),
                            encoding="utf-8")
        host_environment = os.environ.copy()
        host_environment["NEXUS_UPDATE_URL"] = f"http://127.0.0.1:{server.server_port}/releases"
        with (work / "host-a.log").open("wb") as host_log:
            host = subprocess.Popen([str(image), "service"], cwd=install,
                                    stdin=subprocess.DEVNULL, stdout=host_log,
                                    stderr=subprocess.STDOUT, env=host_environment)
            wait_for(lambda: api(port, "GET", "/api/update/status"), 40, "A service startup")
            checked = api(port, "POST", "/api/update/check")
            report(work, "check", checked)
            if (checked.get("latest") != b["version"] or checked.get("available") is not True
                or checked.get("policyVerified") is not True or checked.get("canDownload") is not True):
                raise RuntimeError("A did not discover verified downloadable B update: "
                                   + json.dumps(checked, ensure_ascii=False))
            download = api(port, "POST", "/api/update/download")
            report(work, "download", download)
            wait_for(lambda: (value if (value := api(port, "GET", "/api/update/status")).get("state") == "ready" else None),
                     120, "B package ready")
            report(work, "apply", api(port, "POST", "/api/update/apply"))
            wait_for(lambda: sha(image) == b["imageSha256"], 240, "B application image")
            wait_for(lambda: api(port, "GET", "/api/status").get("version") == b["version"],
                     120, "B service startup")
            result_directory = install / ".nxp" / "state" / "updates"
            def completed_update():
                receipts = list(result_directory.glob("*.result.json"))
                if not receipts:
                    return None
                if len(receipts) != 1:
                    raise RuntimeError("expected one native update result receipt")
                return json.loads(receipts[0].read_text(encoding="utf-8-sig"))
            result = wait_for(completed_update, 120, "B update result receipt")
            report(work, "update-result", result)
            if (result.get("Version") != b["version"] or result.get("Succeeded") is not True
                or result.get("Code") != "committed"):
                raise RuntimeError("B update did not commit: " + json.dumps(result, ensure_ascii=False))
            try:
                host.wait(timeout=20)
            except subprocess.TimeoutExpired as error:
                raise RuntimeError("A process still running after update; keep VM for diagnosis") from error
        if not (install / b_marker).is_file() or (install / a_marker).exists():
            raise RuntimeError("B-only/A-only application inventory mismatch")
        if sha(manager / "nexus-installer-helper.exe") != b["imageSha256"]:
            raise RuntimeError("ownership helper did not advance to B")
        after_update = {"arp": registry_values(),
                        "uninstaller": {path.name: sha(path) for path in manager.glob("unins*") if path.is_file()},
                        "identitySha256": sha(manager / "identity.protected"),
                        "helperSha256": sha(manager / "nexus-installer-helper.exe"),
                        "imageSha256": sha(image),
                        "receiptFiles": [str(p) for p in (install / ".nxp" / "state" / "updates").glob("*.result.json")]}
        report(work, "after-update", after_update)
        if after_update["uninstaller"] != unins:
            raise RuntimeError("old native uninstaller changed during built-in update")
        unins_exe = next(manager / name for name in unins if name.endswith(".exe"))
        if str(unins_exe).casefold() not in str(after_update["arp"].get("UninstallString", {}).get("value", "")).casefold():
            raise RuntimeError("ARP does not reference A native uninstaller")
        stop_owned_updated_host(image, work)
        if run_logged([str(unins_exe), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                       f"/LOG={work / 'uninstall-inno.log'}"], work, "uninstall-a") != 0:
            raise RuntimeError("A native uninstaller failed; preserve VM")
        def uninstall_complete():
            try:
                registry_values()
            except FileNotFoundError:
                return not image.exists()
            return False
        wait_for(uninstall_complete, 120, "A native uninstaller completion")
        for item in b["applicationPayload"]:
            if item["path"].startswith("plugins/"):
                continue
            if (install / item["path"]).exists():
                raise RuntimeError("B application file survived A uninstaller: " + item["path"])
        for folder in ("config", "data", "history", "logs", "plugins", "outputs", "user-assets"):
            destination = install / folder / "nxp-native-fixture-sentinel.txt"
            if destination.read_text(encoding="utf-8") != "fixture-owned user data " + folder + "\n":
                raise RuntimeError("user sentinel changed: " + folder)
        if unknown.read_text(encoding="utf-8") != "fixture-owned unknown file\n":
            raise RuntimeError("unknown sentinel changed")
        try:
            registry_values()
        except FileNotFoundError:
            pass
        else:
            raise RuntimeError("ARP registration survived native uninstall")
        if not (manager / "identity.protected").is_file():
            raise RuntimeError("retained-data ownership identity missing")
        report(work, "after-uninstall", {"BApplicationRemoved": True,
                                          "userAndUnknownSentinelsRetained": True,
                                          "arpAbsent": True,
                                          "retainedIdentitySha256": sha(manager / "identity.protected")})
        print("PASS native A -> built-in B update -> unchanged A uninstaller -> B payload removed")
        return 0
    finally:
        server.shutdown()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"FAIL {error}", file=sys.stderr)
        sys.exit(1)
