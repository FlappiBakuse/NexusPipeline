import { describe, expect, it } from "vitest";
import { scriptDraftFrom, scriptPayload } from "./scriptTypes";

describe("specialized script defaults", () => {
  it("preserves the committed provider revision through ordinary script editing", () => {
    const script = { id: "owned", name: "Maa", executionProviderId: "maa-framework", executionProviderConfigId: "profile", executionProviderConfigRevision: "committed_revision" };
    const draft = scriptDraftFrom(script);
    draft.name = "Renamed";
    expect(scriptPayload(draft).executionProviderConfigRevision).toBe("committed_revision");
  });
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
