/** 构造配置编辑启动请求；候选配置通过本次会话输入覆盖传递。 */
export function buildConfigEditRequest(mode, inputOverride = null, requesterWindowToken = null) {
  const request = { action: "start", mode };
  if (inputOverride) {
    request.configInputName = inputOverride.name;
    request.configInputValue = inputOverride.value;
  }
  if (requesterWindowToken) {
    request.requesterWindowToken = requesterWindowToken;
  }
  return request;
}
