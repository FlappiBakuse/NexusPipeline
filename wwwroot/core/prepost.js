/** 任务前/后脚本路径的 %FIRST% / %LAST% 前缀语义：前缀 + 空格表示仅首次（或仅最终）运行。 */

export const PRE_ONLY_MARKER = "%FIRST%";
export const POST_FINAL_MARKER = "%LAST%";

/** 组合输入框显示值：onceOnly 为 true 时在路径前加标记前缀。 */
export function encodePrePost(marker, onceOnly, value) {
  return (onceOnly ? marker + " " : "") + (value || "");
}

/** 拆分输入框显示值：返回剥离前缀后的路径与 onceOnly 标志。 */
export function splitPrePost(marker, raw) {
  const text = (raw || "").trim();
  return {
    onceOnly: text.startsWith(marker),
    value: text.replace(new RegExp("^" + marker + "\\s*"), ""),
  };
}
