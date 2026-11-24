export async function waitForBrowserContent(page, content, token) {
  const password = page.locator('.authentication input[type=password]');
  await content.or(password).first().waitFor();
  if (await password.isVisible()) {
    await password.fill(token);
    await page.locator('.authentication button').click();
  }
  await content.waitFor();
}

export async function isTemporaryReadFailure(route, response) {
  if ((route === 'status' || route === 'automation/users/summary') && response.status === 500)
    return (await response.clone().json()).code === 'control_failed';
  return route === 'adapters/discover' && response.status === 409 &&
    (await response.clone().json()).code === 'resource_unavailable';
}

export async function waitForRecovery(call, until) {
  let stable;
  await until(async () => {
    const status = await call('status');
    if (!status || status.admission !== 'ready' || status.pendingOperations !== 0) {
      stable = undefined;
      return false;
    }
    // The installed package sweeper starts on the next one-second tick.
    const now = Date.now();
    if (stable?.revision !== status.recoveryRevision) stable = { revision: status.recoveryRevision, since: now };
    return now - stable.since >= 1500;
  }, 'recovery_not_ready');
}
