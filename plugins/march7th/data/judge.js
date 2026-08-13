const input = JSON.parse(__NEXUS_INPUT__);
const log = input.log || "";
const DONE_MARKER = "游戏终止：StarRail";
const FAILURE_PATTERNS = [
  "每日实训未完成",
  "清体力未完成",
  "模拟宇宙未完成",
  "锄大地未完成",
  "遗器背包已满",
  "领取星琼失败"
];

if (log.indexOf(DONE_MARKER) >= 0) {
  const failedLines = [];
  for (const pattern of FAILURE_PATTERNS) {
    if (log.indexOf(pattern) >= 0) failedLines.push(pattern);
  }
  if (failedLines.length === 0) {
    console.log(JSON.stringify({ status: "success", reason: "全部任务执行成功" }));
  } else {
    console.log(JSON.stringify({ status: "failed", reason: "任务未完成：" + failedLines.join("、") }));
  }
} else if (/ \| ERROR \| 发生错误/.test(log)) {
  console.log(JSON.stringify({ status: "failed", reason: "运行发生错误，任务未完成" }));
}
