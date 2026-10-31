// Codex harness. Pi owns OAuth storage, refresh and transport. No AgentSession is constructed:
// there is no tool dispatcher, extension loader, project context or agent loop.
import { join } from 'node:path';
import { classifyError } from './errors.mjs';
import { dispatch, emit, emitError, parseImage, readRequest, safeUsage } from './protocol.mjs';

process.umask(0o077);
// Pi reads its agent directory from the environment; keep it in the per-request work directory.
process.env.PI_CODING_AGENT_DIR = process.cwd();
const { ModelRuntime } = await import('@earendil-works/pi-coding-agent');
const provider = 'openai-codex';
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

function imageContent(url) {
  const { mimeType, data } = parseImage(url);
  return { type: 'image', mimeType, data };
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

function emitResult(result, model) {
  if (result.stopReason === 'error' || result.stopReason === 'aborted') {
    // Provider error text can contain request/credential details. Never forward it.
    emitError(classifyError(result.errorMessage));
  } else if (result.model !== model.id || result.provider !== provider || result.stopReason !== 'stop' || result.content.some(x => x.type === 'toolCall')) {
    emitError('incomplete_response');
  } else {
    const text = result.content.filter(x => x.type === 'text').map(x => x.text).join('');
    const usage = result.usage ?? {};
    // Preserve raw text for private diagnostics; .NET validates before any write.
    emit({ type: 'result', text, ...safeUsage(usage.input, usage.output, usage.cacheRead, usage.cacheWrite) });
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
  await dispatch({
    login: () => login(runtime),
    logout: async () => { await runtime.logout(provider); emit({ type: 'logged_out' }); },
    status: async () => emit({ type: (await runtime.checkAuth(provider))?.type === 'oauth' ? 'ready' : 'auth_required' }),
    models: () => emit({ type: 'models', models: runtime.getModels(provider).filter(x => x.input?.includes('image')).map(x => x.id) }),
    infer: () => infer(runtime),
  }, process.argv[2]);
}

try { await run(); }
catch (error) { emitError(classifyError(error, phase)); }
