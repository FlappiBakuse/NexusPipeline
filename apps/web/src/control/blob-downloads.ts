export class BlobDownloads {
  private readonly urls = new Map<string, ReturnType<typeof setTimeout>>();

  save(blob: Blob, filename: string): void {
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = filename;
    anchor.click();
    this.urls.set(
      url,
      setTimeout(() => {
        if (this.urls.delete(url)) URL.revokeObjectURL(url);
      }, 1000)
    );
  }

  release(): void {
    for (const [url, timer] of this.urls) {
      clearTimeout(timer);
      URL.revokeObjectURL(url);
    }
    this.urls.clear();
  }
}
