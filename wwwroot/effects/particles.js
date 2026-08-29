const canvas = document.querySelector("#ambient-particles");
let context = null;
let particles = [];
let frame = 0;
let paused = false;
let reducedMotion = false;

function resize() {
  if (!canvas) return;
  const ratio = Math.min(window.devicePixelRatio || 1, 2);
  canvas.width = Math.floor(window.innerWidth * ratio);
  canvas.height = Math.floor(window.innerHeight * ratio);
  canvas.style.width = `${window.innerWidth}px`;
  canvas.style.height = `${window.innerHeight}px`;
  context?.setTransform(ratio, 0, 0, ratio, 0, 0);
  const count = window.innerWidth < 640 ? 24 : window.innerWidth < 1000 ? 36 : 48;
  particles = Array.from({ length: count }, () => spawn());
  if (context && (paused || reducedMotion)) drawFrame(false);
}

function spawn() {
  return {
    x: Math.random() * Math.max(window.innerWidth, 1),
    y: Math.random() * Math.max(window.innerHeight, 1),
    vx: (Math.random() - 0.5) * 0.28,
    vy: (Math.random() - 0.5) * 0.2,
    drift: (Math.random() - 0.5) * 0.004,
    r: 1 + Math.random() * 2.6,
  };
}

function color() {
  const value = getComputedStyle(document.body).getPropertyValue("--accent").trim();
  return value || "#62a0ff";
}

function alpha(name, fallback) {
  const value = Number.parseFloat(getComputedStyle(document.body).getPropertyValue(name));
  return Number.isFinite(value) ? value : fallback;
}

function drawFrame(move) {
  if (!context || paused) return;
  const width = window.innerWidth;
  const height = window.innerHeight;
  context.clearRect(0, 0, width, height);
  const accent = color();
  const dotAlpha = alpha("--particle-dot-alpha", 0.12);
  const lineAlpha = alpha("--particle-line-alpha", 0.05);
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
    context.beginPath();
    context.fillStyle = accent;
    context.globalAlpha = dotAlpha;
    context.arc(point.x, point.y, point.r, 0, Math.PI * 2);
    context.fill();
  });
  context.globalAlpha = lineAlpha;
  particles.forEach((point, index) => {
    particles.slice(index + 1).forEach(other => {
      const dx = point.x - other.x;
      const dy = point.y - other.y;
      const distance = Math.sqrt(dx * dx + dy * dy);
      if (distance > 90) return;
      context.beginPath();
      context.strokeStyle = accent;
      context.lineWidth = 1;
      context.moveTo(point.x, point.y);
      context.lineTo(other.x, other.y);
      context.stroke();
    });
  });
  context.globalAlpha = 1;
  canvas.dataset.ready = "true";
}

function tick() {
  if (!context || paused) return;
  drawFrame(!reducedMotion);
  if (reducedMotion) return;
  frame = requestAnimationFrame(tick);
}

export function initParticles() {
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
