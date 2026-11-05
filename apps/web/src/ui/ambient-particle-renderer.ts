export interface AmbientParticle {
  x: number;
  y: number;
  vx: number;
  vy: number;
  readonly drift: number;
  readonly r: number;
}

export class AmbientParticleRenderer {
  private readonly context: CanvasRenderingContext2D;
  private disposed = false;

  constructor(private readonly canvas: HTMLCanvasElement) {
    const context = canvas.getContext('2d', { alpha: true });
    if (!context) throw new Error('Ambient particle graphics context unavailable');
    this.context = context;
    canvas.dataset.renderer = 'canvas2d';
  }

  get available(): boolean {
    return !this.disposed && !this.context.isContextLost();
  }
  resize(): void {}

  render(
    particles: readonly AmbientParticle[],
    width: number,
    height: number,
    color: string,
    dotAlpha: number,
    lineAlpha: number
  ): boolean {
    if (!this.available) return false;
    if (
      particles.length > 256 ||
      !Number.isFinite(width) ||
      !Number.isFinite(height) ||
      width <= 0 ||
      height <= 0
    )
      throw new RangeError('Ambient particle geometry exceeds its bound');
    const context = this.context;
    context.resetTransform();
    context.clearRect(0, 0, this.canvas.width, this.canvas.height);
    context.scale(this.canvas.width / width, this.canvas.height / height);
    context.fillStyle = context.strokeStyle = color;
    context.globalAlpha = dotAlpha;
    for (const point of particles) {
      context.beginPath();
      context.arc(point.x, point.y, point.r, 0, Math.PI * 2);
      context.fill();
    }
    context.globalAlpha = lineAlpha;
    context.lineWidth = 1;
    context.lineCap = 'butt';
    for (let index = 0; index < particles.length; index++) {
      const point = particles[index]!;
      for (let other = index + 1; other < particles.length; other++) {
        const candidate = particles[other]!;
        const dx = point.x - candidate.x,
          dy = point.y - candidate.y;
        const squared = dx * dx + dy * dy;
        if (squared > 104 * 104) continue;
        context.beginPath();
        context.moveTo(point.x, point.y);
        context.lineTo(candidate.x, candidate.y);
        context.stroke();
      }
    }
    return true;
  }

  dispose(): void {
    this.disposed = true;
  }
}
