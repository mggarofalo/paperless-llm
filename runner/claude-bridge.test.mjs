import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, copyFile, writeFile, readFile, rm, chmod } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn } from 'node:child_process';

// The fake stands in for the native Claude Code binary. It records each call and replays a scenario.
const fakeClaude = `#!/usr/bin/env node
const { appendFileSync, readFileSync, writeFileSync } = require('node:fs');
(async () => {
  const home = process.env.CLAUDE_CONFIG_DIR;
  const scenario = JSON.parse(readFileSync(home + '/scenario.json', 'utf8'));
  const args = process.argv.slice(2);
  let input = '';
  if (args[0] === '-p') for await (const chunk of process.stdin) input += chunk;
  const file = args.includes('--system-prompt-file') ? args[args.indexOf('--system-prompt-file') + 1] : null;
  appendFileSync(home + '/calls.jsonl', JSON.stringify({ args, input, env: process.env,
    systemPrompt: file && readFileSync(file, 'utf8') }) + '\\n');
  if (args[0] === 'auth' && args[1] === 'status') {
    console.log(JSON.stringify({ loggedIn: scenario.auth !== 'none', authMethod: scenario.auth }, null, 2));
    process.exit(scenario.auth === 'none' ? 1 : 0);
  }
  if (args[0] === 'auth' && args[1] === 'logout') {
    if (scenario.logout === 'keep') process.exit(0); // Claims success but leaves the login behind.
    if (scenario.logout) process.exit(scenario.logout);
    writeFileSync(home + '/scenario.json', JSON.stringify({ ...scenario, auth: 'none' }));
    process.exit(0);
  }
  if (args[0] === 'auth') process.exit(args[1] === 'login' ? scenario.login ?? 0 : 0);
  console.error('private-provider-secret');
  for (const line of scenario.lines ?? []) console.log(JSON.stringify(line));
  process.exit(scenario.exit ?? 0);
})();
`;

const model = 'claude-sonnet-5-5';
const init = (tools = ['StructuredOutput'], mcp = []) => ({ type: 'system', subtype: 'init', tools, mcp_servers: mcp });
const toolUse = name => ({ type: 'assistant', message: { content: [{ type: 'tool_use', name, input: {} }] } });
const result = (overrides = {}) => ({
  type: 'result', subtype: 'success', is_error: false, result: 'private-provider-secret', structured_output: { ok: true },
  modelUsage: { [model]: {} },
  usage: { input_tokens: 12, output_tokens: 7, cache_read_input_tokens: 3, cache_creation_input_tokens: 5, secret: 'private-provider-secret' },
  ...overrides,
});
const success = [init(), toolUse('StructuredOutput'), result()];

async function bridge(command, scenario, request = {}, { binary = true } = {}) {
  const directory = await mkdtemp(join(tmpdir(), 'ppllm-claude-bridge-'));
  try {
    for (const name of ['claude-bridge.mjs', 'errors.mjs', 'protocol.mjs']) await copyFile(new URL(name, import.meta.url), join(directory, name));
    const musl = process.platform === 'linux' && !process.report.getReport().header.glibcVersionRuntime ? '-musl' : '';
    const module = join(directory, 'node_modules', '@anthropic-ai', `claude-code-${process.platform}-${process.arch}${musl}`);
    if (binary) {
      await mkdir(module, { recursive: true });
      await writeFile(join(module, 'package.json'), JSON.stringify({ name: 'fake-claude' }));
      await writeFile(join(module, 'claude'), fakeClaude);
      await chmod(join(module, 'claude'), 0o755);
    }
    const home = join(directory, 'home'), work = join(directory, 'work');
    await mkdir(home); await mkdir(work);
    await writeFile(join(home, 'scenario.json'), JSON.stringify({ auth: 'claude.ai', ...scenario }));
    const output = await new Promise((resolve, reject) => {
      const child = spawn(process.execPath, [join(directory, 'claude-bridge.mjs'), command], {
        cwd: work, timeout: 10000,
        env: { PATH: process.env.PATH, HOME: work, PPLLM_RUNNER_HOME: home, ANTHROPIC_API_KEY: 'sk-separately-billed', CLAUDE_CODE_OAUTH_TOKEN: 'leaked' },
      });
      let stdout = '', stderr = '';
      child.stdout.on('data', chunk => stdout += chunk);
      child.stderr.on('data', chunk => stderr += chunk);
      child.on('error', reject);
      child.on('close', code => resolve({ code, stdout, stderr }));
      child.stdin.end(JSON.stringify({ model, instructions: 'Synthetic policy', prompt: 'Synthetic OCR', images: ['data:image/png;base64,aGVsbG8='],
        schema: { type: 'object' }, reasoning: 'low', ...request }));
    });
    const calls = await readFile(join(home, 'calls.jsonl'), 'utf8').then(text => text.trim().split('\n').map(JSON.parse), () => []);
    return { ...output, calls, messages: output.stdout.trim() ? output.stdout.trim().split('\n').map(JSON.parse) : [] };
  } finally { await rm(directory, { recursive: true, force: true }); }
}

const options = { skip: process.platform === 'win32' && 'fake native binary needs a POSIX shebang' };

test('structured output becomes the result with validated token counts only', options, async () => {
  const run = await bridge('infer', { lines: success });
  assert.equal(run.code, 0);
  assert.deepEqual(run.messages, [{ type: 'result', text: '{"ok":true}', usage: { input: 12, output: 7, cacheRead: 3, cacheWrite: 5 } }]);
  assert.ok(!run.stdout.includes('private-provider-secret'));
  assert.equal(run.stderr, '');
});

