// Only fixed diagnostic codes cross into ordinary logs; never forward provider text.
export function classifyError(error, phase = 'inference') {
  const message = String(error?.message ?? error ?? '');
  const cause = String(error?.cause?.code ?? error?.code ?? '');
  if (/429|rate.?limit|usage.?limit|usage_not_included|too many requests/i.test(message))
    return 'rate_limited';
  if (/invalid_image|image.*(invalid|corrupt|decode|unsupported)|(?:invalid|corrupt).*image|image_parse_error/i.test(message))
    return 'image_rejected';
  if (/model.*(?:not supported|unsupported|does not exist|not found|no access|not available)|model_not_found/i.test(message))
    return 'model_access_denied';
  if (/401|unauthoriz|auth_required|token.*expir|invalid_grant|refresh.*fail/i.test(message))
    return 'auth_required';
  if (/403|forbidden/i.test(message)) return 'access_denied';
  if (/fetch failed|ECONN|ENOTFOUND|EAI_AGAIN|ETIMEDOUT|certificate|TLS|socket/i.test(message + ' ' + cause))
    return 'transport_failed';
  if (/\b5\d\d\b/.test(message)) return 'provider_unavailable';
  if (/\b400\b|bad request|invalid_request/i.test(message)) return 'request_rejected';
  if (['context_limit', 'image_invalid', 'input_limit'].includes(message)) return message;
  return phase === 'auth' ? 'auth_failed' : 'inference_failed';
}
