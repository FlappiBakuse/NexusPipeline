/** 插件仓库刷新：先让宿主标记 catalog cache 失效，再强制重取 store 列表。
 *  刷新失败必须向上抛出，调用方据此提示失败而不是伪装成功。 */

export interface StoreRefreshOptions {
  refreshRepository: () => Promise<unknown>;
  reloadStore: () => Promise<unknown>;
}

export async function refreshStoreRepository(options: StoreRefreshOptions): Promise<void> {
  await options.refreshRepository();
  await options.reloadStore();
}
