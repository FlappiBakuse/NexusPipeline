'use strict';
const toolbar = document.getElementById('toolbar');
const status = document.getElementById('status');
const buttons = [...toolbar.querySelectorAll('button')];
let gotUpdate = false;

function render(state) {
  document.documentElement.lang = state.locale;
  document.documentElement.dataset.theme = state.theme;
  if (status.textContent !== state.status) status.textContent = state.status;
  for (const button of buttons) {
    const action = button.dataset.action;
    button.setAttribute('aria-disabled', String(!state.enabled[action]));
  }
}

window.loginShell.onState(state => { gotUpdate = true; render(state); });
window.loginShell.action('state').then(state => {
  const labels = state.labels;
  document.getElementById('title').textContent = labels.title;
  for (const id of ['toolbar', 'navigation', 'finish']) document.getElementById(id).setAttribute('aria-label', labels[id]);
  for (const button of buttons) {
    const action = button.dataset.action;
    if (action === 'back' || action === 'forward') {
      button.setAttribute('aria-label', labels[action]);
      button.title = labels[action];
    } else button.textContent = labels[action];
  }
  if (!gotUpdate) render(state);
  toolbar.inert = false;
});
toolbar.addEventListener('click', event => {
  const button = event.target.closest('button');
  if (button && button.getAttribute('aria-disabled') === 'false') void window.loginShell.action(button.dataset.action).catch(() => {});
});
