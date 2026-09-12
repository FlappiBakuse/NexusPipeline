import { createApp, h, nextTick, ref } from "vue";
import { afterEach, describe, expect, it } from "vitest";
import { registerNexusElements } from "./register";

/**
 * 公开元素在插件 Vue 模板里以自定义元素形式使用（`isCustomElement`）：元素自身会被宿主注册的
 * Vue Custom Element 接管渲染，父级应用的更新必须保留元素内部结构。
 */
describe("public element slot contract", () => {
  const apps: Array<{ unmount: () => void }> = [];

  function mount(render: () => unknown) {
    const container = document.createElement("div");
    document.body.append(container);
    const app = createApp({ render });
    app.mount(container);
    apps.push(app);
    return container;
  }

  afterEach(() => {
    apps.splice(0).forEach(app => app.unmount());
    document.body.replaceChildren();
  });

  it("keeps badge structure after the label text changes", async () => {
    registerNexusElements();
    const label = ref("已启用");
    const container = mount(() => h("nxp-badge", { tone: "ok" }, label.value));
    await nextTick();

    expect(container.querySelector("nxp-badge")?.querySelector(".badge")).not.toBeNull();

    label.value = "未启用";
    await nextTick();

    const badge = container.querySelector("nxp-badge");
    expect(badge?.querySelector(".badge")).not.toBeNull();
    expect(badge?.textContent).toContain("未启用");
  });

  it("keeps button structure after the surrounding list re-renders", async () => {
    registerNexusElements();
    const items = ref(["a"]);
    const container = mount(() => h("div", items.value.map(item => h("nxp-button", { key: item, tone: "danger" }, "删除"))));
    await nextTick();

    expect(container.querySelector("nxp-button button")).not.toBeNull();

    items.value = ["a", "b"];
    await nextTick();

    expect(container.querySelectorAll("nxp-button button").length).toBe(2);
    expect(container.querySelector("nxp-button button")?.textContent).toContain("删除");
  });

  it("keeps switch card structure across model updates", async () => {
    registerNexusElements();
    const enabled = ref(false);
    const container = mount(() => h("nxp-switch-setting", {
      label: "启用自定义壁纸",
      description: "启用后使用自定义壁纸作为页面背景。",
      modelValue: enabled.value,
    }));
    await nextTick();

    expect(container.querySelector("nxp-switch-setting .switch-card")).not.toBeNull();

    enabled.value = true;
    await nextTick();

    expect(container.querySelector("nxp-switch-setting .switch-card")).not.toBeNull();
    expect(container.querySelector("nxp-switch-setting .nxp-switch")?.getAttribute("aria-pressed")).toBe("true");
  });

  it("renders text-bearing elements from the label property", async () => {
    registerNexusElements();
    // 插件模板以属性传文案：元素没有子节点，父级重渲染不会触碰插槽内容。
    const label = ref("删除");
    const tone = ref<"ok" | "muted">("ok");
    const container = mount(() => h("div", [
      h("nxp-button", { tone: "danger", variant: "ghost", size: "sm", label: label.value }),
      h("nxp-badge", { tone: tone.value, label: label.value === "删除" ? "已启用" : "未启用" }),
    ]));
    await nextTick();

    expect(container.querySelector("nxp-button button")?.textContent).toBe("删除");
    expect(container.querySelector("nxp-badge .badge")?.textContent).toBe("已启用");

    label.value = "移除";
    tone.value = "muted";
    await nextTick();

    expect(container.querySelector("nxp-button button")?.textContent).toBe("移除");
    expect(container.querySelector("nxp-badge .badge")?.textContent).toBe("未启用");
    expect(container.querySelector("nxp-badge .badge")?.classList.contains("muted")).toBe(true);
  });

  it("keeps the plugin settings card intact across state updates", async () => {    registerNexusElements();
    // 复刻插件卡片的更新方式：状态发布后徽章文案、缩略图与列表一起重渲染。
    const status = ref("已启用");
    const assets = ref([{ id: "a", thumbnails: false as const }, { id: "b", thumbnails: true as const }]);
    const container = mount(() => h("nxp-collapsible-card", { title: "自定义壁纸", expanded: true }, () => [
      h("div", { class: "cw-status-row" }, [
        h("span", { class: "cw-muted" }, "服务端同步"),
        h("nxp-badge", { tone: status.value === "已启用" ? "ok" : "muted" }, status.value),
      ]),
      h("div", { class: "cw-switch-list" }, [
        h("nxp-switch-setting", { key: "enabled", label: "启用自定义壁纸", modelValue: true }),
        h("nxp-switch-setting", { key: "secondary", label: "透明度运用于非主页面", modelValue: false }),
      ]),
      h("div", { class: "cw-list" }, assets.value.map(asset => h("div", { key: asset.id, class: "cw-item" }, [
        h("span", { class: "cw-thumb-placeholder" }),
        h("nxp-button", { tone: "danger", variant: "ghost", size: "sm" }, "删除"),
      ]))),
    ]));
    await nextTick();

    const badge = () => container.querySelector("nxp-badge .badge");
    expect(badge()).not.toBeNull();
    expect(container.querySelectorAll("nxp-collapsible-card .settings-card").length).toBe(1);
    expect(container.querySelectorAll("nxp-button button").length).toBe(2);

    status.value = "未启用";
    assets.value = [{ id: "a", thumbnails: true as const }];
    await nextTick();

    expect(badge()?.textContent).toBe("未启用");
    expect(container.querySelectorAll("nxp-switch-setting .switch-card").length).toBe(2);
    expect(container.querySelectorAll("nxp-button button").length).toBe(1);
    expect(container.querySelector("nxp-button button")?.textContent).toContain("删除");
  });
});
