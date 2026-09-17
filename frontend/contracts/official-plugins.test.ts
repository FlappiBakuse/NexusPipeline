import { afterEach, describe, expect, it, vi } from "vitest";
import { registerNexusElements } from "../src/ui/register";

// @ts-expect-error Cross-repository JavaScript fixture resolved by Vitest.
import * as fixture from "@official-plugins/tools/Test-FrontendPlugins.mjs";
const modules = {
  // @ts-expect-error Built official plugin ESM.
  CustomWallpaper: () => import("@official-plugins/plugins/general/CustomWallpaper/web/main.js"),
  // @ts-expect-error Built official plugin ESM.
  GameCheckIn: () => import("@official-plugins/plugins/general/GameCheckIn/web/main.js"),
  // @ts-expect-error Built official plugin ESM.
  LiveScreenshot: () => import("@official-plugins/plugins/general/LiveScreenshot/web/main.js"),
};
const flush = async () => { await new Promise(resolve => setTimeout(resolve, 0)); };
registerNexusElements();

afterEach(() => { document.body.replaceChildren(); vi.unstubAllGlobals(); });

describe("official plugin modules with registered host elements", () => {
  it.each(["CustomWallpaper", "GameCheckIn", "LiveScreenshot"])("mounts, interacts and disposes %s", async artifact => {
    const module = await modules[artifact as keyof typeof modules]();
    const metrics = fixture.createMetrics();
    const registrations: any[] = [];
    const state = artifact === "CustomWallpaper" ? fixture.wallpaperTestState()
      : artifact === "GameCheckIn" ? fixture.gameCheckInTestState() : fixture.defaultTestState();
    if (artifact === "CustomWallpaper") {
      state.assets.push({ ...state.assets[0], id: "b".repeat(64), originalName: "second.png" });
      state.order.push("b".repeat(64));
    }
    vi.stubGlobal("ResizeObserver", class { observe() {} unobserve() {} disconnect() {} });
    const urls = new Set<string>();
    vi.stubGlobal("URL", class extends URL {
      static createObjectURL() { const url = `blob:official-${Math.random()}`; urls.add(url); return url; }
      static revokeObjectURL(url: string) { urls.delete(url); }
    });
    const host = fixture.createMockHost(artifact, registrations, metrics, state);
    const cleanup = fixture.activationCleanup(await module.activate(host));
    const renderCleanups: (() => unknown)[] = [];
    try {
      if (artifact === "GameCheckIn") {
        const view = document.createElement("main");
        view.id = "view";
        document.body.append(view);
        await metrics.routeRegistrations[0].handler(1, ["plugin", "game-checkin", "tasks"], host);
        await flush();
        const add = [...view.querySelectorAll("nxp-button")].find(element => element.getAttribute("label") === "添加签到任务");
        expect(add?.querySelector("button")).toBeTruthy();
        (add!.querySelector("button") as HTMLButtonElement).click();
        await flush();
        const name = view.querySelector("nxp-text-input#gci-task-name input") as HTMLInputElement;
        expect(name).toBeTruthy();
        name.value = "真实公共元素任务";
        name.dispatchEvent(new Event("input", { bubbles: true }));
        await flush();
        expect(name.value).toBe("真实公共元素任务");
      } else {
        for (const registration of registrations) {
          const element = document.createElement("section");
          document.body.append(element);
          renderCleanups.push(await registration.renderer({ element, context: { mode: "test", primaryId: "run-test" } }));
          await flush();
          if (artifact === "CustomWallpaper") {
            await vi.waitFor(() => expect(element.querySelector("nxp-drag-handle button")).toBeTruthy());
            expect(element.querySelector("nxp-file-picker input[type=file]")).toBeTruthy();
            const handle = element.querySelector("nxp-drag-handle button") as HTMLButtonElement;
            handle.focus();
            handle.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true }));
            await vi.waitFor(() => expect(metrics.apiCalls.some((call: any) =>
              call.method === "PUT" && JSON.stringify(call.body?.order) === JSON.stringify([...state.order].reverse()),
            )).toBe(true));
            const range = element.querySelector("nxp-range input") as HTMLInputElement;
            range.value = "0";
            range.dispatchEvent(new Event("change", { bubbles: true }));
            await vi.waitFor(() => expect(metrics.apiCalls.some((call: any) =>
              call.method === "PUT" && call.body?.effects?.blurPx === 0,
            )).toBe(true));
          } else {
            expect(element.querySelector("nxp-badge")?.textContent).toBeTruthy();
          }
        }
      }
    } finally {
      for (const dispose of renderCleanups.reverse()) await dispose();
      await cleanup();
      await flush();
    }
    expect(registrations.every(item => item.disposed)).toBe(true);
    expect(metrics.routeRegistrations.every((item: any) => item.disposed)).toBe(true);
    expect(urls.size).toBe(0);
  });
});
