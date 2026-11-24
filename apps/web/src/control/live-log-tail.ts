const maximumLines = 500;
const maximumUnits = 256 * 1024;

export class LiveLogTail {
  text = '';
  omitted = false;
  private breaks: number[] = [];
  private firstBreak = 0;
  private offset = 0;

  append(chunk: string): void {
    if (!chunk) return;
    const chunkOffset = this.offset + this.text.length;
    for (let index = chunk.indexOf('\n'); index >= 0; index = chunk.indexOf('\n', index + 1))
      this.breaks.push(chunkOffset + index);

    const joined = this.text + chunk;
    const excessBreaks = this.breaks.length - this.firstBreak - (maximumLines - 1);
    const lineStart = excessBreaks > 0 ? this.breaks[this.firstBreak + excessBreaks - 1]! + 1 : this.offset;
    let trim = Math.max(lineStart - this.offset, joined.length - maximumUnits);
    if (trim > 0) {
      const first = joined.charCodeAt(trim);
      const previous = joined.charCodeAt(trim - 1);
      if (first >= 0xdc00 && first <= 0xdfff && previous >= 0xd800 && previous <= 0xdbff) trim++;
      this.offset += trim;
      this.omitted = true;
      while (this.firstBreak < this.breaks.length && this.breaks[this.firstBreak]! < this.offset)
        this.firstBreak++;
      if (this.firstBreak >= maximumLines) {
        this.breaks = this.breaks.slice(this.firstBreak);
        this.firstBreak = 0;
      }
    }
    this.text = joined.slice(trim);
  }
}
