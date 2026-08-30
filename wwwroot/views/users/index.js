import { actions as sharedActions, pageUsers } from "./shared.js";
import { actions as userManagementActions } from "./user-management.js";
import { actions as globalManagementActions } from "./global-management.js";

/** 用户页动作注册表：列表页 / 用户管理弹窗 / 全局管理弹窗三个模块的合并入口。 */
export const actions = {
  ...sharedActions,
  ...userManagementActions,
  ...globalManagementActions,
};

export { pageUsers };
