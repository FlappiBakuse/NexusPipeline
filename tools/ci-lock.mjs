import fs from "node:fs";

const [file] = process.argv.slice(2);
if (!file) throw new Error("需要仓库锁文件路径");
const lock = JSON.parse(fs.readFileSync(file, "utf8"));
const ref = process.env.NEXUS_CANDIDATE_REF || lock.ref;
if (!/^FlappiBakuse\/NexusPipeline(?:-Plugins)?$/u.test(lock.repository) || !/^[0-9a-f]{40}$/u.test(ref)) {
  throw new Error("仓库锁必须包含已知仓库和完整 commit SHA");
}
const output = `ref=${ref}\nrepository=${lock.repository}\n`;
process.stdout.write(output);
if (process.env.GITHUB_OUTPUT) fs.appendFileSync(process.env.GITHUB_OUTPUT, output);
