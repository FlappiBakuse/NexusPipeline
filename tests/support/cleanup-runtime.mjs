import fs from "node:fs";
import path from "node:path";
import { readRunMarker, stopSpawnedService } from "./test-runtime.mjs";
import { readListeningPids } from "./windows-process.mjs";

const [runRoot, runId] = process.argv.slice(2);
if (process.argv.length !== 4 || !path.isAbsolute(runRoot ?? "") || !/^[A-Za-z0-9_-]+$/.test(runId ?? "")) {
  throw new Error("Usage: cleanup-runtime <absolute run root> <runId>");
}
if (fs.existsSync(runRoot)) {
  const root = fs.realpathSync.native(runRoot);
  for (const name of ["ui", "runtime", "judge-runtime"]) {
    const directory = path.join(root, name);
    if (!fs.existsSync(directory)) continue;
    if (fs.lstatSync(directory).isSymbolicLink()) throw new Error("Linked runtime directory");
    const markerPath = path.join(directory, ".nxp", "test-run-marker.json");
    const marker = readRunMarker(markerPath);
    if (!marker) throw new Error(`Runtime ownership marker missing: ${name}`);
    if (marker.runId !== runId) throw new Error(`Runtime belongs to another run: ${name}`);
    await stopSpawnedService({
      child: null,
      exitFile: path.join(directory, ".nxp", "test-host.exit"),
      pidFilePath: path.join(directory, ".nxp", "runtime", "service.pid"),
      markerPath,
      exitWaitPollMs: 50,
      allowReusedPid: true,
    });
    const portFile = path.join(directory, ".nxp", "runtime", "web.port");
    if (fs.existsSync(portFile)) {
      const port = Number(fs.readFileSync(portFile, "utf8").trim());
      if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error("Invalid recorded runtime port");
      if (readListeningPids(port).length) throw new Error(`Runtime port remains open: ${port}`);
    }
  }
}
console.log("Runtime cleanup confirmed");
