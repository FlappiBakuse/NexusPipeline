import test from "node:test";
import assert from "node:assert/strict";
import { esc } from "../../wwwroot/core/format.js";

test("esc escapes all HTML-sensitive characters", () => {
  assert.equal(esc(`<tag a=\"1\">it's & ok</tag>`), "&lt;tag a=&quot;1&quot;&gt;it&#39;s &amp; ok&lt;/tag&gt;");
  assert.equal(esc(null), "");
  assert.equal(esc(undefined), "");
});
