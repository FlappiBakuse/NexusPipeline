import test from "node:test";
import assert from "node:assert/strict";
import { createBuildFingerprint, fingerprintPayload } from "../../tools/ci-fingerprint.mjs";

test("build fingerprint binds mode, manifest, build arguments and toolchain", () => {
  const options = {
    gitSha: "head-sha",
    sourceHash: "source-hash",
    toolchain: { node: "v24", dotnet: "8.0.0", npm: "10.0.0" },
  };
  const production = fingerprintPayload(options);
  const testHost = fingerprintPayload({ ...options, mode: "test-host" });
  assert.equal(production.applicationManifest, "src/app.manifest");
  assert.equal(testHost.applicationManifest, "src/app.test.manifest");
  assert.ok(production.buildArguments.includes("src/NexusPipeline.csproj"));
  assert.ok(testHost.buildArguments.includes("-p:NexusTestHost=true"));
  assert.notDeepEqual(production, testHost);
  assert.equal(createBuildFingerprint(options), createBuildFingerprint(options));
  assert.notEqual(createBuildFingerprint(options), createBuildFingerprint({ ...options, mode: "test-host" }));
});

test("fingerprint payload remains free of local paths", () => {
  const payload = fingerprintPayload({
    gitSha: "head-sha",
    sourceHash: "source-hash",
    toolchain: { node: "v24", dotnet: "8.0.0", npm: "10.0.0" },
  });
  assert.match(payload.frontendResourceSource, /^frontend\//u);
  assert.doesNotMatch(JSON.stringify(payload), /[A-Za-z]:[\\/]/u);
});
