import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const sharedSource = fs.readFileSync(
  path.join(projectRoot, "wwwroot", "views", "users", "shared.js"),
  "utf8",
);

function functionBody(name, nextName) {
  const start = sharedSource.indexOf(`async function ${name}`);
  const nextCandidates = [
    sharedSource.indexOf(`\nasync function ${nextName}`, start),
    sharedSource.indexOf(`\nexport async function ${nextName}`, start),
  ].filter(index => index >= 0);
  const end = nextCandidates.length > 0 ? Math.min(...nextCandidates) : -1;
  assert.notEqual(start, -1, `未找到函数：${name}`);
  assert.notEqual(end, -1, `未找到函数边界：${nextName}`);
  return sharedSource.slice(start, end);
}

test("配置候选选择通过编辑会话临时覆盖传递", () => {
  const startBody = functionBody("startEditConfig", "adoptConfigCandidate");
  const adoptBody = functionBody("adoptConfigCandidate", "chooseFirstEditConfigMode");

  assert.match(startBody, /inputOverride = null/);
  assert.match(startBody, /configInputName = inputOverride\.name/);
  assert.match(startBody, /configInputValue = inputOverride\.value/);
  assert.match(adoptBody, /startEditConfig\(userId, scriptId, mode, \{ name: inputName, value: candidate \}\)/);
  assert.doesNotMatch(adoptBody, /api\("PUT"/u);
  assert.doesNotMatch(adoptBody, /configInputs\[inputName\]/u);
});
