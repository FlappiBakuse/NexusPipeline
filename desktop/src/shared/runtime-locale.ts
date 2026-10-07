export function runtimeLocale(preferred: string): 'en-US' | 'zh-CN' {
  const language = preferred.replace(/_/g, '-').toLowerCase();
  return /^zh(?:-|$)/.test(language) ? 'zh-CN' : 'en-US';
}
