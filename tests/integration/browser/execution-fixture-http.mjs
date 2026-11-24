import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import { setTimeout } from 'node:timers/promises';
import { isTemporaryReadFailure } from './runtime-ready.mjs';

export function executionFixtureInputs(script, browser) {
  const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  return { script: hash(script), browser: hash(browser), executionFixtureHttp: hash(new URL(import.meta.url)),
    runtimeReady: hash(new URL('./runtime-ready.mjs', import.meta.url)) };
}

export function executionFixtureHttp(address, token, waitMilliseconds) {
  async function call(route, body, missing = false) {
    const response = await fetch(new URL('/control/v1/' + route, address), {
      method: body === undefined ? 'GET' : 'POST', headers: { Authorization: 'Bearer ' + token,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }), signal: AbortSignal.timeout(30000) });
    if (missing && response.status === 404) return undefined;
    if (await isTemporaryReadFailure(route, response)) return undefined;
    assert.equal(response.ok, true, route + ':' + response.status + ':' + (response.ok ? '' : await response.text()));
    return response.json();
  }

  async function until(action, reason) {
    const deadline = Date.now() + waitMilliseconds;
    while (Date.now() < deadline) {
      const result = await action();
      if (result) return result;
      await setTimeout(100);
    }
    throw Error(reason);
  }

  return { call, until };
}

const runIdPattern = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/;

function hasRunIdentity(value) {
  return typeof value?.runId === 'string' && runIdPattern.test(value.runId) &&
    typeof value.attemptId === 'string' && runIdPattern.test(value.attemptId);
}

export async function startFixtureRun(starts, operationId, send) {
  const start = { operationId };
  starts.push(start);
  const accepted = await send();
  assert.ok(hasRunIdentity(accepted), 'fixture_start_response_identity_invalid');
  Object.assign(start, { runId: accepted.runId, attemptId: accepted.attemptId });
  return start;
}

export async function finishFixtureRuns(starts, readActive, cancel, waitFor, describeError) {
  const cleanup = { cleanupUnknown: false, activeObservation: 'no-recorded-run', runs: [], errors: [] };
  if (!starts.length) return cleanup;
  const markUnknown = (evidence, reason) => {
    Object.assign(evidence, { outcome: 'unknown', reason });
    cleanup.cleanupUnknown = true;
  };
  const read = async () => {
    const active = await readActive();
    assert.ok(Array.isArray(active?.items), 'cleanup_active_response_invalid');
    return active.items;
  };
  let active;
  try {
    active = await read();
    cleanup.activeObservation = 'observed';
  } catch (error) {
    cleanup.activeObservation = 'unavailable';
    cleanup.errors.push({ stage: 'activeRead', reason: describeError(error) });
    cleanup.runs = starts.map(start => {
      const evidence = { ...start };
      markUnknown(evidence, 'cleanup_active_unavailable');
      return evidence;
    });
    return cleanup;
  }
  for (const start of starts) {
    const evidence = { ...start, outcome: 'already-inactive' };
    cleanup.runs.push(evidence);
    let run = start;
    let current;
    if (hasRunIdentity(start)) {
      current = active.find(item => item.runId === start.runId);
      if (!current) continue;
      if (current.operationId !== start.operationId || current.attemptId !== start.attemptId) {
        markUnknown(evidence, 'cleanup_active_identity_changed');
        continue;
      }
    } else {
      const matches = active.filter(item => item.operationId === start.operationId);
      if (matches.length !== 1 || !hasRunIdentity(matches[0])) {
        markUnknown(evidence, 'cleanup_start_unconfirmed');
        continue;
      }
      current = matches[0];
      run = { operationId: start.operationId, runId: current.runId, attemptId: current.attemptId };
      Object.assign(evidence, run, { admissionEvidence: 'active-operation' });
    }
    if (current.cancellationRequested) evidence.cancellation = 'already-requested';
    else {
      try {
        await cancel(run);
        evidence.cancellation = 'accepted';
      } catch (error) {
        evidence.cancellation = 'unconfirmed';
        evidence.cancellationReason = describeError(error);
      }
    }
    try {
      await waitFor(async () => !(await read()).some(item => item.runId === run.runId), 'owned_fixture_run_did_not_stop');
      evidence.outcome = 'inactive-observed';
    } catch (error) {
      markUnknown(evidence, describeError(error));
    }
  }
  return cleanup;
}
