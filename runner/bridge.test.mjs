import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, copyFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn } from 'node:child_process';

async function bridge(command, request = {}) {
  const directory = await mkdtemp(join(tmpdir(), 'ppllm-bridge-'));
  try {
    for (const name of ['bridge.mjs', 'errors.mjs']) await copyFile(new URL(name, import.meta.url), join(directory, name));
    const module = join(directory, 'node_modules', '@earendil-works', 'pi-coding-agent');
    await mkdir(module, { recursive: true });
    await writeFile(join(module, 'package.json'), JSON.stringify({ type: 'module', exports: './index.mjs' }));
    await writeFile(join(module, 'index.mjs'), `
      export class ModelRuntime {
        static async create(options) {
          if(options.modelsPath!==null || options.allowModelNetwork!==false || options.refreshOnCreate!==false)
            throw Error('unsafe runtime configuration');
          return new ModelRuntime();
        }
        async login(provider, type, callbacks) {
          if(provider!=='openai-codex'||type!=='oauth') throw Error('wrong auth');
          if(await callbacks.prompt({type:'select',options:[{id:'device_code'}]})!=='device_code') throw Error('wrong flow');
          callbacks.notify({type:'device_code',verificationUri:'https://auth.openai.com/codex/device',userCode:'ABCD-1234'});
        }
        async logout() {}
        async checkAuth() { return {type:'oauth'}; }
        getModels() { return [{id:'model',input:['image']}]; }
        getModel(provider,id) { return id==='missing'?null:{id,provider,input:['image'],contextWindow:100000}; }
        async completeSimple(model, context, options) {
          if(context.tools.length || options.reasoning!=='low' || options.maxTokens!==16000) throw Error('unsafe inference');
          if(!context.systemPrompt.includes('No Markdown.')) throw Error('missing schema contract');
          if(model.id==='rate') return {stopReason:'error',errorMessage:'429 private-provider-secret'};
          const usage = model.id==='usage' ? {input:12,output:7,cacheRead:3,cacheWrite:0,secret:'private-provider-secret'}
            : model.id==='bad-usage' ? {input:-1,output:7,cacheRead:3,cacheWrite:0} : undefined;
          return {model:model.id,provider:'openai-codex',stopReason:'stop',usage,content:
            model.id==='tool'?[{type:'toolCall',name:'shell'}]:[{type:'text',text:'{"ok":true}'}]};
        }
      }
    `);
    return await new Promise((resolve, reject) => {
      const child = spawn(process.execPath, [join(directory, 'bridge.mjs'), command], {
        env: { ...process.env, PPLLM_RUNNER_HOME: join(directory, 'auth') }, timeout: 5000,
      });
      let stdout = '', stderr = '';
      child.stdout.on('data', chunk => stdout += chunk);
      child.stderr.on('data', chunk => stderr += chunk);
      child.on('error', reject);
      child.on('close', code => resolve({ code, stdout, stderr, messages: stdout.trim().split('\n').map(JSON.parse) }));
      child.stdin.end(JSON.stringify({ model:'model', instructions:'Policy', prompt:'Synthetic', images:[], schema:{type:'object'}, reasoning:'low', ...request }));
    });
  } finally { await rm(directory, { recursive: true, force: true }); }
}

test('device code is the only requested login interaction', async () => {
  const result = await bridge('login');
  assert.equal(result.code, 0);
  assert.deepEqual(result.messages, [
    {type:'device_code',url:'https://auth.openai.com/codex/device',code:'ABCD-1234'}, {type:'ready'},
  ]);
});
test('metadata inference has no tools and preserves raw JSON', async () => {
  const result = await bridge('infer');
  assert.equal(result.code, 0);
  assert.deepEqual(result.messages, [{type:'result',text:'{"ok":true}'}]);
});
test('usage forwards only validated token counts', async () => {
  const result = await bridge('infer', {model:'usage'});
  assert.deepEqual(result.messages[0].usage, {input:12,output:7,cacheRead:3,cacheWrite:0});
  assert.ok(!result.stdout.includes('private-provider-secret'));
  assert.equal((await bridge('infer', {model:'bad-usage'})).messages[0].usage, undefined);
});
for (const [model, code, exit] of [['missing','model_unavailable',21],['tool','incomplete_response',21],['rate','rate_limited',22]]) {
  test(`rejects ${model} without leaking provider details`, async () => {
    const result = await bridge('infer', {model});
    assert.equal(result.code, exit);
    assert.deepEqual(result.messages, [{type:'error',code}]);
    assert.equal(result.stderr, '');
    assert.ok(!result.stdout.includes('private-provider-secret'));
  });
}
test('rejects invalid image references before model invocation', async () => {
  const result = await bridge('infer', {images:['file:///secret']});
  assert.deepEqual(result.messages, [{type:'error',code:'image_rejected'}]);
});
test('rejects oversized context before model invocation', async () => {
  const result = await bridge('infer', {prompt:'x'.repeat(100000)});
  assert.deepEqual(result.messages, [{type:'error',code:'context_limit'}]);
});
test('unknown inherited command names do not dispatch', async () => {
  const result = await bridge('constructor');
  assert.equal(result.code, 21);
  assert.deepEqual(result.messages, [{type:'error',code:'auth_failed'}]);
});
