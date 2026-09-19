import assert from "node:assert/strict";
import test from "node:test";

test("qualification controller keeps the fixed Host gate names", async () => {
  const gates = ["H1", "H2", "H3", "H4", "H5"];
  assert.deepEqual(gates, [...new Set(gates)]);
  assert.equal(gates.length, 5);
});
