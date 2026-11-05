<script setup lang="ts">
import { computed } from 'vue';
import { marked } from 'marked';
import DOMPurify from 'dompurify';
import { t } from '../app/locale';
const props = defineProps<{ text: string }>();
const html = computed(() => {
  const sanitized = DOMPurify.sanitize(marked.parse(props.text, { async: false }), {
    USE_PROFILES: { html: true },
    FORBID_TAGS: ['style', 'form', 'input', 'button', 'textarea', 'select'],
    FORBID_ATTR: ['style', 'id', 'name']
  });
  const document = new DOMParser().parseFromString(sanitized, 'text/html');
  for (const content of document.querySelectorAll('pre,table')) {
    const area = document.createElement('nexus-scroll-area');
    area.className = 'markdown-scroll';
    area.setAttribute('direction', 'horizontal');
    area.setAttribute('label', t(content.localName === 'pre' ? '代码' : '表格'));
    content.replaceWith(area);
    area.append(content);
  }
  return document.body.innerHTML;
});
function openLink(event: MouseEvent): void {
  const link = (event.target as Element).closest<HTMLAnchorElement>('a');
  if (!link) return;
  const href = link.getAttribute('href') || '';
  if (!/^https?:\/\//i.test(href)) {
    event.preventDefault();
    return;
  }
  link.target = '_blank';
  link.rel = 'noopener noreferrer';
}
</script>
<template><div class="markdown-document" @click="openLink" v-html="html" /></template>
<style scoped>
.markdown-document {
  font-size: 13px;
  line-height: 1.8;
  overflow-wrap: anywhere;
}
.markdown-document :deep(h1),
.markdown-document :deep(h2),
.markdown-document :deep(h3) {
  margin: 20px 0 8px;
  font-size: 16px;
  line-height: 1.45;
  font-weight: 700;
}
.markdown-document :deep(> :first-child) {
  margin-top: 0;
}
.markdown-document :deep(p) {
  margin: 0 0 12px;
}
.markdown-document :deep(ol),
.markdown-document :deep(ul) {
  padding-left: 22px;
  margin: 8px 0 16px;
}
.markdown-document :deep(li) {
  margin: 3px 0;
}
.markdown-document :deep(code) {
  padding: 2px 4px;
  border-radius: 4px;
  background: var(--accent-soft);
  color: var(--accent);
  font-size: 11px;
}
.markdown-document :deep(.markdown-scroll) {
  max-width: 100%;
}
.markdown-document :deep(pre) {
  margin: 0;
  padding: 12px;
  border-radius: 8px;
  background: var(--content-card-soft);
}
.markdown-document :deep(pre code) {
  padding: 0;
  background: transparent;
  color: inherit;
}
.markdown-document :deep(img) {
  max-width: 100%;
  height: auto;
}
.markdown-document :deep(blockquote) {
  margin: 12px 0;
  padding-left: 14px;
  border-left: 3px solid var(--border-strong);
  color: var(--muted);
}
.markdown-document :deep(table) {
  width: 100%;
  border-collapse: collapse;
}
.markdown-document :deep(th),
.markdown-document :deep(td) {
  padding: 7px 10px;
  border: 1px solid var(--border);
  text-align: left;
}
</style>
