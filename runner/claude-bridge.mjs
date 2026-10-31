// Claude harness. The unmodified, pinned Claude Code CLI owns sign-in, credential storage, refresh and
// transport. Each request is one print-mode run with no built-in tools, MCP servers, settings sources,
// skills or session persistence. Only its structured output and token counts cross back to .NET.
import { spawn } from 'node:child_process';
import { writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { classifyError } from './errors.mjs';
import { dispatch, emit, emitError, parseImage, readRequest, safeUsage } from './protocol.mjs';

process.umask(0o077);
const require = createRequire(import.meta.url);
const maxOutput = 4 * 1024 * 1024;
// Pinned with the CLI version in package.json. Catalog presence does not prove plan entitlement.
const models = ['claude-haiku-5-5', 'claude-sonnet-5-5', 'claude-opus-5-5', 'claude-fable-5-1'];
// Claude Code's typed assistant errors. Its error text is never forwarded.
const assistantErrors = {
  authentication_failed: 'auth_required', rate_limit: 'rate_limited', billing_error: 'access_denied',
  invalid_request: 'request_rejected', server_error: 'provider_unavailable', max_output_tokens: 'incomplete_response',
};
let phase = 'auth';

// Resolve the published native binary directly; package install scripts are never run.
function binary() {
  const musl = process.platform === 'linux' && !process.report.getReport().header.glibcVersionRuntime ? '-musl' : '';
  const name = `@anthropic-ai/claude-code-${process.platform}-${process.arch}${musl}`;
  try { return join(dirname(require.resolve(`${name}/package.json`)), process.platform === 'win32' ? 'claude.exe' : 'claude'); }
  catch { throw new Error('harness_unavailable'); }
}

// Built from an allowlist: provider API keys and tokens can never select separately billed access.
function environment() {
  const env = {};
  for (const name of ['PATH', 'SystemRoot', 'WINDIR', 'PATHEXT', 'HOME', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA',
    'XDG_CONFIG_HOME', 'XDG_CACHE_HOME', 'TMPDIR', 'TMP', 'TEMP', 'NO_COLOR'])
    if (process.env[name] !== undefined) env[name] = process.env[name];
  return {
    ...env, CLAUDE_CONFIG_DIR: process.env.PPLLM_RUNNER_HOME, CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1',
    DISABLE_AUTOUPDATER: '1', CLAUDE_CODE_MAX_OUTPUT_TOKENS: '16000',
  };
}

function interactive(args) {
  return new Promise((resolve, reject) => {
    const child = spawn(binary(), args, { env: environment(), stdio: 'inherit' });
    child.on('error', reject);
    child.on('close', code => resolve(code));
  });
}

// Runs the CLI with stdin input; onLine sees each stdout line and may throw to stop the child.
function capture(args, input, onLine = () => {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(binary(), args, { env: environment(), stdio: ['pipe', 'pipe', 'ignore'] });
    let stdout = '', pending = '', failure;
    const fail = error => { failure ??= error; child.kill(); };
    const lines = text => { try { text.split('\n').filter(Boolean).forEach(onLine); } catch (error) { fail(error); } };
    child.stdout.setEncoding('utf8').on('data', chunk => {
      stdout += chunk;
      if (stdout.length > maxOutput) return fail(new Error('incomplete_response'));
      const complete = (pending + chunk).lastIndexOf('\n') + 1;
      lines((pending + chunk).slice(0, complete));
      pending = (pending + chunk).slice(complete);
    });
    child.on('error', reject);
    child.on('close', code => {
      if (!failure) lines(pending);
      return failure ? reject(failure) : resolve({ code, stdout });
    });
    child.stdin.on('error', () => {});
    child.stdin.end(input);
  });
}

async function signedIn() {
  const { stdout } = await capture(['auth', 'status'], '');
  try {
    const status = JSON.parse(stdout);
    // Only a Claude subscription login counts; API-billed Console logins are rejected.
    return status?.loggedIn === true && status.authMethod === 'claude.ai';
  } catch { return false; }
}

