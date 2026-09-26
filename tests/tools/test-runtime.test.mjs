import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { copyReleaseArtifacts, resolveTestRunRoot } from "../support/test-runtime.mjs";

test("explicit short artifact roots require ownership and keep run identities scoped", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-artifact-root-"));
  const saved = process.env.NEXUS_TEST_ARTIFACT_ROOT;
  try {
    delete process.env.NEXUS_TEST_ARTIFACT_ROOT;
    assert.equal(resolveTestRunRoot(root, "sample"), path.join(root, "tests", ".artifacts", "runs", "sample"));
    process.env.NEXUS_TEST_ARTIFACT_ROOT = root;
    assert.throws(() => resolveTestRunRoot(root, "sample"), /ENOENT/);
    fs.writeFileSync(path.join(root, ".nxp-test-artifact-root.json"), JSON.stringify({ schemaVersion: 1,
      owner: "NexusPipeline.Tests", directory: root }));
    assert.equal(resolveTestRunRoot(root, "sample"), path.join(root, "runs", "sample"));
    assert.throws(() => resolveTestRunRoot(root, "../another"), /identity/);
    fs.writeFileSync(path.join(root, ".nxp-test-artifact-root.json"), JSON.stringify({ schemaVersion: 1,
      owner: "NexusPipeline.Tests", directory: path.join(root, "another") }));
    assert.throws(() => resolveTestRunRoot(root, "sample"), /Unowned/);
  } finally {
    if (saved === undefined) delete process.env.NEXUS_TEST_ARTIFACT_ROOT;
    else process.env.NEXUS_TEST_ARTIFACT_ROOT = saved;
    fs.rmSync(root, { recursive: true, force: true });
  }
});

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
