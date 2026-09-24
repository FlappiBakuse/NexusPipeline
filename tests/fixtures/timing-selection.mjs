import assert from "node:assert/strict";
import test from "node:test";

test("ER07 selected timing case", () => assert.ok(true));
if (process.env.TIMING_SCENARIO !== "missing") {
  const option = process.env.TIMING_SCENARIO === "skip" ? { skip: "selected case" }
    : process.env.TIMING_SCENARIO === "todo" ? { todo: "selected case" } : {};
  test("ER10 selected timing case", option, () => assert.ok(true));
}
test("outside selection", () => assert.ok(true));