async function login() {
  if (await interactive(['auth', 'login', '--claudeai']) !== 0) throw new Error('auth_failed');
}

// A failed removal must never be reported as success; confirm the login is actually gone.
async function logout() {
  const { code } = await capture(['auth', 'logout'], '');
  if (code !== 0 || await signedIn()) throw new Error('auth_failed');
  emit({ type: 'logged_out' });
}

function userMessage(request) {
  const images = request.images.map(url => {
    const { mimeType, data } = parseImage(url);
    return { type: 'image', source: { type: 'base64', media_type: mimeType, data } };
  });
  return JSON.stringify({ type: 'user', message: { role: 'user', content: [{ type: 'text', text: request.prompt }, ...images] } }) + '\n';
}

function printArgs(request, systemPromptFile) {
  return ['-p', '--verbose', '--input-format', 'stream-json', '--output-format', 'stream-json',
    '--model', request.model, '--effort', request.reasoning, '--max-turns', '3',
    '--json-schema', JSON.stringify(request.schema), '--system-prompt-file', systemPromptFile,
    '--tools', '', '--disallowedTools', 'mcp__*', '--strict-mcp-config', '--setting-sources', '',
    '--disable-slash-commands', '--no-session-persistence', '--permission-mode', 'dontAsk'];
}

// Fail closed if the CLI ever exposes a tool besides structured output or an MCP server.
function unsafe(message) {
  if (message.type === 'system' && message.subtype === 'init')
    return message.tools?.some(tool => tool !== 'StructuredOutput') || message.mcp_servers?.length > 0;
  return message.type === 'assistant'
    && message.message?.content?.some(part => part.type === 'tool_use' && part.name !== 'StructuredOutput');
}

function inspect(message, state) {
  if (unsafe(message)) throw new Error('harness_unsafe');
  if (message.type === 'assistant' && message.error) state.error ??= message.error;
  if (message.type === 'result') state.result = message;
}

function outcome(state, model) {
  const result = state.result;
  if (state.error) return { code: assistantErrors[state.error] ?? 'inference_failed' };
  if (!result || result.subtype?.startsWith('error_max')) return { code: 'incomplete_response' };
  if (result.is_error || result.subtype !== 'success') return { code: classifyError(String(result.api_error_status ?? '')) };
  // A model fallback or helper model would show up here; accept only the configured model.
  if (Object.keys(result.modelUsage ?? {}).join('\n') !== model) return { code: 'incomplete_response' };
  const output = result.structured_output;
  if (output === null || typeof output !== 'object' || Array.isArray(output)) return { code: 'incomplete_response' };
  const usage = result.usage ?? {};
  return { text: JSON.stringify(output),
    ...safeUsage(usage.input_tokens, usage.output_tokens, usage.cache_read_input_tokens, usage.cache_creation_input_tokens) };
}

async function infer() {
  if (!await signedIn()) throw new Error('auth_required');
  const request = await readRequest();
  phase = 'inference';
  if (!models.includes(request.model)) return emitError('model_unavailable');
  const input = userMessage(request);
  const systemPromptFile = join(process.cwd(), 'system-prompt.txt');
  await writeFile(systemPromptFile, request.instructions, { encoding: 'utf8', mode: 0o600 });
  const state = {};
  await capture(printArgs(request, systemPromptFile), input, line => inspect(JSON.parse(line), state));
  const { code, text, usage } = outcome(state, request.model);
  if (code) return emitError(code);
  emit({ type: 'result', text, ...(usage ? { usage } : {}) });
}

async function run() {
  await dispatch({
    login,
    logout,
    status: async () => emit({ type: await signedIn() ? 'ready' : 'auth_required' }),
    models: async () => emit({ type: 'models', models }),
    infer,
  }, process.argv[2]);
}

try { await run(); }
catch (error) { emitError(classifyError(error, phase)); }
