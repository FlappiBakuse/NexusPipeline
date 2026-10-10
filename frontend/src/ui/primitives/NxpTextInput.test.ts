import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import NxpTextInput from './NxpTextInput.vue';

describe('controlled credential disclosure', () => {
  it('requests disclosure without changing an unread secret or its input type', async () => {
    const wrapper = mount(NxpTextInput, { props: {
      type: 'password', modelValue: '', secretConfigured: true, readonly: true,
      showPasswordToggle: true, passwordVisibilityControlled: true,
    } });
    await wrapper.get('button[aria-label="Show password"]').trigger('click');
    expect(wrapper.emitted('password-visibility-request')).toEqual([[true]]);
    expect(wrapper.get('input').element.value).toBe('');
    expect(wrapper.get('input').element.type).toBe('password');
    expect(wrapper.emitted('update:modelValue')).toBeUndefined();
    await wrapper.setProps({ modelValue: 'synthetic-secret', passwordVisible: true, readonly: false });
    expect(wrapper.get('input').element.type).toBe('text');
    await wrapper.get('button[aria-label="Hide password"]').trigger('click');
    expect(wrapper.emitted('password-visibility-request')).toEqual([[true], [false]]);
    await wrapper.setProps({ modelValue: '', passwordVisible: false, readonly: true });
    expect(wrapper.get('input').element.value).toBe('');
    expect(wrapper.get('input').element.type).toBe('password');
    wrapper.unmount();
  });

  it('preserves ordinary password toggling and protects disabled or read-only fields', async () => {
    const wrapper = mount(NxpTextInput, { props: { type: 'password', modelValue: 'manual-secret', showPasswordToggle: true } });
    await wrapper.get('button[aria-label="Show password"]').trigger('click');
    expect(wrapper.get('input').element.type).toBe('text');
    expect(wrapper.emitted('password-visibility-request')).toBeUndefined();
    await wrapper.setProps({ readonly: true });
    expect(wrapper.find('button').exists()).toBe(false);
    expect(wrapper.get('input').element.type).toBe('password');
    await wrapper.setProps({ passwordVisibilityControlled: true, secretConfigured: true, disabled: true });
    expect(wrapper.find('button').exists()).toBe(false);
    wrapper.unmount();
  });
});
