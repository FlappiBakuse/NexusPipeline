import { createRouter, createWebHashHistory, type RouteRecordRaw } from "vue-router";

/**
 * 宿主页面路由表。插件 route 由 `PluginRouteHost` 承载，负责 route token、
 * 插件生命周期与无效路由回退。页面组件按需加载，页面自身渲染 `#view` 主内容区。
 */
const developmentRoutes: RouteRecordRaw[] = import.meta.env.DEV
  ? [{ path: "/ui-lab", component: () => import("./ui/UiLabPage.vue") }]
  : [];

export const routes: RouteRecordRaw[] = [
  { path: "/", redirect: "/dashboard" },
  { path: "/dashboard", component: () => import("./features/dashboard/DashboardPage.vue") },
  { path: "/users", component: () => import("./features/users/UsersPage.vue") },
  { path: "/scripts", component: () => import("./features/scripts/ScriptsPage.vue") },
  { path: "/queues", component: () => import("./features/queues/QueuesPage.vue") },
  { path: "/dispatch", component: () => import("./features/dispatch/DispatchPage.vue") },
  { path: "/history", component: () => import("./features/history/HistoryPage.vue") },
  { path: "/plugins", component: () => import("./features/plugins/PluginsPage.vue") },
  { path: "/settings", component: () => import("./features/settings/SettingsPage.vue") },
  ...developmentRoutes,
  { path: "/plugin/:pathMatch(.*)*", component: () => import("./app/PluginRouteHost.vue") },
  { path: "/:pathMatch(.*)*", component: { template: "<span />" } },
];

export const router = createRouter({
  history: createWebHashHistory(),
  routes,
  scrollBehavior: () => ({ top: 0 }),
});
