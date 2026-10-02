// Pi owns OAuth storage, refresh and transport. No AgentSession is constructed:
// there is no tool dispatcher, extension loader, project context or agent loop.
import { ModelRuntime } from '@earendil-works/pi-coding-agent';
import { join } from 'node:path';
import { classifyError } from './errors.mjs';

process.umask(0o077);
const provider = 'openai-codex';
const emit = value => process.stdout.write(JSON.stringify(value) + '\n');
const command = process.argv[2];
let phase = 'auth';
try {
  const runtime = await ModelRuntime.create({
    authPath: join(process.env.PPLLM_RUNNER_HOME, 'auth.json'),
    modelsPath: null,
    modelsStorePath: join(process.env.PPLLM_RUNNER_HOME, 'models-cache.json'),
    allowModelNetwork: false,
    refreshOnCreate: false,
  });
  if (command === 'login') {
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
  } else if (command === 'logout') {
    await runtime.logout(provider);
    emit({ type: 'logged_out' });
  } else if (command === 'status') {
    emit({ type: (await runtime.checkAuth(provider))?.type === 'oauth' ? 'ready' : 'auth_required' });
  } else if (command === 'models') {
    emit({ type: 'models', models: runtime.getModels(provider).filter(x => x.input?.includes('image')).map(x => x.id) });
  } else if (command === 'infer') {
    if ((await runtime.checkAuth(provider))?.type !== 'oauth') throw new Error('auth_required');
    const chunks = [];
    let size = 0;
    for await (const chunk of process.stdin) {
      size += chunk.length;
      if (size > 48 * 1024 * 1024) throw new Error('input_limit');
      chunks.push(chunk);
    }
    const request = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    phase = 'inference';
    const model = runtime.getModel(provider, request.model);
    if (!model || !model.input?.includes('image')) {
      emit({ type: 'error', code: 'model_unavailable' });
      process.exitCode = 21;
    } else {
      // Conservative admission bound: UTF-8 bytes plus page and output allowances.
      if (Buffer.byteLength(request.prompt + request.instructions + JSON.stringify(request.schema), 'utf8') + request.images.length * 16000 + 16000 > model.contextWindow)
        throw new Error('context_limit');
      const images = request.images.map(url => {
        const match = /^data:(image\/(?:png|jpeg));base64,([A-Za-z0-9+/=]+)$/.exec(url);
        if (!match) throw new Error('image_invalid');
        return { type: 'image', mimeType: match[1], data: match[2] };
      });
      const result = await runtime.completeSimple(model, {
        systemPrompt: request.instructions + '\nReturn exactly one JSON object conforming to this schema. No Markdown.\n' + JSON.stringify(request.schema),
        messages: [{ role: 'user', content: [{ type: 'text', text: request.prompt }, ...images], timestamp: Date.now() }],
        tools: [],
      }, { reasoning: request.reasoning, maxTokens: 16000 });
      if (result.stopReason === 'error' || result.stopReason === 'aborted') {
        // Provider error text can contain request/credential details. Never forward it.
        const code = classifyError(result.errorMessage);
        emit({ type: 'error', code });
        process.exitCode = code === 'rate_limited' ? 22 : code === 'auth_required' ? 20 : 21;
      } else if (result.model !== model.id || result.provider !== provider || result.stopReason !== 'stop' || result.content.some(x => x.type === 'toolCall')) {
        emit({ type: 'error', code: 'incomplete_response' });
        process.exitCode = 21;
      } else {
        const text = result.content.filter(x => x.type === 'text').map(x => x.text).join('');
        // Preserve raw text for private diagnostics; .NET parses and validates before any write.
        emit({ type: 'result', text });
      }
    }
  } else throw new Error('unknown_command');
} catch (error) {
  const code = classifyError(error, phase);
  emit({ type: 'error', code });
  process.exitCode = code === 'rate_limited' ? 22 : code === 'auth_required' ? 20 : 21;
}
