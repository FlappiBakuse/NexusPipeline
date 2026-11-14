import { computed, onBeforeUnmount, reactive, ref, shallowRef, watch, type Ref } from 'vue';
import type { AppStore } from '../app/store';
import { readStatus, readValue, type LocalField } from './local-secrets';
import { hostSecretSession, inspectHostSecret, writeHostSecret } from './host-secret-session';

interface Owner {
  store: AppStore;
  visible: boolean;
}
interface ReadTicket {
  epoch: number;
  instanceId: string;
  signal: AbortSignal;
}

export function useSettingsSecrets(owner: Owner, notice: Ref<string>) {
  const instanceId = () => owner.store.state.capabilities?.instanceId || '';
  const input = () => {
    const value = reactive({ value: '', mode: 'keep' as 'keep' | 'set' | 'clear', revision: null as number | null });
    Object.defineProperty(value, 'toJSON', { value: () => { throw Error('secret_input_requires_memory'); } });
    return value;
  };
  const fresh = () => ({ secret: input(), token: input() });
  const inputs = shallowRef(fresh());
  const secret = computed({ get: () => inputs.value.secret.value, set: value => { inputs.value.secret.value = value; } });
  const token = computed({ get: () => inputs.value.token.value, set: value => { inputs.value.token.value = value; } });
  function mode(field: LocalField, value: 'keep' | 'set' | 'clear'): void {
    const draft = field === 'control-bearer' ? inputs.value.token : inputs.value.secret;
    if (value === 'keep') draft.revision = null;
    else draft.revision ??= state(field).status?.revision ?? null;
    draft.mode = value;
  }
  const secretMode = computed({ get: () => inputs.value.secret.mode, set: value => mode('proxy-password', value) });
  const tokenMode = computed({ get: () => inputs.value.token.mode, set: value => mode('control-bearer', value) });
  const secretVisible = ref(false),
    tokenVisible = ref(false);
  const secretReading = ref(false),
    tokenReading = ref(false);
  const edits = { 'proxy-password': 0, 'control-bearer': 0 };
  let epoch = 0,
    controller = new AbortController(),
    disposed = false;
  const state = (field: LocalField) => hostSecretSession(owner.store, instanceId(), field);
  const proxy = computed(() => state('proxy-password')),
    bearer = computed(() => state('control-bearer'));
  const ticket = (): ReadTicket => ({ epoch, instanceId: instanceId(), signal: controller.signal });
  const current = (read: ReadTicket): boolean =>
    !disposed && !owner.store.editors.signal.aborted &&
    !!read.instanceId &&
    owner.visible &&
    epoch === read.epoch &&
    instanceId() === read.instanceId;
  const secretRevision = computed(() => inputs.value.secret.revision ?? proxy.value.status?.revision ?? 0),
    tokenRevision = computed(() => inputs.value.token.revision ?? bearer.value.status?.revision ?? 0);
  const secretConfigured = computed(() => !!proxy.value.status?.isConfigured),
    tokenConfigured = computed(() => !!bearer.value.status?.isConfigured);
  const secretReady = computed(() => proxy.value.status !== null),
    tokenReady = computed(() => bearer.value.status !== null);
  const secretReadable = computed(() => !!proxy.value.status?.isReadable),
    tokenReadable = computed(() => !!bearer.value.status?.isReadable);
  const secretUnknownId = computed(() =>
    proxy.value.pending?.state === 'unknown' ? proxy.value.pending.id : ''
  );
  const tokenUnknownId = computed(() =>
    bearer.value.pending?.state === 'unknown' ? bearer.value.pending.id : ''
  );
  const secretBusy = computed(() => !!proxy.value.pending && proxy.value.pending.state !== 'unknown');
  const tokenBusy = computed(() => !!bearer.value.pending && bearer.value.pending.state !== 'unknown');
  const secretError = computed(() => proxy.value.error),
    tokenError = computed(() => bearer.value.error);
  const tokenOperationStatus = computed(() => bearer.value.operationStatus);
  function conceal(): void {
    secret.value = ''; token.value = '';
    secretMode.value = 'keep'; tokenMode.value = 'keep';
    inputs.value.secret.revision = null; inputs.value.token.revision = null;
    secretVisible.value = false;
    tokenVisible.value = false;
  }
  function reset(): void {
    epoch++;
    controller.abort();
    controller = new AbortController();
    conceal();
    secretReading.value = false;
    tokenReading.value = false;
  }
  watch(secretMode, (mode) => {
    if (mode === 'clear') {
      secret.value = '';
      secretVisible.value = false;
    }
  });
  watch(tokenMode, (mode) => {
    if (mode === 'clear') {
      token.value = '';
      tokenVisible.value = false;
    }
  });
  const ended = () => reset();
  owner.store.editors.signal.addEventListener('abort', ended, { once: true });
  onBeforeUnmount(() => {
    owner.store.editors.signal.removeEventListener('abort', ended);
    disposed = true;
    reset();
  });

  async function load(field: LocalField): Promise<void> {
    const read = ticket();
    if (!current(read) || !owner.store.can('secrets.status')) return;
    const session = state(field);
    try {
      const value = await readStatus(owner.store.client, field, read.signal);
      if (current(read) && value.revision >= (session.status?.revision || 0)) session.status = value;
    } catch (reason) {
      if (current(read)) session.error = reason instanceof Error ? reason.message : '秘密状态读取失败';
    }
  }
  const loadSecret = () => load('proxy-password'),
    loadToken = () => load('control-bearer');
  async function reveal(field: LocalField): Promise<void> {
    const read = ticket(),
      session = state(field),
      isToken = field === 'control-bearer';
    const draft = isToken ? inputs.value.token : inputs.value.secret;
    if (draft.mode === 'set' && draft.value) {
      if (isToken) tokenVisible.value = true; else secretVisible.value = true;
      return;
    }
    const edit = edits[field];
    const reading = isToken ? tokenReading : secretReading,
      visible = isToken ? tokenVisible : secretVisible;
    if (
      !current(read) ||
      session.pending ||
      reading.value ||
      visible.value ||
      !session.status?.isConfigured ||
      !session.status.isReadable ||
      !owner.store.can('secrets.read')
    )
      return;
    reading.value = true;
    try {
      const value = await readValue(owner.store.client, field, read.signal);
      if (!current(read) || edits[field] !== edit || session.pending) return;
      if (isToken) {
        token.value = value;
        tokenMode.value = 'keep';
        tokenVisible.value = true;
      } else {
        secret.value = value;
        secretMode.value = 'keep';
        secretVisible.value = true;
      }
    } catch (reason) {
      if (current(read)) session.error = reason instanceof Error ? reason.message : '秘密状态读取失败';
    } finally {
      if (current(read)) reading.value = false;
    }
  }
  const revealSecret = () => reveal('proxy-password'),
    revealToken = () => reveal('control-bearer');
  function editSecret(value: string): void {
    if (!owner.visible || proxy.value.pending) return;
    edits['proxy-password']++;
    secret.value = value;
    secretVisible.value = true;
    secretMode.value = value ? 'set' : 'keep';
  }
  function editToken(value: string): void {
    if (!owner.visible || bearer.value.pending) return;
    edits['control-bearer']++;
    token.value = value;
    tokenVisible.value = true;
    tokenMode.value = value ? 'set' : 'keep';
  }
  async function save(field: LocalField): Promise<void> {
    const read = ticket(),
      isToken = field === 'control-bearer';
    const mode = isToken ? tokenMode.value : secretMode.value,
      value = isToken ? token.value : secret.value;
    if (
      !current(read) ||
      !state(field).status ||
      state(field).pending ||
      mode === 'keep' ||
      !owner.store.can('secrets.write')
    )
      return;
    if (mode === 'set' && !value) {
      state(field).error = isToken ? '请输入控制令牌。' : '请输入代理密码。';
      return;
    }
    const result = await writeHostSecret(
      owner.store,
      read.instanceId,
      field,
      (isToken ? tokenRevision : secretRevision).value,
      mode,
      value
    );
    if (current(read)) {
      if (isToken) {
        token.value = '';
        tokenVisible.value = false;
        tokenMode.value = 'keep';
      } else {
        secret.value = '';
        secretVisible.value = false;
        secretMode.value = 'keep';
      }
      if (result) notice.value = isToken ? '控制令牌已更新。' : '代理密码状态已保存。';
    }
  }
  const saveSecret = () => save('proxy-password'),
    saveToken = () => save('control-bearer');
  function generateToken(): void {
    if (
      !owner.visible ||
      !instanceId() ||
      !state('control-bearer').status ||
      state('control-bearer').pending ||
      tokenReading.value
    )
      return;
    const bytes = crypto.getRandomValues(new Uint8Array(32));
    edits['control-bearer']++;
    token.value = btoa(String.fromCharCode(...bytes))
      .replace(/\+/g, '-')
      .replace(/\//g, '_')
      .replace(/=+$/, '');
    tokenMode.value = 'set';
    tokenVisible.value = true;
  }
  async function inspect(field: LocalField): Promise<void> {
    const read = ticket();
    if (!current(read) || !owner.store.can('secrets.status')) return;
    await inspectHostSecret(owner.store, read.instanceId, field, read.signal);
  }
  const inspectTokenOperation = () => inspect('control-bearer'),
    inspectSecretOperation = () => inspect('proxy-password');
  watch(
    () => [owner.visible, instanceId()] as const,
    ([visible, identity]) => {
      reset();
      inputs.value = fresh();
      if (visible && identity) {
        void loadSecret();
        void loadToken();
      }
    },
    { immediate: true }
  );
  return {
    secret,
    secretMode,
    secretReady,
    tokenReady,
    secretRevision,
    secretConfigured,
    secretReadable,
    secretBusy,
    secretError,
    secretUnknownId,
    secretVisible,
    token,
    tokenMode,
    tokenRevision,
    tokenConfigured,
    tokenReadable,
    tokenBusy,
    tokenError,
    tokenVisible,
    tokenUnknownId,
    tokenOperationStatus,
    loadSecret,
    saveSecret,
    revealSecret,
    loadToken,
    revealToken,
    generateToken,
    saveToken,
    inspectTokenOperation,
    inspectSecretOperation,
    editSecret,
    editToken,
    cancelSecret: () => { if (!proxy.value.pending) { secret.value = ''; secretMode.value = 'keep'; secretVisible.value = false; } },
    cancelToken: () => { if (!bearer.value.pending) { token.value = ''; tokenMode.value = 'keep'; tokenVisible.value = false; } }
  };
}
