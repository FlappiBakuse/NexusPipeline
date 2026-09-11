<script setup lang="ts">
import NxpBadge from "../../ui/primitives/NxpBadge.vue";
import NxpCard from "../../ui/primitives/NxpCard.vue";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";

type Translator = (
  key: string,
  args?: Record<string, unknown>,
  fallback?: string,
) => string;

interface GlobalUserCardUser {
  id: string;
  name: string;
  avatarUrl?: string;
  bindingCount?: number;
  bindings?: Array<{ scriptInstanceId: string }>;
  nextRunAt?: string;
}

interface UserBadge {
  pluginName?: string;
  id?: string;
  tone?: string;
  title?: string;
  label?: string;
}

defineProps<{
  user: GlobalUserCardUser;
  badges: UserBadge[];
  nextLabel: string;
  initials: string;
  translate: Translator;
}>();

const emit = defineEmits<{
  upload: [userId: string];
  manage: [user: GlobalUserCardUser];
  globalManage: [user: GlobalUserCardUser];
  remove: [user: GlobalUserCardUser];
}>();
</script>

<template>
  <NxpCard
    as="article"
    unstyled
    class="script-card global-user-card"
    data-testid="global-user-card"
    :data-dnd-id="user.id"
  >
    <span
      class="drag-handle"
      role="button"
      tabindex="0"
      :aria-label="translate('users.global.order_help')"
      :title="translate('common.drag_to_reorder')"
      ><NxpIcon name="grip"
    /></span>
    <button
      class="global-user-avatar-button"
      type="button"
      :aria-label="translate('users.avatar_upload_for_user', { name: user.name })"
      :title="translate('users.avatar_upload')"
      @click.stop="emit('upload', user.id)"
      ><img
        v-if="user.avatarUrl"
        class="global-user-avatar"
        :src="user.avatarUrl"
        alt=""
        loading="lazy"
      /><span
        v-else
        class="global-user-avatar global-user-avatar-fallback"
        >{{ initials }}</span
      ><span class="global-user-avatar-mark" aria-hidden="true">+</span></button
    >
    <div class="script-main global-user-main">
      <div class="script-name-row">
        <strong class="global-user-name">{{ user.name }}</strong>
      </div>
      <div class="meta-line global-user-meta">
        <NxpBadge tone="muted">{{
          translate("users.binding.scripts_count", {
            count: user.bindingCount ?? (user.bindings || []).length,
          })
        }}</NxpBadge
        ><NxpBadge
          v-for="badge in badges"
          :key="(badge.pluginName || '') + '-' + (badge.id || '')"
          :tone="
            badge.tone === 'ok' ||
            badge.tone === 'warn' ||
            badge.tone === 'bad' ||
            badge.tone === 'blue'
              ? badge.tone
              : 'muted'
          "
          data-testid="plugin-user-badge"
          :title="badge.title"
          >{{ badge.label || '' }}</NxpBadge
        ><span
          class="plugin-slot user-plugin-slot"
          data-plugin-slot="users.list.badges"
          data-plugin-anchor="users.list.badges"
          data-plugin-mode="list"
          :data-plugin-primary-id="user.id"
          hidden
        ></span
        ><NxpBadge tone="blue" class="global-user-next-run">{{
          nextLabel
        }}</NxpBadge>
      </div>
    </div>
    <div class="global-user-actions row-actions entity-actions">
      <button
        class="tertiary"
        type="button"
        @click.stop="emit('manage', user)"
      >
        {{ translate("users.user_management_button") }}</button
      ><button
        class="tertiary"
        type="button"
        @click.stop="emit('globalManage', user)"
      >
        {{ translate("users.global.open_action") }}</button
      ><button
        class="danger"
        type="button"
        @click.stop="emit('remove', user)"
      >
        {{ translate("users.delete_user") }}
      </button>
    </div>
  </NxpCard>
</template>
