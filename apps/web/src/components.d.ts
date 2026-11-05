export {};
declare module 'vue' {
  export interface GlobalComponents {
    FormSelect: (typeof import('./components/FormSelect'))['default'];
    FormDate: (typeof import('./components/FormDate.vue'))['default'];
    FormNumber: (typeof import('./components/FormNumber.vue'))['default'];
    PageActions: (typeof import('./components/PageActions.vue'))['default'];
  }
}
