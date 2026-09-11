/** 构造配置编辑启动请求；候选配置通过本次会话输入覆盖传递。
 *  编辑程序出现可见窗口后，宿主据此把发起请求的浏览器窗口后置。 */
export function buildConfigEditRequest(
  mode: string,
  inputOverride: { name: string; value: string } | null = null,
  requesterWindowToken: string | null = null,
) {
  const request: Record<string, unknown> = { action: "start", mode };
  if (inputOverride) {
    request.configInputName = inputOverride.name;
    request.configInputValue = inputOverride.value;
  }
  if (requesterWindowToken) request.requesterWindowToken = requesterWindowToken;
  return request;
}
