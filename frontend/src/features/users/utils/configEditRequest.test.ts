import { describe, expect, it } from "vitest";
import { buildConfigEditRequest } from "./configEditRequest";

describe("buildConfigEditRequest", () => {
  it("passes the candidate selection through session-level input override", () => {
    expect(buildConfigEditRequest("reuse")).toEqual({ action: "start", mode: "reuse" });
    expect(buildConfigEditRequest("reuse", { name: "configPath", value: "profiles/user.json" })).toEqual({
      action: "start",
      mode: "reuse",
      configInputName: "configPath",
      configInputValue: "profiles/user.json",
    });
    expect(buildConfigEditRequest("normal")).toEqual({
      action: "start",
      mode: "normal",
    });
  });
});
