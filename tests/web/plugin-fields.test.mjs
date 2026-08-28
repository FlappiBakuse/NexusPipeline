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

test("plugin multi-select renders a dropdown menu with checked options", () => {
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

  assert.match(markup, /class="plugin-multi-select"/);
  assert.match(markup, /data-action="toggle-plugin-multi-select"/);
  assert.match(markup, /role="listbox" aria-label="签到游戏" aria-multiselectable="true"/);
  assert.match(markup, /value="gi"[^>]*checked/);
  assert.doesNotMatch(markup, /<select[^>]*multiple/);
});

test("selectedPluginMultiSelectValues reads checked custom options", () => {
  const inputs = [
    { value: "gi", checked: true },
    { value: "hsr", checked: false },
    { value: "zzz", checked: true },
  ];
  const element = {
    querySelectorAll(selector) {
      assert.equal(selector, "input[data-plugin-multi-option]:checked");
      return inputs.filter(input => input.checked);
    },
  };

  assert.deepEqual(selectedPluginMultiSelectValues(element), ["gi", "zzz"]);
});
