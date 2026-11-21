'use strict';
let locale = 'zh-CN';
const words = {
  loginLoading: ['正在加载官方登录页', 'Loading official login page'], loginActions: ['登录操作', 'Sign-in actions'], loginRefresh: ['刷新登录页', 'Refresh sign-in page'],
  loginLoadFailed: ['官方页面加载失败，请在登录操作中刷新', 'Page failed to load; refresh from Sign-in actions'],
  loginComplete: ['完成并验证', 'Finish and verify'],
  loginRejected: ['验证未通过，可继续登录后再次完成', 'Verification rejected; continue signing in and finish again'],
  loginIncomplete: ['登录信息尚未齐全，请继续登录', 'Sign-in values are incomplete; continue signing in'],
  loginRetry: ['验证暂不可用，请重试完成', 'Verification unavailable; try finishing again'],
  show: ['显示', 'Show'], hide: ['隐藏', 'Hide'], quit: ['退出 NexusPipeline', 'Quit NexusPipeline'],
  closing: ['正在安全退出…', 'Quitting safely…'],
  closeRecoveryPending: ['宿主仍有恢复事项需要处理，已取消退出。', 'The Host has recovery items that require attention. Quit was cancelled.'],
  closeRecoveryUnverified: ['宿主的恢复状态待核实，已取消退出。', 'Host recovery is awaiting verification. Quit was cancelled.'],
  closeMaintenancePending: ['宿主正在进行维护，已取消此次退出。', 'Host maintenance is in progress. This quit request was cancelled.'],
  closeWorkPending: ['宿主仍在结束任务，已取消退出。', 'The Host is still finishing work. Quit was cancelled.'],
  closeInProgress: ['宿主正在处理另一项退出请求。', 'The Host is handling another quit request.'],
  reload: ['重启页面', 'Reload page'], lightweight: ['轻量模式', 'Lightweight mode'],
  cancel: ['取消', 'Cancel'], save: ['允许保存', 'Allow save'],
  saveTitle: ['保存文件', 'Save file'], saveQuestion: ['允许保存到所选文件？', 'Allow saving to the selected file?'],
  login: ['登录', 'Sign in'], failure: ['操作未完成，请稍后重试。', 'The operation could not finish. Try again.']
  , smallWorkArea: ['当前显示工作区不足以容纳 1280 × 720 的业务界面。', 'The display work area cannot fit the 1280 × 720 application area.'],
  smallWorkAreaDetail: ['继续打开可能使部分窗口超出工作区。后台任务和托盘可继续运行。', 'Part of the window may extend beyond the work area. Background tasks and the tray can continue running.'],
  continueOpen: ['继续打开', 'Continue opening'], backgroundOnly: ['仅后台运行', 'Run in background'], cancelOpen: ['取消打开', 'Cancel opening']
};
function setLocale(value) {
  if (value !== 'zh-CN' && value !== 'en-US') throw new Error('desktop_locale_invalid');
  locale = value;
}
function text(key) {
  if (!Object.hasOwn(words, key)) throw new Error('desktop_text_unknown');
  return words[key][locale === 'en-US' ? 1 : 0];
}
module.exports = { setLocale, text };
