'use strict';
let locale = 'zh-CN';
const words = {
  show: ['显示', 'Show'], hide: ['隐藏', 'Hide'], quit: ['退出 NexusPipeline', 'Quit NexusPipeline'],
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
