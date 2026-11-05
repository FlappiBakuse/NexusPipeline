'use strict';
function strictJson(text) {
  if (typeof text !== 'string' || Buffer.byteLength(text, 'utf8') > 32768) throw new Error('desktop_json_limit');
  const parsed = JSON.parse(text);
  let cursor = 0;
  const whitespace = () => { while (/\s/.test(text[cursor] ?? '') && cursor < text.length) cursor++; };
  function string() {
    const start = cursor++;
    while (cursor < text.length) {
      const character = text[cursor++];
      if (character === '\\') cursor++;
      else if (character === '"') return JSON.parse(text.slice(start, cursor));
    }
    throw new Error('desktop_json_string');
  }
  function value(depth) {
    if (depth > 64) throw new Error('desktop_json_depth');
    whitespace();
    const opening = text[cursor];
    if (opening === '{' || opening === '[') {
      cursor++;
      whitespace();
      const closing = opening === '{' ? '}' : ']';
      const keys = new Set();
      if (text[cursor] === closing) { cursor++; return; }
      while (cursor < text.length) {
        whitespace();
        if (opening === '{') {
          const key = string();
          if (keys.has(key)) throw new Error('desktop_json_duplicate');
          keys.add(key);
          whitespace();
          cursor++;
        }
        value(depth + 1);
        whitespace();
        if (text[cursor++] === closing) return;
      }
    } else if (opening === '"') string();
    else {
      const start = cursor;
      while (cursor < text.length && !/[\s,}\]]/.test(text[cursor])) cursor++;
      const scalar = JSON.parse(text.slice(start, cursor));
      if (typeof scalar === 'number' && !Number.isFinite(scalar)) throw new Error('desktop_json_number');
    }
  }
  value(0);
  return parsed;
}
module.exports = { strictJson };
