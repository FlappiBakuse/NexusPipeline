import { describe, expect, it } from "vitest";
import { globalContributionValuesForSave, type Contribution } from "./globalSettings";

describe("globalContributionValuesForSave", () => {
  it("omits read-only status fields and keeps untouched secrets", () => {
    const contribution: Contribution = {
      pluginName: "game-checkin",
      id: "user-settings",
      fields: [
        { key: "enabled", label: "启用", type: "switch" },
        { key: "token", label: "Token", type: "secret" },
        { key: "lastStatus", label: "最近状态", type: "status", readOnly: true },
      ],
      values: {
        enabled: true,
        token: { configured: false },
        lastStatus: "最近一次签到成功",
      },
    };

    expect(globalContributionValuesForSave(contribution, {})).toEqual({
      enabled: true,
      token: { action: "keep" },
    });
  });

  it("serializes an edited secret and preserves multi-select values", () => {
    const contribution: Contribution = {
      pluginName: "game-checkin",
      id: "user-settings",
      fields: [
        { key: "token", label: "Token", type: "secret" },
        { key: "games", label: "游戏", type: "multi-select" },
      ],
      values: { token: "new-token", games: ["game-a", "game-b"] },
    };

    expect(globalContributionValuesForSave(contribution, {
      "game-checkin::user-settings::token": "set",
    })).toEqual({
      token: { action: "set", value: "new-token" },
      games: ["game-a", "game-b"],
    });
  });
});
