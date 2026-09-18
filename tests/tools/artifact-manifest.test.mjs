import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import {
  artifactSetDigest,
  createArtifactManifest,
  createNoArtifactManifest,
  verifyArtifactManifest,
} from "../../tools/artifact-manifest.mjs";

test("真实产物 manifest 绑定文件集合与字节摘要", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nxp-artifact-manifest-"));
  try {
    fs.mkdirSync(path.join(root, "wwwroot"), { recursive: true });
    fs.writeFileSync(path.join(root, "nexus-pipeline.exe"), "candidate-a", "utf8");
    fs.writeFileSync(path.join(root, "wwwroot", "index.html"), "<main>a</main>", "utf8");
    const manifest = createArtifactManifest({ root, kind: "production", mode: "admin", producer: "test" });
    assert.equal(verifyArtifactManifest(root, manifest).ok, true);

    fs.writeFileSync(path.join(root, "nexus-pipeline.exe"), "candidate-b", "utf8");
    const verification = verifyArtifactManifest(root, manifest);
    assert.equal(verification.ok, false);
    assert.match(verification.issues.join("\n"), /字节摘要|文件集合/u);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("产物集合摘要由规范化文件条目决定，空产物显式为 null", () => {
  const files = [{ relativePath: "a.bin", byteLength: 1, sha256: "a".repeat(64) }];
  assert.match(artifactSetDigest(files), /^[0-9a-f]{64}$/u);
  const empty = createNoArtifactManifest({ mode: "ci", producer: "docs-i18n", reason: "docs" });
  assert.equal(empty.present, false);
  assert.equal(empty.artifactSetDigest, null);
});