test('print mode runs without tools, settings, MCP, persistence or provider keys', options, async () => {
  const { calls } = await bridge('infer', { lines: success });
  const call = calls.find(x => x.args[0] === '-p');
  const flag = name => call.args[call.args.indexOf(name) + 1];
  assert.equal(flag('--tools'), '');
  assert.equal(flag('--setting-sources'), '');
  assert.equal(flag('--permission-mode'), 'dontAsk');
  assert.equal(flag('--model'), model);
  assert.equal(flag('--effort'), 'low');
  assert.deepEqual(JSON.parse(flag('--json-schema')), { type: 'object' });
  for (const name of ['--strict-mcp-config', '--no-session-persistence', '--disable-slash-commands']) assert.ok(call.args.includes(name), name);
  assert.equal(call.systemPrompt, 'Synthetic policy');
  assert.ok(!call.args.some(arg => arg.includes('Synthetic OCR')), 'document text must not appear in argv');
  const message = JSON.parse(call.input).message;
  assert.deepEqual(message.content[0], { type: 'text', text: 'Synthetic OCR' });
  assert.deepEqual(message.content[1].source, { type: 'base64', media_type: 'image/png', data: 'aGVsbG8=' });
  for (const key of ['ANTHROPIC_API_KEY', 'CLAUDE_CODE_OAUTH_TOKEN']) assert.equal(call.env[key], undefined, key);
  assert.equal(call.env.CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC, '1');
  assert.ok(call.env.CLAUDE_CONFIG_DIR.endsWith('home'));
});

for (const [name, lines, code, exit] of [
  ['an exposed tool', [init(['StructuredOutput', 'Bash']), ...success.slice(1)], 'harness_unsafe', 21],
  ['an MCP server', [init(undefined, [{ name: 'x' }]), ...success.slice(1)], 'harness_unsafe', 21],
  ['a non-output tool call', [init(), toolUse('Bash'), result()], 'harness_unsafe', 21],
  ['a fallback model', [init(), result({ modelUsage: { [model]: {}, 'claude-haiku-5-5': {} } })], 'incomplete_response', 21],
  ['a missing structured output', [init(), result({ structured_output: undefined })], 'incomplete_response', 21],
  ['exhausted turns', [init(), result({ subtype: 'error_max_turns', is_error: true })], 'incomplete_response', 21],
  ['an expired login', [init(), { type: 'assistant', error: 'authentication_failed', message: { content: [] } }, result({ is_error: true })], 'auth_required', 20],
  ['a usage limit', [init(), { type: 'assistant', error: 'rate_limit', message: { content: [] } }, result({ is_error: true })], 'rate_limited', 22],
  ['a provider outage', [init(), result({ is_error: true, api_error_status: 529 })], 'provider_unavailable', 21],
  ['no result', [init()], 'incomplete_response', 21],
]) {
  test(`rejects ${name} without leaking provider details`, options, async () => {
    const run = await bridge('infer', { lines });
    assert.equal(run.code, exit);
    assert.deepEqual(run.messages, [{ type: 'error', code }]);
    assert.ok(!run.stdout.includes('private-provider-secret'));
  });
}

test('only a Claude subscription login counts as signed in', options, async () => {
  assert.deepEqual((await bridge('status', { auth: 'claude.ai' })).messages, [{ type: 'ready' }]);
  for (const auth of ['api_key', 'oauth_token', 'none'])
    assert.deepEqual((await bridge('status', { auth })).messages, [{ type: 'auth_required' }], auth);
});

test('inference never starts without a subscription login', options, async () => {
  const run = await bridge('infer', { auth: 'api_key', lines: success });
  assert.equal(run.code, 20);
  assert.ok(!run.calls.some(x => x.args[0] === '-p'));
});

test('unknown models and invalid images are rejected before the CLI runs', options, async () => {
  for (const [request, code] of [[{ model: 'gpt-6-sol' }, 'model_unavailable'], [{ images: ['file:///secret'] }, 'image_rejected']]) {
    const run = await bridge('infer', { lines: success }, request);
    assert.deepEqual(run.messages, [{ type: 'error', code }]);
    assert.ok(!run.calls.some(x => x.args[0] === '-p'));
  }
});

test('login delegates to the CLI subscription flow and reports failure by code only', options, async () => {
  const ok = await bridge('login', {});
  assert.equal(ok.code, 0);
  assert.deepEqual(ok.calls[0].args, ['auth', 'login', '--claudeai']);
  const failed = await bridge('login', { login: 1 });
  assert.equal(failed.code, 21);
  assert.deepEqual(failed.messages, [{ type: 'error', code: 'auth_failed' }]);
});

test('catalog and logout need no network', options, async () => {
  assert.deepEqual((await bridge('models', {})).messages[0].models.includes(model), true);
  assert.deepEqual((await bridge('logout', {})).messages, [{ type: 'logged_out' }]);
});

for (const [name, logout] of [['a failed logout', 1], ['a logout that leaves the login behind', 'keep']]) {
  test(`${name} is reported as a failure, not removed credentials`, options, async () => {
    const run = await bridge('logout', { logout });
    assert.equal(run.code, 21);
    assert.deepEqual(run.messages, [{ type: 'error', code: 'auth_failed' }]);
  });
}

test('a missing native binary is reported as unavailable', async () => {
  const run = await bridge('status', {}, {}, { binary: false });
  assert.deepEqual(run.messages, [{ type: 'error', code: 'harness_unavailable' }]);
});
