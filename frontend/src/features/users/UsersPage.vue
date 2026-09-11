<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { isAbortError } from "@legacy/core/api.js";
import { renderPluginSlot } from "@legacy/core/plugin-slots.js";
import { disposePluginSlot } from "@legacy/core/plugin-runtime.js";
import { t } from "@legacy/core/i18n.js";
import { setTopbarTitle, toast } from "@legacy/core/ui.js";
import NxpButton from "../../ui/primitives/NxpButton.vue";
import NxpEmptyState from "../../ui/primitives/NxpEmptyState.vue";
import NxpIcon from "../../ui/primitives/NxpIcon.vue";
import NxpModal from "../../ui/primitives/NxpModal.vue";
import { vSortable } from "../../ui/sortable";
import GlobalUserCard from "./GlobalUserCard.vue";
import ConfigEditFlow from "./components/ConfigEditFlow.vue";
import GlobalManagementModal from "./components/GlobalManagementModal.vue";
import UserManagementModal from "./components/UserManagementModal.vue";
import { findRestorableEditSession } from "./utils/editSession";
import { listEditSessions, listUserBadges, listUsers, listScripts, getStatus, createUser as createUserRequest, deleteUser, reorderUsers as reorderUsersRequest, uploadAvatar as uploadAvatarRequest } from "./services/usersApi";
import type { Badge, Plugin, Script, User, UserBadges } from "./utils/userTypes";

const users = ref<User[]>([]);
const scripts = ref<Script[]>([]);
const plugins = ref<Plugin[]>([]);
const badgesByUser = ref(new Map<string, Badge[]>());
const countdownByUser = ref<Record<string, string>>({});
const loading = ref(true);
const error = ref("");
const newUserOpen = ref(false);
const newUserName = ref("");
const globalUserId = ref("");
const globalUserName = ref("");
const managedUserId = ref("");
const managedUser = ref<User | null>(null);
const deleteTarget = ref<User | null>(null);
const deleteName = ref("");
const root = ref<HTMLElement | null>(null);
const configFlow = ref<InstanceType<typeof ConfigEditFlow> | null>(null);
const userManager = ref<InstanceType<typeof UserManagementModal> | null>(null);
let disposed = false;
let countdownTimer: ReturnType<typeof setInterval> | null = null;
let restoreAttempted = false;

const sortedUsers = computed(() =>
  users.value.slice().sort((a, b) => (a.index ?? 0) - (b.index ?? 0)),
);

const errorText = (reason: unknown) =>
  reason instanceof Error ? reason.message : String(reason);

function initials(name: string) {
  const chars = Array.from(String(name || t("common.user")).trim());
  if (!chars.length) return t("users.user_initial");
  return /[\u3400-\u9fff]/.test(chars[0])
    ? chars[0]
    : chars.slice(0, 2).join("").toUpperCase();
}

