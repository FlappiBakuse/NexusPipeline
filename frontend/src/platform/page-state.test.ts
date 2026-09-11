import { describe, expect, it } from "vitest";
import { disposePage, enterPage, isCurrent, registerInterval, releaseController, state, trackController } from "./page-state";

/**
 * 页面生命周期的 route token 代际与资源清理：
 * 离开页面必须终止该页面的定时器与在途请求，旧页面的延迟回调不得再执行。
 */
describe("page state lifecycle", () => {
  it("advances the route token and disposes the previous page resources", () => {
    const first = enterPage("users");
    expect(isCurrent("users", first)).toBe(true);

    const interval = registerInterval(setInterval(() => {}, 10_000));
    const controller = trackController(new AbortController());
    expect(state.timers.has(interval)).toBe(true);
    expect(state.controllers.has(controller)).toBe(true);

    const second = enterPage("scripts");
    expect(second).toBeGreaterThan(first);
    expect(isCurrent("users", first)).toBe(false);
    expect(isCurrent("scripts", second)).toBe(true);
    expect(state.timers.has(interval)).toBe(false);
    expect(controller.signal.aborted).toBe(true);
    expect(state.controllers.has(controller)).toBe(false);

    disposePage();
  });

  it("stops timers registered for the current page when the page is disposed", () => {
    enterPage("history");
    const interval = registerInterval(setInterval(() => {}, 10_000));
    disposePage();
    expect(state.timers.size).toBe(0);
    expect(state.timers.has(interval)).toBe(false);
  });

  it("releases tracked controllers without aborting them", () => {
    const controller = trackController(new AbortController());
    releaseController(controller);
    expect(controller.signal.aborted).toBe(false);
    expect(state.controllers.has(controller)).toBe(false);
    disposePage();
  });

  it("exposes the current page name and token to the plugin bridge", () => {
    const token = enterPage("dispatch");
    expect(state.page).toBe("dispatch");
    expect(state.routeToken).toBe(token);
    disposePage();
  });
});
