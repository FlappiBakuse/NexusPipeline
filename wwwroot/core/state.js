export const state = {
  page: "dashboard",
  routeToken: 0,
  scripts: [],
  queues: [],
  plugins: [],
  settings: null,
  timers: new Set(),
  controllers: new Set(),
};

export function enterPage(page) {
  disposePage();
  state.page = page;
  state.routeToken += 1;
  return state.routeToken;
}

export function isCurrent(page, token) {
  return state.page === page && state.routeToken === token;
}

export function schedule(callback, delay, page = state.page, token = state.routeToken) {
  const timer = setTimeout(() => {
    state.timers.delete(timer);
    if (isCurrent(page, token)) callback();
  }, delay);
  state.timers.add(timer);
  return timer;
}

export function registerInterval(interval) {
  state.timers.add(interval);
  return interval;
}

export function trackController(controller) {
  state.controllers.add(controller);
  return controller;
}

export function releaseController(controller) {
  state.controllers.delete(controller);
}

export function disposePage() {
  state.timers.forEach(timer => {
    clearTimeout(timer);
    clearInterval(timer);
  });
  state.timers.clear();
  state.controllers.forEach(controller => controller.abort());
  state.controllers.clear();
}
