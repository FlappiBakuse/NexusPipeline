import { trackController, releaseController } from "./state.js";

export async function api(method, path, body, signal) {
  const controller = signal ? null : trackController(new AbortController());
  const options = { method, headers: {}, signal: signal || controller.signal };
  try {
    if (body !== undefined) {
      options.headers["Content-Type"] = "application/json";
      options.body = JSON.stringify(body);
    }
    const response = await fetch(path, options);
    if (response.status === 204) return null;
    const data = await response.json().catch(() => null);
    if (!response.ok) {
      throw new Error((data && data.error) || ("HTTP " + response.status));
    }
    return data;
  } finally {
    if (controller) releaseController(controller);
  }
}
