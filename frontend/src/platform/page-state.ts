export interface PageState {
  page: string;
  routeToken: number;
  scripts: unknown[];
  queues: unknown[];
  plugins: unknown[];
  settings: unknown;
  limits?: unknown;
  limitsWarnings?: string[];
  timers: Set<ReturnType<typeof setTimeout>>;
  controllers: Set<AbortController>;
}

export const state: PageState = {
  page: "dashboard",
  routeToken: 0,
  scripts: [],
  queues: [],
  plugins: [],
  settings: null,
  timers: new Set(),
  controllers: new Set(),
};

export function enterPage(page: string): number {
  disposePage();
  state.page = page;
  state.routeToken += 1;
  return state.routeToken;
}

export function isCurrent(page: string, token: number): boolean {
  return state.page === page && state.routeToken === token;
}

export function schedule(callback: () => void, delay: number, page: string = state.page, token: number = state.routeToken): ReturnType<typeof setTimeout> {
  const timer = setTimeout(() => {
    state.timers.delete(timer);
    if (isCurrent(page, token)) callback();
  }, delay);
  state.timers.add(timer);
  return timer;
}

export function registerInterval<T extends ReturnType<typeof setInterval>>(interval: T): T {
  state.timers.add(interval);
  return interval;
}

export function trackController(controller: AbortController): AbortController {
  state.controllers.add(controller);
  return controller;
}

export function releaseController(controller: AbortController): void {
  state.controllers.delete(controller);
}

export function disposePage(): void {
  state.timers.forEach(timer => {
    clearTimeout(timer);
    clearInterval(timer);
  });
  state.timers.clear();
  state.controllers.forEach(controller => controller.abort());
  state.controllers.clear();
}
