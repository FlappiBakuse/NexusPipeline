export async function showsHeightMotion(
  measure: readonly (() => number)[], toggle: () => void, ready: () => boolean
): Promise<boolean> {
  const start = measure.map(height => height());
  const samples = measure.map(() => new Set<number>());
  let current = start, changedAt = performance.now();
  toggle();
  const deadline = changedAt + 1500;
  while (performance.now() < deadline) {
    const now = await new Promise<number>(requestAnimationFrame);
    const next = measure.map(height => height());
    if (next.some((height, index) => Math.abs(height - current[index]!) > 0.5)) changedAt = now;
    current = next;
    if (ready() && now - changedAt >= 100 && current.every((height, index) => Math.abs(height - start[index]!) > 2))
      return samples.every((heights, index) => [...heights].some(height =>
        height > Math.min(start[index]!, current[index]!) + 1 && height < Math.max(start[index]!, current[index]!) - 1));
    current.forEach((height, index) => samples[index]!.add(height));
  }
  return false;
}

export function showsDetailsMotion(details: readonly HTMLDetailsElement[], toggle: () => void): Promise<boolean> {
  const open = details.map(detail => detail.open);
  return showsHeightMotion(details.map(detail => () => detail.getBoundingClientRect().height), toggle,
    () => details.every((detail, index) => detail.open !== open[index]));
}
