import { createRouter, createWebHashHistory } from "vue-router";

export const router = createRouter({
  history: createWebHashHistory(),
  routes: [{ path: "/:pathMatch(.*)*", component: { template: "<span />" } }],
  scrollBehavior: () => ({ top: 0 }),
});
