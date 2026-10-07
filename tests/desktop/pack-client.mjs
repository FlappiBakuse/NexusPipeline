import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
const [inputs,fixture,archive]=process.argv.slice(2);
const {createPackageWithOptions}=await import(pathToFileURL(path.join(inputs,'desktop/source/node_modules/@electron/asar/lib/asar.js')));
const file=path.join(fixture,'package.json'),record=JSON.parse(fs.readFileSync(file));
record.main='client-driver.cjs';fs.writeFileSync(file,JSON.stringify(record));
await createPackageWithOptions(fixture,archive,{unpack:'**/*.node'});
