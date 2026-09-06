import test from "node:test";
import assert from "node:assert/strict";
import { buildConfigEditRequest } from "../../wwwroot/views/users/config-edit.js";

test("配置候选选择通过编辑会话临时覆盖传递", () => {
  assert.deepEqual(
    buildConfigEditRequest("reuse"),
    { action: "start", mode: "reuse" },
  );
  assert.deepEqual(
    buildConfigEditRequest("reuse", { name: "configPath", value: "profiles/user.json" }),
    {
      action: "start",
      mode: "reuse",
      configInputName: "configPath",
      configInputValue: "profiles/user.json",
    },
  );
});
