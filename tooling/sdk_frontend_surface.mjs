/** Read the emitted declarations with the repository's locked TypeScript compiler. */
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';

const [compilerInput, declarationsInput, mode, destinationInput] = process.argv.slice(2);
if (!compilerInput || !declarationsInput || !destinationInput || !['--write', '--verify'].includes(mode))
  throw new Error('usage: sdk_frontend_surface <locked-typescript.js> <dist-root> <--write|--verify> <descriptor>');
const compilerPath = path.resolve(compilerInput), declarationsRoot = path.resolve(declarationsInput);
const destination = path.resolve(destinationInput);
const ts = createRequire(import.meta.url)(compilerPath);
if (ts.version !== '5.9.3') throw new Error('sdk_surface_compiler_version_unbound');
const root = fs.realpathSync(declarationsRoot);
const pending = ['index.d.ts'], visited = new Set(), declarations = [];
const printer = ts.createPrinter({ removeComments: true, newLine: ts.NewLineKind.LineFeed });
let bytes = 0;
while (pending.length) {
  const name = pending.pop();
  if (visited.has(name)) continue;
  visited.add(name);
  if (visited.size > 64 || !/^[a-z0-9.-]+\.d\.ts$/.test(name)) throw new Error('sdk_surface_declaration_limit');
  const sourcePath = path.join(root, name);
  if (fs.realpathSync(sourcePath) !== sourcePath || fs.lstatSync(sourcePath).isSymbolicLink())
    throw new Error('sdk_surface_declaration_unbound');
  const text = fs.readFileSync(sourcePath, 'utf8');
  bytes += Buffer.byteLength(text);
  if (bytes > 2097152) throw new Error('sdk_surface_declaration_limit');
  const source = ts.createSourceFile(name, text, ts.ScriptTarget.ES2023, true, ts.ScriptKind.TS);
  if (source.parseDiagnostics.length) throw new Error('sdk_surface_declaration_invalid');
  for (const statement of source.statements) {
    if ((ts.isExportDeclaration(statement) || ts.isImportDeclaration(statement)) && statement.moduleSpecifier) {
      const module = statement.moduleSpecifier;
      if (!ts.isStringLiteral(module) || !/^\.\/[a-z0-9.-]+\.js$/.test(module.text))
        throw new Error('sdk_surface_unreviewed_module');
      pending.push(module.text.slice(2, -3) + '.d.ts');
    }
  }
  const transformed = ts.transform(source, [context => tree => ts.visitNode(tree, function visit(node) {
    if (name === 'sdk-identity.generated.d.ts' && ts.isVariableDeclaration(node) &&
        ts.isIdentifier(node.name) && node.name.text === 'contractFingerprint')
      return ts.factory.updateVariableDeclaration(node, node.name, node.exclamationToken,
        ts.factory.createKeywordTypeNode(ts.SyntaxKind.StringKeyword), undefined);
    if (ts.isStringLiteral(node)) return ts.factory.createStringLiteral(node.text);
    if (ts.isNumericLiteral(node)) return ts.factory.createNumericLiteral(Number(node.text));
    const value = ts.visitEachChild(node, visit, context);
    if ((ts.isInterfaceDeclaration(value) || ts.isTypeLiteralNode(value)) &&
        value.members.every(member => ts.isPropertySignature(member))) {
      const keys = value.members.map(member => printer.printNode(ts.EmitHint.Unspecified, member.name, source));
      if (new Set(keys).size === keys.length) {
        const members = [...value.members].sort((left, right) => {
          const a = printer.printNode(ts.EmitHint.Unspecified, left.name, source);
          const b = printer.printNode(ts.EmitHint.Unspecified, right.name, source);
          return a < b ? -1 : a > b ? 1 : 0;
        });
        return ts.isInterfaceDeclaration(value) ? ts.factory.updateInterfaceDeclaration(value, value.modifiers,
          value.name, value.typeParameters, value.heritageClauses, members) : ts.factory.updateTypeLiteralNode(value, members);
      }
    }
    return value;
  })]);
  try { declarations.push({ path: name, text: printer.printFile(transformed.transformed[0]) }); }
  finally { transformed.dispose(); }
}
declarations.sort((left, right) => left.path < right.path ? -1 : left.path > right.path ? 1 : 0);
const value = { format: 'nexus.sdk.frontend-public-api', version: 1, assembly: '@nexuspipeline/extension-sdk', declarations };
const output = JSON.stringify(value) + '\n';
if (mode === '--write') fs.writeFileSync(destination, output, { encoding: 'utf8' });
else if (JSON.stringify(JSON.parse(fs.readFileSync(destination, 'utf8'))) !== JSON.stringify(value))
  throw new Error('sdk_frontend_api_descriptor_changed');
