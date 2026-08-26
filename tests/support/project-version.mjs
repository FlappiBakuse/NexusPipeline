import fs from "node:fs";
import path from "node:path";

const numericSemver = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;

export function parseProjectVersion(version) {
  const match = numericSemver.exec(String(version).trim());
  if (!match) {
    throw new Error(`项目版本必须是 numeric semver（major.minor.patch）：${version}`);
  }
  return {
    text: `${match[1]}.${match[2]}.${match[3]}`,
    major: Number(match[1]),
    minor: Number(match[2]),
    patch: Number(match[3]),
  };
}

export function readProjectVersion(projectRoot) {
  const csprojPath = path.join(projectRoot, "src", "NexusPipeline.csproj");
  const text = fs.readFileSync(csprojPath, "utf8");
  const match = /<Version>\s*([^<]+?)\s*<\/Version>/i.exec(text);
  if (!match) throw new Error(`未找到项目版本：${csprojPath}`);
  return parseProjectVersion(match[1]).text;
}

export function deriveCandidateVersion(projectVersion) {
  const parsed = parseProjectVersion(projectVersion);
  if (!Number.isSafeInteger(parsed.patch) || parsed.patch === Number.MAX_SAFE_INTEGER) {
    throw new Error(`项目 patch 版本无法安全递增：${projectVersion}`);
  }
  return `${parsed.major}.${parsed.minor}.${parsed.patch + 1}`;
}
