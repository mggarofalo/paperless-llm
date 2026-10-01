import { test } from 'node:test';
import assert from 'node:assert/strict';
import { classifyError } from './errors.mjs';

for (const [message, expected] of [
  ['400 invalid_image: failed to decode image with private OCR', 'image_rejected'],
  ['The model is not supported when using this account', 'model_access_denied'],
  ['401 expired access token SECRET', 'auth_required'],
  ['403 Forbidden secret', 'access_denied'],
  ['429 usage limit secret', 'rate_limited'],
  ['503 private provider response', 'provider_unavailable'],
  ['400 invalid_request sensitive data', 'request_rejected'],
  ['Unexpected response with secret', 'inference_failed'],
]) test(expected, () => assert.equal(classifyError(message), expected));
test('network errors do not become sign-in failures', () => {
  assert.equal(classifyError({ message: 'fetch failed', cause: { code: 'ENOTFOUND' } }, 'auth'), 'transport_failed');
});
