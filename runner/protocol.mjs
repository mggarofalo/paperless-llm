// Harness-neutral NDJSON protocol shared by every bridge. .NET only understands these messages.
export const emit = value => process.stdout.write(JSON.stringify(value) + '\n');

export function emitError(code) {
  emit({ type: 'error', code });
  process.exitCode = code === 'rate_limited' ? 22 : code === 'auth_required' ? 20 : 21;
}

export async function readRequest() {
  const chunks = [];
  let size = 0;
  for await (const chunk of process.stdin) {
    size += chunk.length;
    if (size > 48 * 1024 * 1024) throw new Error('input_limit');
    chunks.push(chunk);
  }
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

export function parseImage(url) {
  const match = /^data:(image\/(?:png|jpeg));base64,([A-Za-z0-9+/=]+)$/.exec(url);
  if (!match) throw new Error('image_invalid');
  return { mimeType: match[1], data: match[2] };
}

// Provider usage objects can carry extra fields; forward only validated token counts.
export function safeUsage(input, output, cacheRead, cacheWrite) {
  const usage = { input, output, cacheRead, cacheWrite };
  if (!Object.values(usage).every(value => Number.isSafeInteger(value) && value >= 0)) return {};
  return { usage };
}

export function dispatch(commands, name) {
  if (!Object.hasOwn(commands, name)) throw new Error('unknown_command');
  return commands[name]();
}
