/**
 * Error codes the tools answer with. Kept to a fixed vocabulary so a failure can never carry the
 * token, a URL or a backend stack trace into the model's context.
 */
const STABLE = [
  'agenthub_not_configured', 'agenthub_invalid_url',
  'agenthub_response_too_large', 'agenthub_invalid_json',
  // expiry.mjs throws this before any HTTP call when autoDeleteAfter has no unit or is out of range.
  'autodelete_invalid_duration'
];

// A code the backend named in its error body (client.mjs attaches it as error.code). Only the
// shape is trusted, not the content: anything that is not a short snake_case word is dropped.
const CODE_SHAPE = /^[a-z][a-z0-9_]{0,63}$/;

export function safeError(error) {
  if (typeof error?.code === 'string' && CODE_SHAPE.test(error.code)) return error.code;
  const message = error instanceof Error ? error.message : '';
  return STABLE.find(code => message.includes(code)) ??
    (/agenthub_http_\d{3}/.exec(message)?.[0]) ?? 'agenthub_operation_failed';
}

/**
 * What the sharing endpoints' statuses mean, as the codes the remote MCP server uses for the same
 * failures — so a client that talks to both sees one vocabulary. 400 is left to the backend's
 * own `code` (unknown_recipient) because a bare 400 says nothing a caller could act on.
 */
const SHARING_STATUS = { 402: 'license_required', 404: 'session_not_found' };

export function sharingErrorCode(error) {
  if (typeof error?.code === 'string' && CODE_SHAPE.test(error.code)) return error.code;
  const status = /agenthub_http_(\d{3})/.exec(error instanceof Error ? error.message : '')?.[1];
  return SHARING_STATUS[status] ?? safeError(error);
}
