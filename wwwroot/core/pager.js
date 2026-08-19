const pagers = new Map();

export function pagerMarkup(key, page, pageSize, total) {
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  // v0.7.3+（用户需求）：未达分页条件（仅一页）时隐藏整个分页条（含条数信息）。
  if (totalPages <= 1) return "";
  const from = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const to = Math.min(total, page * pageSize);
  let pages = "";
  for (let p = 1; p <= totalPages; p++) {
    pages += `<button type="button" class="sm ${p === page ? "pager-active" : ""}" data-action="pager-page" data-pager="${key}" data-page="${p}" ${p === page ? 'aria-current="page"' : ""}>${p}</button>`;
  }
  return `<div class="pager" data-testid="pager-${key}" data-page-current="${page}" data-pages="${totalPages}"><span class="pager-info">共 ${total} 条${total ? `，第 ${from}-${to} 条` : ""}</span><button type="button" class="sm" data-action="pager-prev" data-pager="${key}" ${page <= 1 ? "disabled" : ""}>上一页</button>${pages}<button type="button" class="sm" data-action="pager-next" data-pager="${key}" ${page >= totalPages ? "disabled" : ""}>下一页</button></div>`;
}

export function registerPager(key, onChange) {
  pagers.set(key, onChange);
}

/** 将当前分页的新顺序写回全量数组，禁止把当前页隐式移动到全局首部。 */
export function replacePageOrder(items, page, pageSize, orderedKeys, keyOf = item => item.id) {
  const start = (page - 1) * pageSize;
  const pageLength = Math.min(pageSize, Math.max(0, items.length - start));
  if (start < 0 || pageLength !== orderedKeys.length) return [...items];
  const byKey = new Map(items.map(item => [String(keyOf(item)), item]));
  const ordered = orderedKeys.map(key => byKey.get(String(key))).filter(Boolean);
  if (ordered.length !== pageLength) return [...items];
  const result = [...items];
  result.splice(start, pageLength, ...ordered);
  return result;
}

export function pagerNavigate(key, action, target) {
  const onChange = pagers.get(key);
  if (!onChange) return;
  const container = target.closest(".pager");
  const current = parseInt(container?.dataset.pageCurrent || "1", 10) || 1;
  const totalPages = parseInt(container?.dataset.pages || "1", 10) || 1;
  let next = current;
  if (action === "prev") next = Math.max(1, current - 1);
  else if (action === "next") next = Math.min(totalPages, current + 1);
  else next = parseInt(target.dataset.page, 10) || current;
  if (next !== current) onChange(next);
}
