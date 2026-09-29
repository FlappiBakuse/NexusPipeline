import { performance } from "node:perf_hooks";

export class BudgetExceeded extends Error {
  constructor(label) {
    super(`预算耗尽：${label}`);
    this.exitCode = 5;
  }
}

export class Budget {
  constructor(label, limitMs, { parent = null, reserveMs = 0, qualificationMs = limitMs,
    clock = () => performance.now() } = {}) {
    if (!Number.isFinite(limitMs) || limitMs <= 0 || !Number.isFinite(reserveMs)
      || reserveMs < 0 || reserveMs >= limitMs || !Number.isFinite(qualificationMs)
      || qualificationMs <= 0 || qualificationMs > limitMs) throw new TypeError("Invalid budget");
    this.label = label;
    this.clock = parent?.clock ?? clock;
    this.started = this.clock();
    this.startedMonotonic = this.started;
    this.deadline = Math.min(this.started + limitMs, parent?.workDeadline ?? Infinity);
    this.cleanupDeadline = this.deadline;
    this.qualificationDeadline = Math.min(this.started + qualificationMs, this.deadline);
    this.workDeadline = Math.min(this.qualificationDeadline, this.deadline - reserveMs);
    this.limitMs = limitMs;
  }

  get elapsedMs() { return Math.max(0, this.clock() - this.started); }
  remainingMs({ cleanup = false } = {}) {
    return Math.max(0, (cleanup ? this.deadline : this.workDeadline) - this.clock());
  }
  check() {
    if (this.remainingMs() <= 0) throw new BudgetExceeded(this.label);
  }
  assertWithinBudget() {
    if (this.clock() > this.qualificationDeadline || this.clock() > this.deadline)
      throw new BudgetExceeded(this.label);
  }
  child(label, limitMs, reserveMs = 0) {
    this.check();
    return new Budget(label, limitMs, { parent: this, reserveMs });
  }
}
