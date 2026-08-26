import fs from "node:fs";
import path from "node:path";

const pidPath = process.argv[2];
if (!pidPath) {
  console.error("long-lived-child.mjs 需要 PID 文件路径");
  process.exit(2);
}

fs.mkdirSync(path.dirname(pidPath), { recursive: true });
fs.writeFileSync(pidPath, `${process.pid}\n`, "ascii");

const stop = () => {
  try {
    if (fs.existsSync(pidPath)) fs.rmSync(pidPath, { force: true });
  } finally {
    process.exit(0);
  }
};

process.on("SIGINT", stop);
process.on("SIGTERM", stop);
setInterval(() => {}, 1000);
