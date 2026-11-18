'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vectors = require('../fixtures/browser-login-values.json');
const { isReady } = require('../../apps/desktop/login-values.cjs');

function fixture(row) {
  const json = ['jsonDepth', 'arrayDepth', 'jsonText', 'literalJson'].includes(row.form);
  let text = row.text ?? '';
  if (row.form === 'jsonDepth' || row.form === 'arrayDepth') {
    const object = row.form === 'jsonDepth';
    text = '{"id":"value","extra":' + (object ? '{"x":' : '[').repeat(row.depth - 1) +
      '0' + (object ? '}' : ']').repeat(row.depth - 1) + '}';
    if (row.pad) text = text.padEnd(row.pad, ' ');
  } else if (row.form === 'jsonText') text = '{"id":"' + row.character.repeat(row.repeat) + '"}';
  else if (row.form === 'raw') text = row.character.repeat(row.repeat);
  const policy = { cookies: [], jsonRequests: [],
    storage: ['one', 'two', 'alternative'].map(id => ({ id, origin: 'https://login.example.test', storage: 'localStorage', key: id })),
    checks: [{ id: 'one', kind: row.form === 'token' ? 'token' : 'nonEmpty', path: json ? ['id'] : [], maximumLength: 32768 }],
    readySets: ['equal', 'missing', 'alternative'].includes(row.form) ? [['one', 'two'], ['alternative']] : [['one']],
    equalSets: [['one', 'two']] };
  const values = row.form === 'alternative' ? { alternative: 'ready' } : { one: text };
  if (row.form === 'equal') values.two = row.other;
  return { policy, values };
}

for (const row of vectors) test('shared native login value: ' + row.name, () => {
  const { policy, values } = fixture(row);
  assert.equal(isReady(policy, values), row.expected);
});
