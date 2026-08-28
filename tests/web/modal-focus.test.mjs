import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const modalSource = readFileSync(new URL("../../wwwroot/core/modal.js", import.meta.url), "utf8");

test("modal focus recovery does not reset the scroll position", () => {
  assert.match(modalSource, /if \(first\) first\.focus\(\{ preventScroll: true \}\);/);
});
