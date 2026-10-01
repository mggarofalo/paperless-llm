// npm honors Pi's published shrinkwrap over our lock/overrides for this nested
// dependency. Copy the exact integrity-locked patched package already installed
// at the root. No network, lifecycle script or unbounded dependency update.
import { readFileSync, realpathSync, rmSync, cpSync } from 'node:fs';
import { dirname, join, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(fileURLToPath(import.meta.url));
const modules = realpathSync(join(root, 'node_modules'));
const source = realpathSync(join(modules, 'brace-expansion'));
const target = realpathSync(join(modules, '@earendil-works/pi-coding-agent/node_modules/brace-expansion'));
for (const path of [source, target]) {
  if (!path.startsWith(modules + sep)) throw new Error('Unexpected dependency path');
}
const manifest = path => JSON.parse(readFileSync(join(path, 'package.json'), 'utf8'));
const sourcePackage = manifest(source);
const oldPackage = manifest(target);
if (sourcePackage.name !== 'brace-expansion' || sourcePackage.version !== '5.0.12' ||
    oldPackage.name !== 'brace-expansion' || !['5.0.9', '5.0.12'].includes(oldPackage.version))
  throw new Error('Dependency version changed; review the pinned repair');
rmSync(target, { recursive: true });
cpSync(source, target, { recursive: true, dereference: false });
if (manifest(target).version !== '5.0.12') throw new Error('Dependency repair failed');
console.log('Verified nested brace-expansion 5.0.12');
