import { describe, expect, it } from "vitest";
import { routes } from "./router";

/**
 * 路由表契约：正常页面全部按需加载，插件 route 由 PluginRouteHost 承载，
 * 未知路径保持空 shell 而不是重定向。
 */
describe("host route table", () => {
  const byPath = new Map(routes.map(route => [String(route.path), route]));

  it("covers every host page with a dedicated route", () => {
    for (const path of ["/dashboard", "/users", "/scripts", "/queues", "/dispatch", "/history", "/plugins", "/settings"]) {
      expect(byPath.has(path), `缺少路由 ${path}`).toBe(true);
    }
    expect(byPath.has("/ui-lab")).toBe(import.meta.env.DEV);
  });

  it("registers the plugin route with a catch-all segment matcher", () => {
    expect(byPath.has("/plugin/:pathMatch(.*)*")).toBe(true);
  });

  it("keeps unknown paths on an empty fallback instead of a redirect", () => {
    const fallback = byPath.get("/:pathMatch(.*)*");
    expect(fallback).toBeTruthy();
    expect(fallback!.component).toBeTruthy();
    expect(fallback!.redirect).toBeUndefined();
  });

  it("loads every page route as a dynamic import", () => {
    const lazy = ["/dashboard", "/users", "/scripts", "/queues", "/dispatch", "/history", "/plugins", "/settings", "/plugin/:pathMatch(.*)*"];
    for (const path of lazy) {
      const component = byPath.get(path)!.component as unknown;
      expect(typeof component, `${path} 未按需加载`).toBe("function");
    }
  });

  it("loads the component laboratory only in development", () => {
    const component = byPath.get("/ui-lab")?.component as unknown;
    expect(import.meta.env.DEV ? typeof component : component).toBe(import.meta.env.DEV ? "function" : undefined);
  });

  it("redirects the root path to the dashboard", () => {
    expect(byPath.get("/")?.redirect).toBe("/dashboard");
  });
});
