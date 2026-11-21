export async function showsDetailsMotion(details: readonly HTMLDetailsElement[], toggle: () => void): Promise<boolean> {
  const start = details.map(detail => detail.offsetHeight);
  const heights = details.map(() => new Set<number>());
  if (details.some(detail => getComputedStyle(detail, '::details-content').transitionDuration.split(',')[0] !== '0.22s'))
    return false;
  toggle();
  const deadline = performance.now() + 1200;
  while (performance.now() < deadline) {
    await new Promise(requestAnimationFrame);
    const current = details.map(detail => detail.offsetHeight);
    if (details.every(detail => Number(getComputedStyle(detail, '::details-content').opacity) === Number(detail.open))) {
      return heights.every((samples, index) => [...samples].some(height =>
        height > Math.min(start[index]!, current[index]!) && height < Math.max(start[index]!, current[index]!)));
    }
    current.forEach((height, index) => heights[index]!.add(height));
  }
  return false;
}
