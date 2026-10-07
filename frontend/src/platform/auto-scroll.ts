/** 滚动提示浮层显隐：输入框有值或聚焦时隐藏（原生 placeholder 无法滚动，改用浮层；:placeholder-shown 对无 placeholder 属性的输入框不可靠）。 */
function initInputHints(root: HTMLElement): void {
  root.querySelectorAll<HTMLElement>(".input-scroll").forEach(wrap => {
    const input = wrap.querySelector<HTMLInputElement>("input");
    const hint = wrap.querySelector<HTMLElement>(".input-scroll-hint");
    if (!input || !hint) return;
    const sync = () => {
      hint.hidden = input.value.length > 0 || document.activeElement === input;
    };
    input.addEventListener("input", sync);
    input.addEventListener("focus", sync);
    input.addEventListener("blur", sync);
    sync();
  });
}

/** 长文本滚动：内容溢出容器时启用往返滚动（否则保持省略号兜底）。 */
export function initAutoScroll(root: HTMLElement | null = document.querySelector<HTMLElement>("#view")): void {
  if (!root) return;
  root.querySelectorAll<HTMLElement>(".scroll-text").forEach(el => {
    const inner = el.querySelector<HTMLElement>(":scope > .scroll-inner");
    if (!inner) return;
    if (inner.scrollWidth > el.clientWidth + 1) {
      el.style.setProperty("--scroll-x", `${el.clientWidth - inner.scrollWidth}px`);
      el.classList.add("scrolling", "is-overflowing");
    } else {
      el.classList.remove("scrolling", "is-overflowing");
      el.style.removeProperty("--scroll-x");
    }
  });
  initInputHints(root);
}
