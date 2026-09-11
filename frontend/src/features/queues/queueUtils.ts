export function durationClock(totalSeconds: number): string {
  const seconds = Math.max(0, Math.floor(Number(totalSeconds) || 0));
  const hours = String(Math.floor(seconds / 3600)).padStart(2, "0");
  const minutes = String(Math.floor((seconds % 3600) / 60)).padStart(2, "0");
  const remainder = String(seconds % 60).padStart(2, "0");
  return `${hours}:${minutes}:${remainder}`;
}

export type QueueCountdown =
  | { kind: "waiting" }
  | { kind: "about" }
  | { kind: "countdown"; duration: string };

export function queueCountdown(nextTrigger: string | undefined, now = Date.now()): QueueCountdown {
  const target = nextTrigger ? new Date(nextTrigger).getTime() : Number.NaN;
  if (!Number.isFinite(target)) return { kind: "waiting" };
  const remaining = target - now;
  if (remaining <= 0) return { kind: "about" };
  return { kind: "countdown", duration: durationClock(Math.floor(remaining / 1000)) };
}
