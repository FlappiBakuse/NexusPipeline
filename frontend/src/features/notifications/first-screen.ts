import { api } from "../../platform/api";
import type { NotificationTask } from "./notificationTypes";
export function loadNotificationsPage(signal?: AbortSignal) {
  return api<NotificationTask[]>("GET", "/api/notifications", undefined, signal);
}
