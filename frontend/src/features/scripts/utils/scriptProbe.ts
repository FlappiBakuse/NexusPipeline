/** 专项脚本根目录即时探测：根路径变化时请求宿主按当前 profile 推导配置，
 *  失败提示用户根目录/输入不可用。仅在专项脚本编辑且根路径非空时触发。 */

export interface RootProbeRequest {
  pluginType: string;
  rootPath: string;
  inputs: Record<string, unknown>;
}

export interface RootProbeOptions {
  request: (input: RootProbeRequest) => Promise<unknown>;
  onError: (reason: unknown) => void;
}

export interface RootProbe {
  probe: (pluginType: string, rootPath: string) => Promise<void>;
  invalidate: () => void;
}

export function shouldProbeSpecializedRoot(pluginType: unknown, rootPath: unknown): boolean {
  return Boolean(String(pluginType ?? "").trim() && String(rootPath ?? "").trim());
}

/**
 * 生成带代际校验的探测器：连续 change 只让最后一次请求的错误可见，
 * `invalidate` 用于编辑器关闭/切换编辑实例时废弃在途响应。
 */
export function createRootProbe(options: RootProbeOptions): RootProbe {
  let generation = 0;
  return {
    async probe(pluginType, rootPath) {
      if (!shouldProbeSpecializedRoot(pluginType, rootPath)) return;
      const token = ++generation;
      try {
        await options.request({
          pluginType: String(pluginType).trim(),
          rootPath: String(rootPath).trim(),
          inputs: {},
        });
      } catch (reason) {
        if (token !== generation) return;
        options.onError(reason);
      }
    },
    invalidate() {
      generation += 1;
    },
  };
}
