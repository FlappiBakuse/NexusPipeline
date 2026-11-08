'use strict';
const labels = [
  ['打开工作目录', 'Opening workspace'], ['检查本地数据', 'Checking local data'],
  ['恢复上次操作', 'Recovering previous operations'], ['读取设置', 'Reading settings'],
  ['恢复执行记录', 'Restoring execution records'], ['准备服务', 'Preparing services'],
  ['检查内置插件', 'Checking bundled plugins'], ['启动插件', 'Starting plugins'],
  ['启动本地服务', 'Starting local service'], ['启动客户端', 'Starting client'],
  ['检查或应用启动更新', 'Checking startup updates'], ['正在打开控制台', 'Opening console']
];
let state, elapsed = 0, updated = performance.now();
function render(value) {
  const now = performance.now();
  elapsed = Math.max(elapsed + now - updated, Number.isFinite(value.elapsedMilliseconds) ? value.elapsedMilliseconds : 0);
  state = value; updated = now;
  const en = value.locale === 'en-US';
  const failed = value.kind === 'failed';
  document.documentElement.lang = en ? 'en' : 'zh-CN';
  document.querySelector('#stage').textContent = failed ? (en ? 'Startup failed' : '启动失败') :
    `${value.stage + 1}/${value.total} · ${labels[value.stage][en ? 1 : 0]}`;
  document.querySelector('#progress').hidden = failed;
  if (!failed) document.querySelector('#progress').value = value.stage + 1;
  if (!failed && value.preparation) {
    const phases = { '准备界面': 'Preparing interface', '加载插件界面': 'Loading extension interfaces', '准备页面数据': 'Preparing page data', '准备首次显示': 'Preparing first display' };
    const { stage, done, total } = value.preparation;
    document.querySelector('#stage').textContent = `${en ? phases[stage] : stage}${total ? ` · ${done}/${total}` : ''}`;
    const progress = document.querySelector('#progress');
    if (total) { progress.max = total; progress.value = done; }
    else progress.removeAttribute('value');
  }
  document.querySelector('#actions').hidden = !failed;
  document.querySelector('#diagnostic').hidden = !failed;
  document.querySelector('#diagnostic').textContent = failed ? `${value.reason}\n${value.errorCode}` : '';
  document.querySelector('#copy').textContent = en ? 'Copy diagnostics' : '复制诊断';
  document.querySelector('#exit').textContent = en ? 'Exit' : '退出';
}
for (const id of ['hide', 'minimize', 'maximize', 'copy', 'exit'])
  document.getElementById(id).addEventListener('click', () => window.nexusStartup.action(id));
window.nexusStartup.subscribe(render);
window.nexusStartup.action('state').then(render);
setInterval(() => {
  if (!state) return;
  const seconds = Math.floor((elapsed + performance.now() - updated) / 1000);
  document.querySelector('#elapsed').textContent = state.kind === 'failed' ? '' :
    state.locale === 'en-US' ? `Elapsed: ${seconds}s` : `已用时 ${seconds} 秒`;
}, 250);
