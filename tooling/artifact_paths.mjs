import fs from 'node:fs';
import path from 'node:path';

export function ordinaryPath(value) {
  if (typeof value !== 'string' || !path.isAbsolute(value)) throw new Error('An explicit absolute path is required');
  const requested = path.resolve(value);
  for (let current = requested; ; current = path.dirname(current)) {
    if (fs.lstatSync(current, { throwIfNoEntry: false })?.isSymbolicLink()) throw new Error('Paths cannot traverse a link or reparse point');
    if (current === path.dirname(current)) break;
  }
  const suffix = [];
  let ancestor = requested;
  while (!fs.existsSync(ancestor)) {
    suffix.unshift(path.basename(ancestor));
    const parent = path.dirname(ancestor);
    if (parent === ancestor) throw new Error('Path has no existing filesystem root');
    ancestor = parent;
  }
  return path.join(fs.realpathSync.native(ancestor), ...suffix);
}

export function disjointDirectories(root, output) {
  const source = ordinaryPath(root), target = ordinaryPath(output);
  const contains = (parent, child) => {
    const relative = path.relative(parent, child);
    return relative === '' || relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative);
  };
  if (contains(source, target) || contains(target, source)) throw new Error('Output must be outside and disjoint from the source tree');
  return target;
}

export function externalDirectory(root, output) {
  if (!fs.statSync(ordinaryPath(root)).isDirectory()) throw new Error('Source root must be a directory');
  return disjointDirectories(root, output);
}
