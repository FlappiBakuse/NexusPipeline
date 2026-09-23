import { describe, expect, it, vi } from "vitest";
import { taskOutcomeLabel } from "./taskLabels";
vi.mock("../../../platform/i18n", () => ({ t: (key: string) => key }));
describe("task display labels", () => {
  it("localizes protocol outcomes without replacing external diagnostic text", () => {
    expect(taskOutcomeLabel("incomplete")).toBe("tasks.outcome.incomplete");
    expect(taskOutcomeLabel("tasks.all_satisfied")).toBe("tasks.outcome.satisfied");
    expect(taskOutcomeLabel("Custom failure detail")).toBe("Custom failure detail");
    expect(taskOutcomeLabel()).toBe("-");
  });
});