function remainingLabel(value: string) {
  if (!value) return t("users.no_scheduled_tasks");
  const target = new Date(value).getTime();
  if (!Number.isFinite(target)) return t("users.no_scheduled_tasks");
  const remaining = target - Date.now();
  if (remaining <= 0) return t("common.about_to_run");
  const seconds = Math.floor(remaining / 1000);
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const clock = `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
  return days
    ? t("users.schedule.runs_in_days", { days, clock })
    : t("users.schedule.runs_in", { clock });
}

function refreshCountdowns() {
  const next: Record<string, string> = {};
  for (const user of users.value)
    next[user.id] = remainingLabel(user.nextRunAt || "");
  countdownByUser.value = next;
}
async function reorderUsers(ids: string[]) {
  const byId = new Map(users.value.map(user => [user.id, user]));
  const next = ids.map(id => byId.get(id)).filter((user): user is User => Boolean(user));
  if (next.length !== users.value.length || next.every((user, index) => user.id === sortedUsers.value[index]?.id)) return;
  users.value = next.map((item, index) => ({ ...item, index }));
  try {
    await reorderUsersRequest(next.map(item => item.id));
    toast(t("users.user_order_saved"));
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
    await load();
  }
}

async function load() {
  loading.value = true;
  error.value = "";
  try {
    const [userData, scriptData, status, badgeData] = (await Promise.all([
      listUsers(),
      listScripts(),
      getStatus(),
      listUserBadges(),
    ])) as [User[], Script[], { plugins?: Plugin[] }, UserBadges[]];
    if (disposed) return;
    users.value = Array.isArray(userData) ? userData : [];
    scripts.value = Array.isArray(scriptData) ? scriptData : [];
    plugins.value = Array.isArray(status?.plugins) ? status.plugins : [];
    badgesByUser.value = new Map(
      (Array.isArray(badgeData) ? badgeData : []).map((item) => [
        String(item?.userId || ""),
        Array.isArray(item?.badges) ? item.badges : [],
      ]),
    );
    refreshCountdowns();
    // 插件 slot 属于页面增强，不能阻塞用户列表首屏；老服务端或单个插件异常时，
    // 列表仍必须先可见，slot 在后台完成渲染即可。
    void paintListSlots();
  } catch (reason) {
    if (!disposed && !isAbortError(reason)) error.value = errorText(reason);
  } finally {
    if (!disposed) loading.value = false;
  }
  if (!disposed && !error.value) void restoreEditSessionCard();
}

function modalOpen() {
  return Boolean(
    newUserOpen.value ||
      globalUserId.value ||
      managedUserId.value ||
      deleteTarget.value ||
      configFlow.value?.isOpen,
  );
}

/** 刷新或服务重启后，把用户带回仍在进行的锁定配置编辑事务。恢复失败保持页面可用。 */
async function restoreEditSessionCard() {
  if (restoreAttempted || modalOpen()) return;
  restoreAttempted = true;
  try {
    const sessions = await listEditSessions();
    if (disposed || modalOpen()) return;
    const matched = findRestorableEditSession(sessions, users.value, scripts.value);
    if (matched) await configFlow.value?.restore(matched);
  } catch {
    // 恢复失败时保留页面，用户可从绑定卡片重新进入配置编辑。
  }
}

function openNewUser() {
  newUserName.value = "";
  newUserOpen.value = true;
}

function closeNewUser() {
  newUserOpen.value = false;
}

async function createUser() {
  const name = newUserName.value.trim();
  if (!name) {
    toast(t("users.validation.username_required"), "error");
    return;
  }
  if (new TextEncoder().encode(name).length > 64) {
    toast(t("users.validation.username_length", { bytes: 64 }), "error");
    return;
  }
  if (users.value.some(user => user.name.toLocaleLowerCase() === name.toLocaleLowerCase())) {
    toast(t("users.validation.username_duplicate"), "error");
    return;
  }
  try {
    await createUserRequest(name);
    closeNewUser();
    toast(t("users.user_created"));
    await load();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

function openGlobalManagement(user: User) {
  globalUserId.value = user.id;
  globalUserName.value = user.name;
}

function closeGlobalManagement() {
  globalUserId.value = "";
  globalUserName.value = "";
}

function openUserManagement(user: User) {
  managedUser.value = user;
  managedUserId.value = user.id;
}

function closeUserManagement() {
  managedUser.value = null;
  managedUserId.value = "";
}

async function handleOpenConfig(details: { userId: string; scriptId: string; userName: string; scriptName: string; freshAvailable: boolean }) {
  await configFlow.value?.open(details, details.freshAvailable);
}

async function refreshUserDraft(userId: string) {
  await load();
  if (managedUserId.value === userId) await userManager.value?.refresh();
}

/** 列表卡片头像上传：选择图片后写入用户并对齐用户管理与列表两边草稿。 */
function uploadAvatar(userId: string) {
  const input = document.createElement("input");
  input.type = "file";
  input.accept = "image/png,image/jpeg,image/webp";
  input.addEventListener("change", () => {
    const file = input.files?.[0];
    if (!file) return;
    if (!["image/png", "image/jpeg", "image/webp"].includes(file.type)) {
      toast(t("users.validation.avatar_type"), "error");
      return;
    }
    if (file.size > 5 * 1024 * 1024) {
      toast(t("users.validation.avatar_size"), "error");
      return;
    }
    const reader = new FileReader();
    reader.onload = async () => {
      try {
        const dataUrl = String(reader.result || "");
        await uploadAvatarRequest(userId, file.type, dataUrl.split(",", 2)[1] || "");
        toast(t("users.avatar_updated"));
        await refreshUserDraft(userId);
      } catch (reason) {
        if (!isAbortError(reason)) toast(errorText(reason), "error");
      }
    };
    reader.readAsDataURL(file);
  }, { once: true });
  input.click();
}

function askDelete(user: User) {
  deleteTarget.value = user;
  deleteName.value = "";
}
function closeDelete() {
  deleteTarget.value = null;
  deleteName.value = "";
}
async function confirmDelete() {
  const user = deleteTarget.value;
  if (!user || deleteName.value !== user.name) {
    toast(t("users.confirm.username_help"), "error");
    return;
  }
  try {
    await deleteUser(user.id, deleteName.value);
    closeDelete();
    toast(t("users.deleted_user_value", { name: user.name }));
    await load();
  } catch (reason) {
    if (!isAbortError(reason)) toast(errorText(reason), "error");
  }
}

async function paintListSlots() {
  const slots =
    root.value?.querySelectorAll<HTMLElement>(
      '[data-plugin-slot="users.list.badges"]',
    ) || [];
  for (const slot of slots)
    await renderPluginSlot(slot, "users.list.badges", {
      mode: "list",
      primaryId: slot.dataset.pluginPrimaryId || "",
    });
}

onMounted(() => {
  disposed = false;
  setTopbarTitle(t("users.user_management"));
  countdownTimer = setInterval(refreshCountdowns, 1000);
  void load();
});

onBeforeUnmount(() => {
  disposed = true;
  if (countdownTimer) clearInterval(countdownTimer);
  const slots =
    root.value?.querySelectorAll<HTMLElement>("[data-plugin-slot]") || [];
  for (const slot of slots) void disposePluginSlot(slot);
});
</script>

<template>
  <main
    id="view"
    ref="root"
    class="view-root users-page"
    data-testid="main-view"
  >
    <NxpEmptyState v-if="loading" :title="t('common.loading')" />
    <NxpEmptyState
      v-else-if="error"
      :title="t('users.error.load')"
      :description="error"
      tone="danger"
    />
    <template v-else>
      <header class="page-head">
        <div class="page-head-copy">
          <div class="eyebrow">{{ t("users.account_management") }}</div>
          <h2>{{ t("users.user_management") }}</h2>
          <p class="page-kicker">{{ t("users.page.help") }}</p>
        </div>
        <div class="page-head-actions">
          <NxpButton
            class="primary"
            data-testid="open-global-user-modal"
            @click="openNewUser"
            >{{ t("users.add_user") }}</NxpButton
          >
        </div>
      </header>
      <NxpEmptyState
        v-if="!sortedUsers.length"
        :title="t('users.no_users_yet')"
        :description="t('users.page.empty_help')"
      />
      <section v-else class="card list-surface">
        <TransitionGroup v-sortable="{ onDrop: reorderUsers }" name="nxp-card" tag="div" class="script-grid global-user-list">
          <GlobalUserCard
            v-for="user in sortedUsers"
            :key="user.id"
            :user="user"
            :badges="badgesByUser.get(user.id) || []"
            :next-label="countdownByUser[user.id] || remainingLabel(user.nextRunAt || '')"
            :initials="initials(user.name)"
            :translate="t"
            @upload="uploadAvatar"
            @manage="openUserManagement"
            @global-manage="openGlobalManagement"
            @remove="askDelete"
          />
        </TransitionGroup>
      </section>
    </template>

    <NxpModal
      :open="newUserOpen"
      :closeable="false"
      :locked="true"
      :aria-label="t('users.add_user')"
      panel-class="secondary-surface"
      data-locked
    >
      <template #header>
        <div><h3 class="modal-title">{{ t("users.add_user") }}</h3></div>
        <button
          class="icon-button modal-close"
          type="button"
          :aria-label="t('common.close')"
          @click.stop="closeNewUser"
        >
          <NxpIcon name="close" />
        </button>
      </template>
      <div class="field">
        <label class="field-label" for="gu-name">{{
          t("users.user_name")
        }} <span class="req">*</span></label
        ><input id="gu-name" v-model="newUserName" type="text" />
        <span class="muted">{{ t("users.username_case_insensitive") }}</span>
      </div>
      <template #footer>
        <button class="ghost" type="button" @click.stop="closeNewUser">
          {{ t("common.cancel") }}</button
        ><button
          class="primary"
          type="button"
          data-testid="save-global-user"
          @click.stop="createUser"
        >
          {{ t("common.save") }}
        </button>
      </template>
    </NxpModal>
    <GlobalManagementModal
      v-if="globalUserId"
      :user-id="globalUserId"
      :user-name="globalUserName"
      @close="closeGlobalManagement"
      @saved="load"
    />
    <UserManagementModal
      v-if="managedUser"
      ref="userManager"
      :user="managedUser"
      :scripts="scripts"
      :plugins="plugins"
      :users="users"
      @close="closeUserManagement"
      @saved="load"
      @open-config="handleOpenConfig"
    />
    <ConfigEditFlow ref="configFlow" @changed="refreshUserDraft" />
    <NxpModal
      :open="Boolean(deleteTarget)"
      :title="t('users.delete_user')"
      panel-class="secondary-surface"
      :close-label="t('common.close', {}, 'Close')"
      @close="closeDelete"
    >
      <p v-if="deleteTarget" class="modal-copy">
        {{ t("users.confirm.delete", { name: deleteTarget.name }) }}
      </p>
      <div class="field">
        <label class="field-label" for="gu-delete-name">{{
          t("users.confirm_username")
        }}</label
        ><input id="gu-delete-name" v-model="deleteName" type="text" />
      </div>
      <template #footer>
        <NxpButton class="ghost" type="button" @click="closeDelete">{{
          t("common.cancel")
        }}</NxpButton>
        <NxpButton
          class="danger solid"
          type="button"
          data-testid="confirm-delete-global-user"
          @click="confirmDelete"
          >{{ t("common.confirm_deletion") }}</NxpButton
        >
      </template>
    </NxpModal>
  </main>
</template>

<style scoped>
.users-page .global-user-list {
  min-width: 0;
}
.users-page .global-user-avatar {
  flex: 0 0 auto;
}
.users-page .global-user-main {
  min-width: 0;
}
.users-page .global-user-next-run {
  white-space: nowrap;
}
.users-page .plugin-contribution-fields {
  display: grid;
  gap: var(--space-3);
}
.users-page .plugin-field .muted {
  display: block;
  margin-top: var(--space-1);
}
</style>
