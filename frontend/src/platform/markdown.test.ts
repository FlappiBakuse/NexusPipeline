import { describe, expect, it } from "vitest";
import { renderMarkdown } from "./markdown";

describe("plugin README markdown rendering", () => {
  it("escapes HTML and keeps only HTTPS links", () => {
    const html = renderMarkdown("# 标题\n\n**重点** `code` [官方](https://example.com) [危险](javascript:alert(1))\n\n<script>alert(1)</script>");

    expect(html).toMatch(/<h2>标题<\/h2>/);
    expect(html).toMatch(/<strong>重点<\/strong>/);
    expect(html).toMatch(/<code>code<\/code>/);
    expect(html).toMatch(/target="_blank" rel="noopener noreferrer"/);
    expect(html).not.toMatch(/<script>/);
    expect(html).not.toMatch(/href="javascript:/);
    expect(html).toMatch(/javascript:alert\(1\)/);
  });

  it("supports lists, quotes, and fenced code", () => {
    const html = renderMarkdown("- 一\n- 二\n\n> 提示\n\n```js\nconst value = 1 < 2;\n```");

    expect(html).toMatch(/<ul><li>一<\/li><li>二<\/li><\/ul>/);
    expect(html).toMatch(/<blockquote>提示<\/blockquote>/);
    expect(html).toMatch(/<pre><code>const value = 1 &lt; 2;<\/code><\/pre>/);
  });
});
