<script setup lang="ts">
type CollapseState = {
  timer: number;
  onEnd: (event: TransitionEvent) => void;
};

const activeTransitions = new WeakMap<HTMLElement, CollapseState>();

function clearStyles(element: Element) {
  const node = element as HTMLElement;
  node.style.maxHeight = "";
  node.style.opacity = "";
  node.style.transform = "";
}

function transitionDurationMs(element: HTMLElement) {
  const raw = getComputedStyle(element).transitionDuration.split(",")[0]?.trim() || "0s";
  const value = Number.parseFloat(raw) || 0;
  return raw.endsWith("ms") ? value : value * 1000;
}

function finish(element: HTMLElement, done: () => void) {
  const state = activeTransitions.get(element);
  if (!state) {
    done();
    return;
  }
  activeTransitions.delete(element);
  window.clearTimeout(state.timer);
  element.removeEventListener("transitionend", state.onEnd);
  done();
}

function begin(element: HTMLElement, done: () => void) {
  const previous = activeTransitions.get(element);
  if (previous) {
    window.clearTimeout(previous.timer);
    element.removeEventListener("transitionend", previous.onEnd);
  }
  const onEnd = (event: TransitionEvent) => {
    if (event.target === element && event.propertyName === "max-height") {
      finish(element, done);
    }
  };
  const state: CollapseState = {
    onEnd,
    timer: window.setTimeout(
      () => finish(element, done),
      Math.max(80, transitionDurationMs(element) + 80),
    ),
  };
  activeTransitions.set(element, state);
  element.addEventListener("transitionend", onEnd);
}

function beforeEnter(element: Element) {
  const node = element as HTMLElement;
  node.style.maxHeight = "0px";
  node.style.opacity = "0";
  node.style.transform = "translateY(-4px)";
}

function enter(element: Element, done: () => void) {
  const node = element as HTMLElement;
  begin(node, done);
  void node.offsetHeight;
  node.style.maxHeight = `${node.scrollHeight}px`;
  node.style.opacity = "1";
  node.style.transform = "translateY(0)";
}

function beforeLeave(element: Element) {
  const node = element as HTMLElement;
  node.style.maxHeight = `${node.scrollHeight}px`;
  node.style.opacity = "1";
  node.style.transform = "translateY(0)";
}

function leave(element: Element, done: () => void) {
  const node = element as HTMLElement;
  begin(node, done);
  void node.offsetHeight;
  node.style.maxHeight = "0px";
  node.style.opacity = "0";
  node.style.transform = "translateY(-4px)";
}

function cancel(element: Element) {
  const node = element as HTMLElement;
  const state = activeTransitions.get(node);
  if (state) {
    window.clearTimeout(state.timer);
    node.removeEventListener("transitionend", state.onEnd);
    activeTransitions.delete(node);
  }
  clearStyles(node);
}
</script>

<template>
  <Transition
    name="nxp-collapse"
    @before-enter="beforeEnter"
    @enter="enter"
    @after-enter="clearStyles"
    @enter-cancelled="cancel"
    @before-leave="beforeLeave"
    @leave="leave"
    @after-leave="clearStyles"
    @leave-cancelled="cancel"
  >
    <slot />
  </Transition>
</template>
