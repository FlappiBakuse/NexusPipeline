<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref, watch } from 'vue';
import { AmbientParticleRenderer, type AmbientParticle } from '../ui/ambient-particle-renderer';
const props = defineProps<{ visible: boolean }>();
const canvas = ref<HTMLCanvasElement>();
const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
const lightTheme = matchMedia('(prefers-color-scheme: light)');
let renderer: AmbientParticleRenderer | undefined;
let frame = 0;
let previous = 0;
let width = 0;
let height = 0;
let appearance: MutationObserver | undefined;
let color = '#62a0ff';
let dotAlpha = 0.12;
let lineAlpha = 0.05;
let particles: AmbientParticle[] = [];
function draw(time: number): void {
  frame = 0;
  if (!props.visible || !renderer || !canvas.value || width <= 0 || height <= 0) return;
  const elapsed = previous ? Math.min(3, (time - previous) / (1000 / 60)) : 0;
  previous = time;
  if (!reducedMotion.matches) {
    for (const point of particles) {
      point.vx += (Math.random() * 2 - 1) * point.drift * elapsed;
      point.vy += (Math.random() - 0.5) * point.drift * elapsed;
      point.x += point.vx * elapsed;
      point.y += point.vy * elapsed;
      if (point.x < -10) point.x = width + 10;
      else if (point.x > width + 10) point.x = -10;
      if (point.y < -10) point.y = height + 10;
      else if (point.y > height + 10) point.y = -10;
    }
  }
  renderer.render(particles, width, height, color, dotAlpha, lineAlpha);
  if (!reducedMotion.matches) frame = requestAnimationFrame(draw);
}
function resume(): void {
  cancelAnimationFrame(frame);
  previous = 0;
  if (props.visible) frame = requestAnimationFrame(draw);
}
function refreshAppearance(): void {
  const style = getComputedStyle(document.body);
  color = style.getPropertyValue('--accent').trim() || '#62a0ff';
  dotAlpha = Number.parseFloat(style.getPropertyValue('--particle-dot-alpha')) || 0.12;
  lineAlpha = Number.parseFloat(style.getPropertyValue('--particle-line-alpha')) || 0.05;
  resume();
}
function resizeViewport(): void {
  if (!canvas.value) return;
  width = innerWidth;
  height = innerHeight;
  const ratio = Math.min(2, devicePixelRatio || 1);
  canvas.value.width = Math.floor(width * ratio);
  canvas.value.height = Math.floor(height * ratio);
  const count = width < 640 ? 36 : width < 1000 ? 56 : 80;
  particles = Array.from({ length: count }, () => ({
    x: Math.random() * width,
    y: Math.random() * height,
    r: 1 + Math.random() * 2.6,
    vx: (Math.random() - 0.5) * 0.28,
    vy: (Math.random() - 0.5) * 0.2,
    drift: (Math.random() - 0.5) * 0.004
  }));
  canvas.value.dataset.particleCount = String(count);
  resume();
}
watch(() => props.visible, resume);
onMounted(() => {
  reducedMotion.addEventListener('change', resume);
  lightTheme.addEventListener('change', refreshAppearance);
  window.addEventListener('resize', resizeViewport);
  appearance = new MutationObserver(refreshAppearance);
  appearance.observe(document.body, { attributes: true, attributeFilter: ['style', 'data-theme'] });
  try {
    renderer = new AmbientParticleRenderer(canvas.value!);
    refreshAppearance();
    resizeViewport();
  } catch {
    canvas.value?.remove();
  }
});
onBeforeUnmount(() => {
  reducedMotion.removeEventListener('change', resume);
  lightTheme.removeEventListener('change', refreshAppearance);
  window.removeEventListener('resize', resizeViewport);
  appearance?.disconnect();
  cancelAnimationFrame(frame);
  renderer?.dispose();
});
</script>
<template><canvas ref="canvas" class="ambient-layer" aria-hidden="true" /></template>
