import { describe, expect, it } from "vitest";
import { scriptDraftFrom, scriptPayload } from "./scriptTypes";

describe("specialized script defaults", () => {
  it("enables force close for new specialized drafts and preserves explicit user choices", () => {
    for (const plugin of ["baah", "maas", "oknte"]) {
      const draft = scriptDraftFrom(null, plugin);
      expect(scriptPayload(draft).forceCloseGame).toBe(true);
      draft.forceCloseGame = false;
      expect(scriptPayload(scriptDraftFrom(scriptPayload(draft))).forceCloseGame).toBe(false);
    }
    expect(scriptDraftFrom(null).forceCloseGame).toBe(false);
    expect(scriptDraftFrom(null, "provider:maa").forceCloseGame).toBe(false);
  });
});
