import test from "node:test";
import assert from "node:assert/strict";
import { switchControl } from "../../wwwroot/core/forms.js";
import {
  pluginMultiSelectMarkup,
  selectedPluginMultiSelectValues,
} from "../../wwwroot/core/plugin-fields.js";

test("switchControl keeps required markup out of aria-label", () => {
  const markup = switchControl(
    "enabled",
    '启用自动签到 <span class="req">*</span>',
    "",
    true,
    "toggle-plugin",
    'data-plugin-field="enabled"',
    "启用自动签到",
  );

  assert.match(markup, /<strong>启用自动签到 <span class="req">\*<\/span><\/strong>/);
  assert.match(markup, /aria-label="启用自动签到"/);
  assert.doesNotMatch(markup, /aria-label="[^"]*class=/);
});

test("plugin multi-select reuses the shared dropdown control with selected options", () => {
  const markup = pluginMultiSelectMarkup(
    "gm-plugin-hoyolab-user-settings-games",
    {
      key: "games",
      label: "签到游戏",
      type: "multi-select",
      required: true,
      description: "选择需要签到的游戏。",
      options: [
        { value: "gi", label: "原神" },
        { value: "hsr", label: "崩坏：星穹铁道" },
      ],
    },
    ["gi"],
  );

  assert.match(markup, /class="nxp-select" data-nxp-select data-nxp-select-multiple="true" data-plugin-field="games"/);
  assert.match(markup, /data-nxp-select-multiple="true"/);
  assert.match(markup, /role="listbox" aria-multiselectable="true"/);
  assert.match(markup, /data-value="gi"[^>]*aria-selected="true"/);
  assert.match(markup, /class="nxp-select-check"[^>]*>✓<\/span>/u);
  assert.doesNotMatch(markup, /type="checkbox"/);
  assert.doesNotMatch(markup, /<select[^>]*multiple/);
});

test("selectedPluginMultiSelectValues reads selected custom options", () => {
  const inputs = [
    { dataset: { value: "gi" } },
    { dataset: { value: "zzz" } },
  ];
  const element = {
    querySelectorAll(selector) {
      assert.equal(selector, '[data-plugin-multi-option][aria-selected="true"]');
      return inputs;
    },
  };

  assert.deepEqual(selectedPluginMultiSelectValues(element), ["gi", "zzz"]);
});
