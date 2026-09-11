<script setup lang="ts">
import { ref } from "vue";
import NxpBadge from "./primitives/NxpBadge.vue";
import NxpButton from "./primitives/NxpButton.vue";
import NxpCard from "./primitives/NxpCard.vue";
import NxpColorPicker from "./primitives/NxpColorPicker.vue";
import NxpEmptyState from "./primitives/NxpEmptyState.vue";
import NxpField from "./primitives/NxpField.vue";
import NxpFilePicker from "./primitives/NxpFilePicker.vue";
import NxpIcon from "./primitives/NxpIcon.vue";
import NxpLoadingState from "./composites/NxpLoadingState.vue";
import NxpModal from "./primitives/NxpModal.vue";
import NxpNumberInput from "./primitives/NxpNumberInput.vue";
import NxpPager from "./primitives/NxpPager.vue";
import NxpPathPicker from "./primitives/NxpPathPicker.vue";
import NxpRange from "./primitives/NxpRange.vue";
import NxpSelect from "./primitives/NxpSelect.vue";
import NxpSpinner from "./primitives/NxpSpinner.vue";
import NxpSwitch from "./primitives/NxpSwitch.vue";
import NxpSwitchSetting from "./composites/NxpSwitchSetting.vue";
import NxpTextArea from "./primitives/NxpTextArea.vue";
import NxpTextInput from "./primitives/NxpTextInput.vue";
import NxpTimePicker from "./primitives/NxpTimePicker.vue";
import NxpToast from "./primitives/NxpToast.vue";
import NxpTooltip from "./primitives/NxpTooltip.vue";

const text = ref("");
const select = ref("scheduled");
const number = ref(3);
const range = ref(60);
const switchValue = ref(true);
const time = ref("05:30");
const path = ref("");
const color = ref("#62a0ff");
const page = ref(1);
const modalOpen = ref(false);
const toastVisible = ref(true);
</script>

<template>
  <main class="ui-lab view-root">
    <header class="page-head">
      <div class="page-head-copy">
        <div class="eyebrow">Nexus UI</div>
        <h2>组件状态实验室</h2>
        <p class="page-kicker">统一检查常用元件的状态、辅助提示和响应式表现。</p>
      </div>
      <div class="page-head-actions"><NxpButton class="tertiary" @click="toastVisible = !toastVisible">切换提示</NxpButton></div>
    </header>

    <div class="ui-lab-grid">
      <NxpCard>
        <h3>按钮与状态</h3>
        <div class="ui-lab-row"><NxpButton class="primary">主要操作</NxpButton><NxpButton class="tertiary">次要操作</NxpButton><NxpButton class="danger">危险操作</NxpButton><NxpButton disabled>禁用</NxpButton></div>
        <div class="ui-lab-row"><NxpBadge tone="ok">已启用</NxpBadge><NxpBadge tone="blue">运行中</NxpBadge><NxpBadge tone="warn">待处理</NxpBadge><NxpBadge tone="bad">异常</NxpBadge><NxpBadge tone="muted">未设置</NxpBadge></div>
        <div class="ui-lab-row"><NxpIcon name="check" /><NxpIcon name="chevronDown" /><NxpSpinner /><NxpTooltip text="延迟显示的局部提示"><button class="tertiary" type="button">悬停提示</button></NxpTooltip></div>
      </NxpCard>

      <NxpCard>
        <h3>表单元件</h3>
        <div class="ui-lab-form">
          <NxpField label="文本输入" description="常驻说明文字。" help="辅助提示不会占用默认布局高度。"><NxpTextInput v-model="text" placeholder="输入较长的文本内容" /></NxpField>
          <NxpField label="文本域" error="示例错误信息"><NxpTextArea v-model="text" placeholder="多行文本" :rows="3" /></NxpField>
          <NxpField label="选择框"><NxpSelect id="ui-lab-select" v-model="select" :options="[{ value: 'none', label: '不运行' }, { value: 'scheduled', label: '定时运行' }, { value: 'startup', label: '启动时运行' }]" /></NxpField>
          <NxpField label="数字输入" help="-1 表示不限制。"><NxpNumberInput v-model="number" :min="-1" :max="10" placeholder="-1" /></NxpField>
          <NxpField label="滑块"><NxpRange v-model="range" :min="0" :max="100" /></NxpField>
          <NxpField label="时间"><NxpTimePicker v-model="time" /></NxpField>
        </div>
      </NxpCard>

      <NxpCard>
        <h3>复合设置</h3>
        <NxpSwitchSetting v-model="switchValue" label="同步通用设置" description="开启后覆盖脚本实例中的通用设置。" help="这是复合开关的辅助提示。" />
        <NxpSwitch :model-value="switchValue" aria-label="原始开关" @update:model-value="switchValue = $event" />
        <div class="ui-lab-form"><NxpField label="路径" help="支持脚本文件。"><NxpPathPicker v-model="path" kind="file" filter="脚本文件|*.exe;*.bat|所有文件|*.*" placeholder="选择脚本文件" help="支持脚本文件。" /></NxpField><NxpField label="文件选择"><NxpFilePicker label="选择文件" /></NxpField><NxpField label="颜色"><NxpColorPicker v-model="color" /></NxpField></div>
      </NxpCard>

      <NxpCard>
        <h3>空、加载和提示</h3>
        <NxpLoadingState title="正在加载数据" description="请稍候…" />
        <NxpEmptyState title="暂无数据" description="当前没有可显示的内容。" />
        <NxpToast :visible="toastVisible" message="这是一个可关闭的提示消息。" tone="success" />
        <NxpPager v-model:page="page" :total-pages="4" :total="16" />
      </NxpCard>
    </div>

    <div class="ui-lab-footer"><button class="primary" type="button" @click="modalOpen = true">打开模态框状态</button></div>
    <NxpModal :open="modalOpen" title="组件模态框" @close="modalOpen = false"><p>模态框、遮罩、焦点和 Escape 行为在这里集中检查。</p><template #footer><button class="ghost" type="button" @click="modalOpen = false">取消</button><button class="primary" type="button" @click="modalOpen = false">完成</button></template></NxpModal>
  </main>
</template>

<style scoped>
.ui-lab { min-width: 0; }
.ui-lab-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-4); }
.ui-lab h3 { margin: 0 0 var(--space-3); }
.ui-lab-row { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); margin-top: var(--space-3); }
.ui-lab-form { display: grid; gap: var(--space-3); }
.ui-lab-footer { display: flex; justify-content: flex-end; margin-top: var(--space-4); }
@media (max-width: 820px) { .ui-lab-grid { grid-template-columns: 1fr; } }
</style>
