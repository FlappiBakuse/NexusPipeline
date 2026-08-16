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

/** 脚本主程序图标加载失败时的通用占位图（内联 SVG，主题无关）。 */
export const scriptFallbackIcon = "data:image/svg+xml;charset=utf-8," + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40" viewBox="0 0 40 40"><rect width="40" height="40" rx="9" fill="#000" opacity=".07"/><path d="M11 9h13l6 6v16H11z" fill="none" stroke="#000" stroke-opacity=".38" stroke-width="2" stroke-linejoin="round"/><path d="M24 9v6h6" fill="none" stroke="#000" stroke-opacity=".38" stroke-width="2" stroke-linejoin="round"/></svg>');
