// Only fixed diagnostic codes cross into ordinary logs; never forward provider text.
export function classifyError(error, phase = 'inference') {
  const { message, cause } = diagnosticText(error);
  const matched = errorRules.find(rule => rule.pattern.test(rule.includeCause ? message + ' ' + cause : message));
  if (matched) return matched.code;
  if (['context_limit', 'image_invalid', 'input_limit'].includes(message)) return message;
  return phase === 'auth' ? 'auth_failed' : 'inference_failed';
}

function diagnosticText(error) {
  return { message: String(error?.message ?? error ?? ''), cause: String(error?.cause?.code ?? error?.code ?? '') };
}

const errorRules = [
  { pattern: /429|rate.?limit|usage.?limit|usage_not_included|too many requests/i, code: 'rate_limited', includeCause: false },
  { pattern: /invalid_image|image.*(invalid|corrupt|decode|unsupported)|(?:invalid|corrupt).*image|image_parse_error/i, code: 'image_rejected', includeCause: false },
  { pattern: /model.*(?:not supported|unsupported|does not exist|not found|no access|not available)|model_not_found/i, code: 'model_access_denied', includeCause: false },
  { pattern: /401|unauthoriz|auth_required|token.*expir|invalid_grant|refresh.*fail/i, code: 'auth_required', includeCause: false },
  { pattern: /403|forbidden/i, code: 'access_denied', includeCause: false },
  { pattern: /fetch failed|ECONN|ENOTFOUND|EAI_AGAIN|ETIMEDOUT|certificate|TLS|socket/i, code: 'transport_failed', includeCause: true },
  { pattern: /\b5\d\d\b/, code: 'provider_unavailable', includeCause: false },
  { pattern: /\b400\b|bad request|invalid_request/i, code: 'request_rejected', includeCause: false },
];
