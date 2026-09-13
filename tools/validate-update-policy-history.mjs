import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

export const POLICY_PATH = "update-policy.json";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const VERSION_PATTERN = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(beta|rc)\.(0|[1-9]\d*))?$/u;
const MAX_INT32 = 2_147_483_647;

function parseVersion(value, label) {
  if (typeof value !== "string") {
    throw new Error(`${label} version 必须是字符串`);
  }
  const match = VERSION_PATTERN.exec(value);
  if (!match) {
    throw new Error(`${label} version 不符合 Nexus 版本格式：${value}`);
  }
  const numbers = [match[1], match[2], match[3], match[5] || "0"].map(Number);
  if (numbers.some(value => !Number.isSafeInteger(value) || value > MAX_INT32)) {
    throw new Error(`${label} version 数值超出宿主支持范围：${value}`);
  }
  const stage = match[4] === "beta" ? 0 : match[4] === "rc" ? 1 : 2;
  return {
    major: numbers[0],
    minor: numbers[1],
    patch: numbers[2],
    stage,
    stageNumber: numbers[3],
  };
}

function compareVersions(left, right) {
  for (const key of ["major", "minor", "patch", "stage", "stageNumber"]) {
    if (left[key] !== right[key]) return left[key] < right[key] ? -1 : 1;
  }
  return 0;
}

function parsePolicyText(text, label) {
  let document;
  try {
    document = JSON.parse(text);
  } catch (error) {
    throw new Error(`${label} JSON 无效：${error.message}`);
  }
  if (!document || typeof document !== "object" || Array.isArray(document)) {
    throw new Error(`${label} 必须是 JSON 对象`);
  }
  if (!Array.isArray(document.barriers)) {
    throw new Error(`${label} 缺少 barriers 数组`);
  }
  return document;
}

function optionalText(value) {
  return value === undefined || value === null ? null : String(value);
}

function sameBarrier(left, right) {
  return left?.version === right?.version
    && left?.code === right?.code
    && optionalText(left?.migrationUrl) === optionalText(right?.migrationUrl);
}

/**
 * 验证 policy 历史只能追加 barrier。生产 schema 校验由 C# UpdatePolicy.TryParse 负责；
 * 本工具只比较历史不可变字段与版本顺序，避免在 Node 中复制完整生产 schema。
 */
export function validatePolicyHistory(baseText, headText) {
  const head = parsePolicyText(headText, "head update-policy.json");
  if (baseText === null || baseText === undefined) {
    return { basePresent: false, baseCount: 0, headCount: head.barriers.length };
  }

  const base = parsePolicyText(baseText, "base update-policy.json");
  if (head.barriers.length < base.barriers.length) {
    throw new Error("update-policy.json 不能删除历史 barrier");
  }

  for (let index = 0; index < base.barriers.length; index++) {
    if (!sameBarrier(base.barriers[index], head.barriers[index])) {
      throw new Error(`历史 barrier ${index + 1} 被删除、修改或重新排序`);
    }
  }

  let previous = null;
  if (base.barriers.length > 0) {
    previous = parseVersion(base.barriers.at(-1).version, "base barrier");
  }
  for (let index = base.barriers.length; index < head.barriers.length; index++) {
    const current = parseVersion(head.barriers[index]?.version, `head barrier ${index + 1}`);
    if (previous !== null && compareVersions(current, previous) <= 0) {
      throw new Error("新增 barrier 必须严格追加在历史最后一个版本之后");
    }
    previous = current;
  }
  return {
    basePresent: true,
    baseCount: base.barriers.length,
    headCount: head.barriers.length,
  };
}

function readGitFile(ref) {
  const result = spawnSync("git", ["show", `${ref}:${POLICY_PATH}`], {
    cwd: projectRoot,
    encoding: "utf8",
    maxBuffer: 512 * 1024,
  });
  if (result.status === 0) return result.stdout;
  const stderr = (result.stderr || "").trim();
  if (/does not exist|exists on disk, but not in/u.test(stderr)) return null;
  throw new Error(`读取 ${ref}:${POLICY_PATH} 失败：${stderr || `git show 退出码 ${result.status}`}`);
}

function isZeroSha(value) {
  return /^0+$/u.test(value || "");
}

function parseArguments(argv) {
  const options = { base: null, head: null };
  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];
    if (argument === "--base") {
      options.base = argv[++index];
    } else if (argument === "--head") {
      options.head = argv[++index];
    } else {
      throw new Error(`未知参数：${argument}`);
    }
  }
  return options;
}

function main() {
  const options = parseArguments(process.argv.slice(2));
  const policyPath = path.join(projectRoot, POLICY_PATH);
  const workingTreeText = fs.readFileSync(policyPath, "utf8");
  const event = process.env.GITHUB_EVENT_NAME || "";
  const baseRef = options.base
    || process.env.NEXUS_UPDATE_POLICY_BASE_SHA
    || (event === "pull_request" ? process.env.GITHUB_BASE_SHA : process.env.GITHUB_EVENT_BEFORE)
    || "";
  const headRef = options.head
    || process.env.NEXUS_UPDATE_POLICY_HEAD_SHA
    || process.env.GITHUB_SHA
    || "";

  if (!baseRef || isZeroSha(baseRef)) {
    parsePolicyText(workingTreeText, "当前 update-policy.json");
    console.log("[update-policy] 无可比较的历史提交，已完成当前 JSON 结构检查。");
    return 0;
  }

  const baseText = readGitFile(baseRef);
  const headText = headRef ? readGitFile(headRef) : workingTreeText;
  if (headText === null) {
    throw new Error(`head 提交缺少 ${POLICY_PATH}`);
  }
  const result = validatePolicyHistory(baseText, headText);
  if (!result.basePresent) {
    console.log(`[update-policy] 首次引入策略文件，head barriers=${result.headCount}。`);
  } else {
    console.log(`[update-policy] append-only 校验通过：${result.baseCount} → ${result.headCount} 个 barriers。`);
  }
  return 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    process.exitCode = main();
  } catch (error) {
    console.error(`[update-policy] 校验失败：${error.message}`);
    process.exitCode = 1;
  }
}
