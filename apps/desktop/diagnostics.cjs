'use strict';

const types = new Set(['Error', 'TypeError', 'RangeError', 'SyntaxError', 'AbortError', 'TimeoutError']);
const codes = new Set(['ENOENT', 'EACCES', 'EPERM', 'EEXIST', 'EBUSY', 'ENOSPC', 'EIO', 'ECONNREFUSED', 'ECONNRESET']);
const reasons = new Set([
  'browser_login_storage_conflict', 'browser_login_storage_not_cleared', 'browser_login_window_not_closed',
  'desktop_bootstrap_required', 'desktop_bootstrap_invalid', 'desktop_locale_invalid', 'desktop_window_state_invalid',
  'desktop_login_identity_changed', 'desktop_login_limit', 'desktop_login_reply_invalid', 'desktop_login_offer_invalid',
  'desktop_host_startup_unavailable', 'desktop_host_instance_or_challenge_mismatch', 'desktop_document_changed',
  'desktop_proof_encoding', 'desktop_proof_format', 'desktop_proof_invalid', 'desktop_payload_invalid',
  'desktop_response_unconfirmed', 'desktop_response_limit', 'desktop_close_binding_invalid', 'desktop_close_expiry_invalid',
  'desktop_startup_pipe_invalid', 'desktop_startup_channel_ended', 'desktop_startup_frame_limit',
  'desktop_menu_invalid', 'desktop_startup_stage_invalid', 'desktop_startup_message_invalid'
]);
const uuid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;

function describeFailure(error) {
  const type = types.has(error?.name) ? error.name : 'Error';
  const code = codes.has(error?.code) ? error.code : error?.cause?.code;
  return { type, reason: reasons.has(error?.message) ? error.message : codes.has(code) ? code : 'unclassified' };
}

function recordDiagnostic(kind, stage, details = {}) {
  try { console.error(JSON.stringify({ kind, stage, ...details })); } catch { }
}

function recordFailure(kind, stage, error, correlationId) {
  recordDiagnostic(kind, stage, { ...describeFailure(error), ...(uuid.test(correlationId) ? { correlationId } : {}) });
}

module.exports = { describeFailure, recordDiagnostic, recordFailure };
