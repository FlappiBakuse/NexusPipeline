import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { copyReleaseArtifacts } from "../support/test-runtime.mjs";

test("runtime copy accepts but does not copy the verified build-cache manifest", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-runtime-copy-"));
  try {
    const release = path.join(root, "cache");
    const runtime = path.join(root, "runtime");
    fs.mkdirSync(path.join(release, "wwwroot"), { recursive: true });
    fs.mkdirSync(path.join(release, "plugins"), { recursive: true });
    fs.writeFileSync(path.join(release, "nexus-pipeline.exe"), "exe");
    fs.writeFileSync(path.join(release, "NexusPipeline.dll"), "dll");
    fs.writeFileSync(path.join(release, "wwwroot", "index.html"), "html");
    fs.writeFileSync(path.join(release, ".complete.json"), "{}\n");

    copyReleaseArtifacts(release, runtime);
    assert.equal(fs.readFileSync(path.join(runtime, "nexus-pipeline.exe"), "utf8"), "exe");
    assert.equal(fs.readFileSync(path.join(runtime, "wwwroot", "index.html"), "utf8"), "html");
    assert.equal(fs.existsSync(path.join(runtime, ".complete.json")), false);

    fs.writeFileSync(path.join(release, "unexpected.sidecar"), "no");
    assert.throws(() => copyReleaseArtifacts(release, path.join(root, "rejected")), /未允许的旁车文件/u);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
