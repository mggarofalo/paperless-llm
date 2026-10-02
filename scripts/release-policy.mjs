import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export function stableVersion(tag) {
  const match = /^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.exec(tag);
  return match ? match.slice(1).map(BigInt) : null;
}

export function compareVersions(left, right) {
  for (let index = 0; index < 3; index++) {
    if (left[index] > right[index]) return 1;
    if (left[index] < right[index]) return -1;
  }
  return 0;
}

export function shouldPromote(candidate, publishedTags) {
  const version = stableVersion(candidate);
  if (!version) throw new Error('Candidate must be a stable vMAJOR.MINOR.PATCH tag');
  return publishedTags.map(stableVersion).filter(Boolean)
    .every(published => compareVersions(version, published) >= 0);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const tags = readFileSync(0, 'utf8').split(/\r?\n/).filter(Boolean);
  process.stdout.write(String(shouldPromote(process.argv[2], tags)) + '\n');
}
