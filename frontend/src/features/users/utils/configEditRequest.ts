export function buildConfigEditRequest(
  mode: string,
  inputOverride: { name: string; value: string } | null = null,
) {
  const request: Record<string, unknown> = { action: "start", mode };
  if (inputOverride) {
    request.configInputName = inputOverride.name;
    request.configInputValue = inputOverride.value;
  }
  return request;
}
