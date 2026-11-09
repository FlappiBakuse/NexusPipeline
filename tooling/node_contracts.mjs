import fs from 'node:fs';
import path from 'node:path';
import { run } from 'node:test';

export async function runNodeContract({ root, modules, directory, suite, entry, captureInput, cases }) {
  for (const folder of ['apps/web/src', 'apps/desktop']) {
    const base = path.join(root, folder);
    for (const item of fs.readdirSync(base, { recursive: true, withFileTypes: true })) {
      if (item.isFile()) captureInput(path.join(item.parentPath, item.name));
    }
  }
  const file = path.join(root, entry); captureInput(file);
  for (const relative of ['apps/web/package-lock.json', 'tooling/node_contracts.mjs', 'tooling/web_contracts.mjs']) {
    captureInput(path.join(root, relative));
  }
  const temporary = path.join(directory, 'tmp'); fs.mkdirSync(temporary);
  const events = [], first = cases.length;
  let summary, fileSummary;
  try {
    const stream = run({ files: [file], isolation: 'process', timeout: 30000,
      signal: AbortSignal.timeout(60000), env: { ...process.env, TEMP: temporary, TMP: temporary,
        NEXUS_FRONTEND_TYPESCRIPT: path.join(modules, 'typescript'),
        NEXUS_FRONTEND_SOURCE: path.join(root, 'apps/web/src/control') } });
    for await (const event of stream) {
      events.push(event);
      if (event.type === 'test:summary') {
        if (event.data.file) fileSummary = event.data; else summary = event.data;
      }
      if (event.type === 'test:pass' || event.type === 'test:fail') {
        const data = event.data;
        cases.push({ suite, name: data.name, status: data.skip || data.todo ? 'SKIP' :
          event.type === 'test:pass' ? 'PASS' : 'FAIL', elapsedMilliseconds: data.details?.duration_ms });
      }
    }
    if (!fileSummary?.success || !fileSummary.counts.tests || !summary?.success || !summary.counts.tests || summary.counts.failed || summary.counts.cancelled ||
        summary.counts.skipped || summary.counts.todo || cases.length === first ||
        cases.slice(first).some(item => item.status !== 'PASS')) throw new Error('Node contract incomplete or failed: ' + suite);
  } finally {
    fs.writeFileSync(path.join(directory, 'events.json'), JSON.stringify(events,
      (_key, value) => value instanceof Error ? { name: value.name, message: value.message, stack: value.stack,
        cause: value.cause, code: value.code } : value, 2) + '\n');
  }
}
