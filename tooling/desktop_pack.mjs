import path from 'node:path';
import { pathToFileURL } from 'node:url';

const [modules, source, output, ...extra] = process.argv.slice(2);
if (extra.length || ![modules, source, output].every(value => value && path.isAbsolute(value)))
  throw new Error('Explicit ASAR modules, source and output paths required');
const { createPackage } = await import(pathToFileURL(path.join(modules, '@electron/asar/lib/asar.js')));
await createPackage(source, output);
