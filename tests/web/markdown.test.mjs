import test from "node:test";
import assert from "node:assert/strict";
import { renderMarkdown } from "../../wwwroot/core/markdown.js";

test("renderMarkdown escapes HTML and keeps only HTTPS links", () => {
  const html = renderMarkdown("# 标题\n\n**重点** `code` [官方](https://example.com) [危险](javascript:alert(1))\n\n<script>alert(1)</script>");

  assert.match(html, /<h2>标题<\/h2>/);
  assert.match(html, /<strong>重点<\/strong>/);
  assert.match(html, /<code>code<\/code>/);
  assert.match(html, /target="_blank" rel="noopener noreferrer"/);
  assert.doesNotMatch(html, /<script>/);
  assert.doesNotMatch(html, /href="javascript:/);
  assert.match(html, /javascript:alert\(1\)/);
});

test("renderMarkdown supports lists, quotes, and fenced code", () => {
  const html = renderMarkdown("- 一\n- 二\n\n> 提示\n\n```js\nconst value = 1 < 2;\n```");

  assert.match(html, /<ul><li>一<\/li><li>二<\/li><\/ul>/);
  assert.match(html, /<blockquote>提示<\/blockquote>/);
  assert.match(html, /<pre><code>const value = 1 &lt; 2;<\/code><\/pre>/);
});
