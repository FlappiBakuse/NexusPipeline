const input = JSON.parse(__NEXUS_INPUT__);
const log = input.log || "";
const DONE_MARKERS = ["关闭游戏成功", "暂停运行"];
const IGNORE_APPS = ["等待大世界画面", "通知"];

let done = false;
for (const marker of DONE_MARKERS) {
  if (log.indexOf(marker) >= 0) {
    done = true;
    break;
  }
}
if (!done) {
  // 尚未运行结束（未出现结束关键字），持续等待
} else {
  const failed = [];
  const re = /指令\[ (.+?) \] 执行失败 返回状态/g;
  let m;
  while ((m = re.exec(log)) !== null) {
    const name = (m[1] || "").trim();
    if (name && IGNORE_APPS.indexOf(name) < 0 && failed.indexOf(name) < 0) {
      failed.push(name);
    }
  }
  if (failed.length === 0) {
    console.log(JSON.stringify({ status: "success", reason: "全部应用执行成功" }));
  } else {
    console.log(JSON.stringify({
      status: "success",
      reason: "一条龙运行完成，但部分应用执行失败",
      notifyText: "本次运行有应用执行失败：" + failed.join("、")
    }));
  }
}
