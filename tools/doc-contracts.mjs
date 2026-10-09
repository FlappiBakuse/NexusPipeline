import fs from "node:fs";
import path from "node:path";

export function repositoryPath(root, base, relative) {
  if (typeof relative !== "string" || !relative || path.isAbsolute(relative) || /^[A-Za-z]:/.test(relative))
    throw new Error(`Invalid repository path: ${relative}`);
  const target = path.resolve(root, base, relative);
  const inside = value => { const name = path.relative(fs.realpathSync(root), value); return name !== ".." && !name.startsWith(".." + path.sep) && !path.isAbsolute(name); };
  if (!inside(target) || !fs.existsSync(target) || !inside(fs.realpathSync(target)))
    throw new Error(`Missing/outside repository path: ${relative}`);
  return target;
}

export function checkContracts(root) {
  const results = [];
  const read = file => fs.readFileSync(repositoryPath(root, "", file), "utf8");
  const one = (text, expression, label) => {
    const matches = [...text.matchAll(expression)];
    if (matches.length !== 1) throw new Error(`Cannot extract unique ${label}`);
    return matches[0][1];
  };
  try {
    const source = read("src/NexusPipeline.Plugin.Abstractions/PluginApi.cs");
    const version = name => {
      const body = one(source, new RegExp(`public static class ${name}\\s*\\{([^}]+)\\}`, "g"), name);
      return ["Major", "Minor"].map(field => one(body, new RegExp(`public const int ${field}\\s*=\\s*(\\d+)\\s*;`, "g"), `${name}.${field}`)).join(".");
    };
    const managed = version("PluginApiVersion"), frontend = version("FrontendApiVersion");
    const targets = [
      ["src/NexusPipeline.Plugin.Abstractions/PluginApi.cs", /public const string Text\s*=\s*"([\d.]+)"\s*;/g, frontend],
      ["frontend/src/plugin-bridge/runtime.ts", /export const FRONTEND_API_VERSION\s*=\s*"([\d.]+)"\s*;/g, frontend],
      ["docs/reference/plugin-api/managed.md", /当前 managed 插件精确声明 Plugin API `([\d.]+)`/g, managed],
      ["docs/reference/plugin-api/managed.md", /Frontend API 独立维持 `([\d.]+)`/g, frontend],
      ["docs/reference/plugin-api/frontend.md", /^## 前端插件运行时（Frontend API ([\d.]+)）$/gm, frontend],
      ["docs/reference/plugin-api/frontend.md", /Frontend API `([\d.]+)` 精确版本/g, frontend],
      ["docs/reference/plugin-api/frontend.md", /只有 `([\d.]+)` 被接受/g, frontend],
      ["docs/testing/policy.md", /^- Frontend API ([\d.]+) 的宿主外部契约/gm, frontend],
    ];
    for (const [file, expression, expected] of targets) {
      const text = read(file), matches = [...text.matchAll(expression)];
      if (matches.length !== 1) {
        results.push({ rule: "GOV-DOC-01", status: file.startsWith("docs/") ? "REVIEW" : "NOT_CHECKED", file, reason: "Designated current claim is ambiguous/missing; inspect its wording", expected });
      } else {
        const actual = matches[0][1];
        results.push({ rule: "GOV-DOC-01", status: actual === expected ? "PASS" : "FAIL", file,
          line: text.slice(0, matches[0].index).split("\n").length, actual, expected });
      }
    }
  } catch (error) {
    results.push({ rule: "GOV-DOC-01", status: "NOT_CHECKED", reason: error.message });
  }
  return results;
}
