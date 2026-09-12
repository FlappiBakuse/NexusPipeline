import { describe, expect, it } from "vitest";
import { NEXUS_PUBLIC_ELEMENTS } from "../ui/register";

/**
 * 桥接层的静态实现边界：声明式插件表单直接使用宿主公开的 nxp-* 元素，
 * 桥接目录内不得再出现自行拼装的控件 DOM，也不得绕过 host-adapter 直接依赖平台模块。
 */

const bridgeSources = import.meta.glob("./*.ts", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
const publicElementNames = new Set(Object.keys(NEXUS_PUBLIC_ELEMENTS));

function productionSources(): Array<[string, string]> {
  return Object.entries(bridgeSources).filter(([file]) => !file.endsWith(".test.ts"));
}

describe("plugin bridge implementation boundary", () => {
  it("builds declarative field controls from public elements only", () => {
    expect(productionSources().length).toBeGreaterThan(0);
    for (const [file, source] of productionSources()) {
      const relative = file.replace(/^\.\//u, "");
      for (const pattern of ["innerHTML", "insertAdjacentHTML", "outerHTML"]) {
        expect(source, `${relative} 不应通过 ${pattern} 拼装控件 DOM`).not.toContain(pattern);
      }
      const elementNames = [...source.matchAll(/createElement\(\s*["']([a-z0-9-]+)["']/gu)].map(match => match[1]);
      for (const name of elementNames.filter(value => value.startsWith("nxp-"))) {
        expect(publicElementNames.has(name), `${relative} 使用了未公开的宿主元素 ${name}`).toBe(true);
      }
    }
  });

  it("keeps platform dependencies behind the host adapter", () => {
    for (const [file, source] of productionSources()) {
      const relative = file.replace(/^\.\//u, "");
      if (relative === "host-adapter.ts") continue;
      const platformImports = [...source.matchAll(/from\s+["']\.\.\/([^"']+)["']/gu)].map(match => match[1]);
      expect(platformImports, `${relative} 应通过 host-adapter 使用宿主平台服务`).toEqual([]);
    }
  });

  it("maps every declarative field type to a public element", () => {
    const source = bridgeSources["./controls.ts"];
    expect(source).toBeTruthy();
    const tags = [...source.matchAll(/tag:\s*"([a-z0-9-]+)"/gu)].map(match => match[1]);
    expect(tags.length).toBeGreaterThan(0);
    for (const tag of new Set(tags)) {
      expect(publicElementNames.has(tag), `字段控件映射到了未公开元素 ${tag}`).toBe(true);
    }
  });
});
