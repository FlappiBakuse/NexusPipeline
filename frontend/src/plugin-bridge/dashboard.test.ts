import { describe, expect, it, vi } from "vitest";
import { disposeDashboardCard, registerDashboardCard, renderDashboardCard } from "./dashboard";
describe("dashboard renderer lifecycle", () => {
  it("preserves a mounted renderer through repeated status paints", async () => {
    const element=document.createElement("div"), cleanup=vi.fn(), renderer=vi.fn(()=>cleanup);
    const registration=registerDashboardCard("stable-test","carousel",renderer);
    await renderDashboardCard(element,"plugin:stable-test:carousel");await renderDashboardCard(element,"plugin:stable-test:carousel");
    expect(renderer).toHaveBeenCalledTimes(1);expect(cleanup).not.toHaveBeenCalled();
    registration.dispose();expect(cleanup).toHaveBeenCalledTimes(1);
  });
  it("aborts before cleanup and disposes late async render results", async () => {
    const element=document.createElement("div");let resolve!: (cleanup:()=>void)=>void;let signal!:AbortSignal;
    const cleanup=vi.fn(()=>expect(signal.aborted).toBe(true));
    const registration=registerDashboardCard("late-test","carousel",surface=>{signal=surface.signal;return new Promise(done=>{resolve=done;});});
    const pending=renderDashboardCard(element,"plugin:late-test:carousel");disposeDashboardCard(element);resolve(cleanup);await pending;
    registration.dispose();expect(cleanup).toHaveBeenCalledTimes(1);
  });
  it("rejects duplicate and foreign local IDs", () => {
    const registration=registerDashboardCard("identity-test","carousel",()=>()=>{});
    expect(()=>registerDashboardCard("identity-test","carousel",()=>()=>{})).toThrow();
    expect(()=>registerDashboardCard("identity-test","plugin:foreign:carousel",()=>()=>{})).toThrow();registration.dispose();
  });
  it("isolates an aborted asynchronous surface from a replacement registration", async () => {
    const element = document.createElement("div"); document.body.append(element);
    let oldSurface!: HTMLElement, newSurface!: HTMLElement, finish!: (cleanup: () => void) => void;
    const cleanup = vi.fn();
    const old = registerDashboardCard("replacement-test", "card", surface => {
      oldSurface = surface.element; return new Promise(resolve => { finish = resolve; });
    });
    const pending = renderDashboardCard(element, "plugin:replacement-test:card"); old.dispose();
    const replacement = registerDashboardCard("replacement-test", "card", surface => { newSurface = surface.element; });
    await renderDashboardCard(element, "plugin:replacement-test:card");
    expect(oldSurface.isConnected).toBe(false); expect(newSurface.parentElement).toBe(element);
    oldSurface.replaceChildren(document.createElement("aside")); finish(cleanup); await pending;
    expect(newSurface.parentElement).toBe(element); expect(cleanup).toHaveBeenCalledTimes(1);
    replacement.dispose(); element.remove();
  });
});
