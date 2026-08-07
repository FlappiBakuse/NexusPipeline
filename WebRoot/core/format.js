export function esc(value) {
  return String(value ?? "").replace(/[&<>"']/g, char => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  }[char]));
}

export function fmtTime(value) {
  if (!value) return "-";
  return new Date(value).toLocaleString("zh-CN", { hour12: false });
}

export function statusBadge(status) {
  if (status === "success") return '<span class="badge ok">成功</span>';
  if (status === "partial") return '<span class="badge warn">部分失败</span>';
  if (status === "running") return '<span class="badge blue">运行中</span>';
  if (status === "cancelled") return '<span class="badge warn">已取消</span>';
  if (status === "error") return '<span class="badge bad">异常</span>';
  return '<span class="badge bad">失败</span>';
}

export function finalStatusOf(record) {
  return record.finalStatus || record.status;
}

export function dayDesc(days = []) {
  const names = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
  if (days.length >= 7) return "每天";
  return days.map(day => names[day]).join("/");
}

export function actionLabel(action) {
  return {
    none: "无操作", exit: "退出软件", sleep: "休眠", reboot: "重启", shutdown: "关机",
  }[action] || action;
}
