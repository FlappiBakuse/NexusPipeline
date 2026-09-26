from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from tools.host_installer import dependency_pair, render_script, verified_payload
from tools.host_release import HostReleaseError


class HostInstallerTests(unittest.TestCase):
    def test_installer_rejects_staging_byte_drift_and_unknown_files(self) -> None:
        with tempfile.TemporaryDirectory(prefix="nxp-installer-payload-") as temporary:
            root = Path(temporary)
            payload = root / "production"
            source = {
                "nexus-pipeline.exe": b"exe",
                "README.md": b"readme",
                "wwwroot/index.html": b"page",
                "plugins/EmulatorSupport/plugin.json": b"one",
                "plugins/LiveScreenshot/plugin.json": b"two",
            }
            for name, data in source.items():
                path = payload / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(data)
            metadata = {"version": "0.16.9", "payloadFiles": [
                {"path": name, "sizeBytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
                for name, data in source.items()]}
            files = verified_payload(payload, metadata)
            self.assertEqual(files, metadata["payloadFiles"])
            template = "@@VERSION@@\n@@FILES@@\n@@PAYLOAD_MANIFEST@@\n@@EXE_SHA256@@\n@@VERIFY_STAGED@@\n@@DESKTOP_URL@@\n@@DESKTOP_SHA256@@\n@@ASPNET_URL@@\n@@ASPNET_SHA256@@\n@@OUTPUT_DIR@@"
            dependencies = {name: {"url": "https://builds.dotnet.microsoft.com/dotnet/one.exe", "sha256": "a" * 64}
                            for name in ("Microsoft.WindowsDesktop.App", "Microsoft.AspNetCore.App")}
            script = render_script(template, production_root=payload, output_dir=root / "out",
                                   metadata=metadata, files=files, dependencies=dependencies)
            self.assertIn("onlyifdoesntexist", script)
            manifest_line = next(line for line in script.splitlines() if line.startswith('[{"Path":'))
            inventory = json.loads(manifest_line)
            self.assertEqual({item["Path"] for item in inventory}, {"nexus-pipeline.exe", "README.md", "wwwroot/index.html"})
            self.assertEqual(next(item["Sha256"] for item in inventory if item["Path"] == "README.md"),
                             hashlib.sha256(source["README.md"]).hexdigest())
            self.assertIn("VerifyStagedFile('README.md'", script)
            self.assertIn("DestDir: \"{app}\\.nxp-update\\staging\\0.16.9\"", script)
            self.assertNotIn("staging\\0.16.9\\plugins", script)

            (payload / "plugins/LiveScreenshot/plugin.json").write_bytes(b"changed")
            with self.assertRaisesRegex(HostReleaseError, "字节不同"):
                verified_payload(payload, metadata)
            (payload / "plugins/LiveScreenshot/plugin.json").write_bytes(b"two")
            (payload / "config").mkdir()
            (payload / "config/settings.json").write_bytes(b"secret")
            with self.assertRaisesRegex(HostReleaseError, "非应用文件"):
                verified_payload(payload, metadata)

    def test_runtime_lock_rejects_nonofficial_or_wrong_architecture(self) -> None:
        with tempfile.TemporaryDirectory(prefix="nxp-installer-dependencies-") as temporary:
            path = Path(temporary) / "deps.json"
            items = [{"framework": name, "version": "8.0.31", "rid": "win-x64",
                      "url": "https://builds.dotnet.microsoft.com/dotnet/runtime.exe", "sha256": "a" * 64}
                     for name in ("Microsoft.WindowsDesktop.App", "Microsoft.AspNetCore.App")]
            path.write_text(json.dumps({"schemaVersion": 1, "dependencies": items}), encoding="utf-8")
            self.assertEqual(len(dependency_pair(path)), 2)
            items[0]["url"] = "https://example.invalid/runtime.exe"
            path.write_text(json.dumps({"schemaVersion": 1, "dependencies": items}), encoding="utf-8")
            with self.assertRaisesRegex(HostReleaseError, "微软官方"):
                dependency_pair(path)
            items[0]["url"] = "https://builds.dotnet.microsoft.com/dotnet/runtime.exe"
            items[0]["rid"] = "win-x86"
            path.write_text(json.dumps({"schemaVersion": 1, "dependencies": items}), encoding="utf-8")
            with self.assertRaisesRegex(HostReleaseError, "架构"):
                dependency_pair(path)


if __name__ == "__main__":
    unittest.main()
