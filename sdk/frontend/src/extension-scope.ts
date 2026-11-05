export type Cleanup = () => void | Promise<void>;

/** Owns surface resources; callers must await registration and disposal failures. */
export class ExtensionScope {
  private readonly controller = new AbortController();
  private readonly resources: Cleanup[] = [];
  private isClosed = false;
  private disposal: Promise<void> | undefined;

  get signal(): AbortSignal {
    return this.controller.signal;
  }

  async child(): Promise<ExtensionScope> {
    const child = new ExtensionScope();
    const release = (): Promise<void> => child.dispose();
    await child.own(() => {
      const index = this.resources.indexOf(release);
      if (index >= 0) this.resources.splice(index, 1);
    });
    await this.own(release);
    return child;
  }

  /** A registration completed after disposal is cleaned before this promise resolves. */
  async own(cleanup: Cleanup): Promise<void> {
    if (this.isClosed) {
      await cleanup();
      return;
    }
    this.resources.push(cleanup);
  }

  dispose(): Promise<void> {
    if (this.disposal) return this.disposal;
    this.isClosed = true;
    this.disposal = Promise.resolve().then(() => this.release());
    this.controller.abort();
    return this.disposal;
  }

  private async release(): Promise<void> {
    const errors: unknown[] = [];
    const resources = this.resources.splice(0).reverse();
    for (const cleanup of resources) {
      try {
        await cleanup();
      } catch (error) {
        errors.push(error);
      }
    }
    if (errors.length) throw new AggregateError(errors, 'Extension cleanup failed');
  }
}
