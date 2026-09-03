/**
 * 判断候选实体名称是否与集合中的其他实体冲突。
 * 名称比较与宿主规则保持一致：去除首尾空白后按不区分大小写比较。
 * 该规则不读取 state、不修改集合，也不依赖 DOM。
 */
export function hasEntityNameConflict(items, candidateName, ignoreId = null) {
  const candidate = String(candidateName ?? "").trim().toLocaleLowerCase("en-US");
  if (!candidate || !Array.isArray(items)) return false;

  const excludedId = ignoreId === null || ignoreId === undefined || String(ignoreId).trim() === ""
    ? null
    : String(ignoreId);
  return items.some(item => {
    if (!item || (excludedId !== null && String(item.id ?? "") === excludedId)) return false;
    const name = String(item.name ?? "").trim();
    return name.length > 0 && name.toLocaleLowerCase("en-US") === candidate;
  });
}
