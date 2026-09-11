import { ref } from "vue";

/** 用户列表倒计时到期后的单次后端刷新。
 *
 *  倒计时不只是显示：当某个 `nextRunAt` 到期后，计划可能已经执行或状态已经变化，
 *  页面需要在短暂延迟后重新拉取后端状态，而不是继续显示旧时间。
 *  为避免后端短时间内仍返回同一过期时间戳时形成无界请求循环，按
 *  `userId + nextRunAt` 签名记录已处理项；`nextRunAt` 更新后允许再次进入生命周期。
 */

export interface CountdownUserLike {
  id: string;
  nextRunAt?: string;
}

export interface CountdownRefreshOptions {
  /** 到期后触发的后端刷新。 */
  onRefresh: () => void | Promise<void>;
  /** 到期到刷新之间的延迟，默认 1500ms。 */
  delayMs?: number;
  /** 可注入时钟，便于测试。 */
  now?: () => number;
}

function dueSignature(user: CountdownUserLike) {
  return `${user.id}\u0000${user.nextRunAt ?? ""}`;
}

function isDue(user: CountdownUserLike, now: number) {
  const target = Date.parse(String(user.nextRunAt ?? ""));
  return Number.isFinite(target) && target <= now;
}

export function useCountdownRefresh(options: CountdownRefreshOptions) {
  const delayMs = options.delayMs ?? 1500;
  const now = options.now ?? (() => Date.now());
  const pending = ref(false);
  let timer: ReturnType<typeof setTimeout> | null = null;
  let disposed = false;
  const handled = new Set<string>();

  /** 在每次倒计时 tick 时调用：同一批到期用户只安排一次刷新。 */
  function tick(users: readonly CountdownUserLike[]) {
    if (disposed || pending.value) return;
    // 仅保留当前仍在列表中的签名，避免已消失用户长期占用记录。
    const live = new Set(users.map(dueSignature));
    for (const signature of [...handled]) if (!live.has(signature)) handled.delete(signature);

    const timestamp = now();
    const expired = users.filter(user => isDue(user, timestamp));
    const fresh = expired.filter(user => !handled.has(dueSignature(user)));
    if (!fresh.length) return;
    for (const user of expired) handled.add(dueSignature(user));

    pending.value = true;
    timer = setTimeout(async () => {
      timer = null;
      try {
        await options.onRefresh();
      } finally {
        pending.value = false;
      }
    }, delayMs);
  }

  /** 页面卸载时清理在途定时器。 */
  function dispose() {
    disposed = true;
    if (timer) clearTimeout(timer);
    timer = null;
    pending.value = false;
  }

  return { pending, tick, dispose };
}
