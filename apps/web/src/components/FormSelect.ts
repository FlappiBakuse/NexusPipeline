import { defineComponent, h, ref, type VNode, type PropType } from 'vue';
import { useFormLabel } from './form-label';

type Choice = { value: unknown; label: string; disabled: boolean };
function text(node: VNode | string): string {
  if (typeof node === 'string') return node;
  return typeof node.children === 'string'
    ? node.children
    : Array.isArray(node.children)
      ? node.children.map((child) => text(child as VNode)).join('')
      : '';
}
function choices(nodes: readonly VNode[], disabled = false): Choice[] {
  return nodes.flatMap((node) =>
    node.type === 'option'
      ? [
          {
            value: node.props?.value ?? text(node),
            label: text(node),
            disabled: disabled || node.props?.disabled === '' || node.props?.disabled === true
          }
        ]
      : Array.isArray(node.children)
        ? choices(node.children as VNode[], disabled || node.props?.disabled === true)
        : []
  );
}

export default defineComponent({
  name: 'FormSelect',
  inheritAttrs: false,
  props: {
    modelValue: null,
    value: null,
    disabled: Boolean,
    modelModifiers: { type: Object as PropType<{ number?: boolean }>, default: () => ({}) }
  },
  emits: ['update:modelValue', 'change'],
  setup(props, { attrs, slots, emit }) {
    const root = ref<HTMLElement>();
    const label = useFormLabel(root, attrs);
    return () => {
      const items = choices(slots.default?.() || []);
      const value = props.modelValue === undefined ? props.value : props.modelValue;
      const selected = items.findIndex((item) => String(item.value) === String(value ?? ''));
      return h(
        'span',
        {
          ...attrs,
          ref: root,
          class: ['form-select', attrs.class],
          inert: props.disabled,
          'aria-disabled': String(props.disabled),
          onChange: (event: Event) => emit('change', event)
        },
        [
          h('nexus-select', {
            value: selected < 0 ? '' : String(selected),
            'aria-label': label.value,
            searchable: items.length > 80,
            options: items.map((item, index) => ({
              id: String(index),
              label: item.label,
              disabled: item.disabled
            })),
            'onNexus-change': (event: CustomEvent<{ value: string }>) => {
              event.stopPropagation();
              const item = items[Number(event.detail.value)];
              if (!item || props.disabled) return;
              const next = props.modelModifiers.number ? Number(item.value) : item.value;
              emit('update:modelValue', next);
              Object.assign(root.value!, { value: String(item.value) });
              root.value!.dispatchEvent(new Event('change', { bubbles: true }));
            }
          })
        ]
      );
    };
  }
});
