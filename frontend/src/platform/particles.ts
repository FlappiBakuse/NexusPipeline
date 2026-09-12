interface Particle {
  x: number;
  y: number;
  vx: number;
  vy: number;
  drift: number;
  r: number;
}

const canvas = document.querySelector<HTMLCanvasElement>("#ambient-particles");
let context: CanvasRenderingContext2D | null = null;
let particles: Particle[] = [];
let frame = 0;
let paused = false;
let reducedMotion = false;

/** 连线阈值（像素）：密度提升后同步放大连接范围。 */
const connectionDistance = 104;

function particleCount(): number {
  return window.innerWidth < 640 ? 36 : window.innerWidth < 1000 ? 56 : 80;
}

function resize(): void {
  if (!canvas) return;
  const ratio = Math.min(window.devicePixelRatio || 1, 2);
  canvas.width = Math.floor(window.innerWidth * ratio);
  canvas.height = Math.floor(window.innerHeight * ratio);
  canvas.style.width = `${window.innerWidth}px`;
  canvas.style.height = `${window.innerHeight}px`;
  context?.setTransform(ratio, 0, 0, ratio, 0, 0);
  particles = Array.from({ length: particleCount() }, () => spawn());
  if (context && (paused || reducedMotion)) drawFrame(false);
}

function spawn(): Particle {
  return {
    x: Math.random() * Math.max(window.innerWidth, 1),
    y: Math.random() * Math.max(window.innerHeight, 1),
    vx: (Math.random() - 0.5) * 0.28,
    vy: (Math.random() - 0.5) * 0.2,
    drift: (Math.random() - 0.5) * 0.004,
    r: 1 + Math.random() * 2.6,
  };
}

function color(): string {
  const value = getComputedStyle(document.body).getPropertyValue("--accent").trim();
  return value || "#62a0ff";
}

function alpha(name: string, fallback: number): number {
  const value = Number.parseFloat(getComputedStyle(document.body).getPropertyValue(name));
  return Number.isFinite(value) ? value : fallback;
}

function drawFrame(move: boolean): void {
  if (!context || paused || !canvas) return;
  const width = window.innerWidth;
  const height = window.innerHeight;
  context.clearRect(0, 0, width, height);
  const accent = color();
  const dotAlpha = alpha("--particle-dot-alpha", 0.2);
  const lineAlpha = alpha("--particle-line-alpha", 0.08);
  particles.forEach(point => {
    if (move) {
      point.vx += (Math.random() - 0.5) * point.drift * 2;
      point.vy += (Math.random() - 0.5) * point.drift;
      point.x += point.vx;
      point.y += point.vy;
      if (point.x < -10) point.x = width + 10;
      if (point.x > width + 10) point.x = -10;
      if (point.y < -10) point.y = height + 10;
      if (point.y > height + 10) point.y = -10;
    }
    context!.beginPath();
    context!.fillStyle = accent;
    context!.globalAlpha = dotAlpha;
    context!.arc(point.x, point.y, point.r, 0, Math.PI * 2);
    context!.fill();
  });
  // 连线使用双层索引循环：粒子密度提高后不再每帧创建临时数组。
  context.globalAlpha = lineAlpha;
  context.strokeStyle = accent;
  context.lineWidth = 1;
  const maxDistanceSquared = connectionDistance * connectionDistance;
  for (let index = 0; index < particles.length; index += 1) {
    const point = particles[index];
    for (let other = index + 1; other < particles.length; other += 1) {
      const candidate = particles[other];
      const dx = point.x - candidate.x;
      const dy = point.y - candidate.y;
      if (dx * dx + dy * dy > maxDistanceSquared) continue;
      context.beginPath();
      context.moveTo(point.x, point.y);
      context.lineTo(candidate.x, candidate.y);
      context.stroke();
    }
  }
  context.globalAlpha = 1;
  canvas.dataset.ready = "true";
}

function tick(): void {
  if (!context || paused) return;
  drawFrame(!reducedMotion);
  if (reducedMotion) return;
  frame = requestAnimationFrame(tick);
}

export function initParticles(): void {
  if (!canvas) return;
  context = canvas.getContext("2d");
  reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  window.matchMedia("(prefers-reduced-motion: reduce)").addEventListener?.("change", event => {
    reducedMotion = event.matches;
    if (reducedMotion && frame) cancelAnimationFrame(frame);
    if (reducedMotion) drawFrame(false);
    else if (!paused) frame = requestAnimationFrame(tick);
  });
  resize();
  window.addEventListener("resize", resize, { passive: true });
  document.addEventListener("nexus:appearance-changed", () => drawFrame(false));
  document.addEventListener("visibilitychange", () => {
    paused = document.hidden;
    if (paused && frame) cancelAnimationFrame(frame);
    if (!paused && !reducedMotion) frame = requestAnimationFrame(tick);
  });
  drawFrame(false);
  if (!reducedMotion) frame = requestAnimationFrame(tick);
}
