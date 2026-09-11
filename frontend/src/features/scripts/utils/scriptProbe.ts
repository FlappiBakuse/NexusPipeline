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
  /** 探测指定 profile 与根路径；通用脚本、空路径与重复签名会被跳过。 */
  probe: (pluginType: string | undefined, rootPath: string | undefined) => Promise<void>;
  /** 编辑器关闭或切换编辑对象时调用，废弃在途响应并允许再次探测同一路径。 */
  invalidate: () => void;
}

function signatureOf(pluginType: string, rootPath: string) {
  return `${pluginType}\u0000${rootPath}`;
}

export function shouldProbeSpecializedRoot(pluginType: unknown, rootPath: unknown): boolean {
  return Boolean(String(pluginType ?? "").trim() && String(rootPath ?? "").trim());
}

/**
 * 生产用探测器：`NxpPathPicker` 在同一次编辑里会对 input/change 各发一次 change，
 * 因此这里按「插件 + 根路径」签名去重，保证一次用户提交只产生一次 probe。
 * 连续 change 只让最后一次请求的错误可见，`invalidate` 废弃在途响应。
 */
export function createRootProbe(options: RootProbeOptions): RootProbe {
  let generation = 0;
  let lastSignature: string | null = null;
  return {
    async probe(pluginType, rootPath) {
      if (!shouldProbeSpecializedRoot(pluginType, rootPath)) return;
      const normalizedPlugin = String(pluginType).trim();
      const normalizedRoot = String(rootPath).trim();
      const signature = signatureOf(normalizedPlugin, normalizedRoot);
      if (signature === lastSignature) return;
      lastSignature = signature;
      const token = ++generation;
      try {
        await options.request({
          pluginType: normalizedPlugin,
          rootPath: normalizedRoot,
          inputs: {},
        });
      } catch (reason) {
        if (token !== generation) return;
        options.onError(reason);
      }
    },
    invalidate() {
      generation += 1;
      lastSignature = null;
    },
  };
}
