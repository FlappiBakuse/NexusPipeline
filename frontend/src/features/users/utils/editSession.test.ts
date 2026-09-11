import { describe, expect, it } from "vitest";
import { findRestorableEditSession } from "./editSession";

const users = [
  {
    id: "u1",
    name: "Alice",
    bindings: [{ scriptInstanceId: "s1" }, { scriptInstanceId: "s2" }],
  },
  { id: "u2", name: "Bob", bindings: [{ scriptInstanceId: "s1" }] },
];
const scripts = [
  { id: "s1", name: "Script One" },
  { id: "s2", name: "Script Two" },
];

describe("findRestorableEditSession", () => {
  it("matches a session by userId", () => {
    expect(
      findRestorableEditSession(
        [{ userId: "u2", userName: "ignored", scriptId: "s1", editMode: "reuse" }],
        users,
        scripts,
      ),
    ).toEqual({
      userId: "u2",
      scriptId: "s1",
      userName: "Bob",
      scriptName: "Script One",
      mode: "reuse",
    });
  });

  it("falls back to userName when userId is absent", () => {
    expect(
      findRestorableEditSession(
        [{ userName: "Alice", scriptId: "s2" }],
        users,
        scripts,
      ),
    ).toMatchObject({ userId: "u1", scriptId: "s2", userName: "Alice", scriptName: "Script Two", mode: "normal" });
  });

  it("does not restore when the binding is missing", () => {
    expect(
      findRestorableEditSession([{ userId: "u2", scriptId: "s2" }], users, scripts),
    ).toBeNull();
  });

  it("does not restore when the script instance no longer exists", () => {
    expect(
      findRestorableEditSession([{ userId: "u1", scriptId: "s1" }], users, [{ id: "s2", name: "Script Two" }]),
    ).toBeNull();
  });

  it("returns the first valid session when several are present", () => {
    expect(
      findRestorableEditSession(
        [
          { userId: "u2", scriptId: "s2" },
          { userId: "u1", scriptId: "s1", editMode: "fresh" },
        ],
        users,
        scripts,
      ),
    ).toMatchObject({ userId: "u1", scriptId: "s1", mode: "fresh" });
  });

  it("tolerates malformed payloads", () => {
    expect(findRestorableEditSession(null, users, scripts)).toBeNull();
    expect(findRestorableEditSession([null, 7, { userId: "u1" }], users, scripts)).toBeNull();
    expect(findRestorableEditSession([{ userId: "u1", scriptId: "s1" }], users, null)).toBeNull();
    expect(findRestorableEditSession([{ userId: "u1", scriptId: "s1" }], [{ id: "u1", name: "Alice", bindings: null }], scripts)).toBeNull();
  });

  it("normalizes a blank editMode to normal", () => {
    expect(
      findRestorableEditSession([{ userId: "u1", scriptId: "s1", editMode: "   " }], users, scripts),
    ).toMatchObject({ mode: "normal" });
  });
});
