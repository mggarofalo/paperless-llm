// Pi owns OAuth storage, refresh and transport. No AgentSession is constructed:
// there is no tool dispatcher, extension loader, project context or agent loop.
import { ModelRuntime } from '@earendil-works/pi-coding-agent';
import { join } from 'node:path';
import { classifyError } from './errors.mjs';

process.umask(0o077);
const provider = 'openai-codex';
const emit = value => process.stdout.write(JSON.stringify(value) + '\n');
let phase = 'auth';

async function login(runtime) {
  await runtime.login(provider, 'oauth', {
    prompt: async prompt => {
      if (prompt.type === 'select' && prompt.options.some(x => x.id === 'device_code')) return 'device_code';
      throw new Error('unsupported_auth_interaction');
    },
    notify: event => {
      if (event.type === 'device_code' && event.verificationUri === 'https://auth.openai.com/codex/device')
        emit({ type: 'device_code', url: event.verificationUri, code: event.userCode });
    },
  });
  emit({ type: 'ready' });
}

async function readRequest() {
  const chunks = [];
  let size = 0;
  for await (const chunk of process.stdin) {
    size += chunk.length;
    if (size > 48 * 1024 * 1024) throw new Error('input_limit');
    chunks.push(chunk);
  }
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

function imageContent(url) {
  const match = /^data:(image\/(?:png|jpeg));base64,([A-Za-z0-9+/=]+)$/.exec(url);
  if (!match) throw new Error('image_invalid');
  return { type: 'image', mimeType: match[1], data: match[2] };
}

function context(request, model) {
  // Conservative admission bound: UTF-8 bytes plus page and output allowances.
  if (Buffer.byteLength(request.prompt + request.instructions + JSON.stringify(request.schema), 'utf8') + request.images.length * 16000 + 16000 > model.contextWindow)
    throw new Error('context_limit');
  return {
    systemPrompt: request.instructions + '\nReturn exactly one JSON object conforming to this schema. No Markdown.\n' + JSON.stringify(request.schema),
    messages: [{ role: 'user', content: [{ type: 'text', text: request.prompt }, ...request.images.map(imageContent)], timestamp: Date.now() }],
    tools: [],
  };
}

function emitError(code) {
  emit({ type: 'error', code });
  process.exitCode = code === 'rate_limited' ? 22 : code === 'auth_required' ? 20 : 21;
}

function emitResult(result, model) {
  if (result.stopReason === 'error' || result.stopReason === 'aborted') {
    // Provider error text can contain request/credential details. Never forward it.
    emitError(classifyError(result.errorMessage));
  } else if (result.model !== model.id || result.provider !== provider || result.stopReason !== 'stop' || result.content.some(x => x.type === 'toolCall')) {
    emitError('incomplete_response');
  } else {
    const text = result.content.filter(x => x.type === 'text').map(x => x.text).join('');
    // Preserve raw text for private diagnostics; .NET validates before any write.
    emit({ type: 'result', text });
  }
}

async function infer(runtime) {
  if ((await runtime.checkAuth(provider))?.type !== 'oauth') throw new Error('auth_required');
  const request = await readRequest();
  phase = 'inference';
  const model = runtime.getModel(provider, request.model);
  if (!model || !model.input?.includes('image')) {
    emitError('model_unavailable');
    return;
  }
  const result = await runtime.completeSimple(model, context(request, model), { reasoning: request.reasoning, maxTokens: 16000 });
  emitResult(result, model);
}

async function run() {
  const runtime = await ModelRuntime.create({
    authPath: join(process.env.PPLLM_RUNNER_HOME, 'auth.json'),
    modelsPath: null,
    modelsStorePath: join(process.env.PPLLM_RUNNER_HOME, 'models-cache.json'),
    allowModelNetwork: false,
    refreshOnCreate: false,
  });
  const commands = {
    login: () => login(runtime),
    logout: async () => { await runtime.logout(provider); emit({ type: 'logged_out' }); },
    status: async () => emit({ type: (await runtime.checkAuth(provider))?.type === 'oauth' ? 'ready' : 'auth_required' }),
    models: () => emit({ type: 'models', models: runtime.getModels(provider).filter(x => x.input?.includes('image')).map(x => x.id) }),
    infer: () => infer(runtime),
  };
  const command = commands[process.argv[2]];
  if (!Object.hasOwn(commands, process.argv[2])) throw new Error('unknown_command');
  await command();
}

try { await run(); }
catch (error) { emitError(classifyError(error, phase)); }
