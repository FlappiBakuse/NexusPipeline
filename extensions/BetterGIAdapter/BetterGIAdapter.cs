using NexusPipeline.Plugins;

namespace BetterGIAdapter;

/// <summary>BetterGenshinImpact 专项脚本适配：根据脚本根目录推导主程序/配置/日志/自启动参数。</summary>
public sealed class BetterGenshinImpactAdapter : ISpecializedScriptPlugin
{
    public string Name => "bettergi";

    public string DisplayName => "BetterGI";

    public string GameName => "原神";

    public string Description => "BetterGenshinImpact 专项脚本实例配置接管（自动推导主程序、配置、日志路径与自启动参数）";

    public string Version => "0.1.0";

    public bool IsBuiltIn => false;

    public void Initialize(PluginContext context)
    {
        context.Log("BetterGI 专项脚本适配已就绪。");
    }

    public void Shutdown()
    {
    }

    public ScriptProfile? Resolve(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }
        string exe = Path.Combine(rootPath, "BetterGI.exe");
        if (!File.Exists(exe))
        {
            return null;
        }
        return new ScriptProfile
        {
            MainExe = exe,
            Args = "--startOneDragon",
            ConfigPath = Path.Combine(rootPath, "User", "OneDragon", "NexusPipeline.json"),
            LogPath = Path.Combine(rootPath, "log", "better-genshin-impact.log"),
            SuccessMarkers = "一条龙和配置组任务结束",
            JudgeScript = DefaultJudgeScript,
            ConfigTemplate = MinimalConfigTemplate,
        };
    }

    /// <summary>
    /// BetterGI 默认判断脚本（v0.6.1，JavaScript/Jint）：
    /// 等待日志出现运行结束关键字「一条龙和配置组任务结束」→ 提取「{任务名}执行失败/执行异常」失败任务 →
    /// 修改 OneDragon 配置（TaskEnabledList 仅失败任务开、其余关，并清空 NextTaskId 防止重试从中间任务开始跳过失败任务）→
    /// 返回 failed + replaceConfigs 触发重试；无失败任务返回 success。配置文件名为 NexusPipeline.json（与 ConfigPath 一致，单文件替换规则）。
    /// </summary>
    private const string DefaultJudgeScript = """
        const input = JSON.parse(__NEXUS_INPUT__);
        const log = input.log || "";
        const DONE_MARKER = "一条龙和配置组任务结束";

        if (log.indexOf(DONE_MARKER) < 0) {
          // 尚未运行完成（未出现运行结束关键字），持续等待
        } else {
          const ALIAS = { "前往冒险家协会领取奖励": "领取每日奖励" };

          function extractFailedNames(text) {
            const names = [];
            const re = /^(.+?)执行(?:失败|异常)/gm;
            let m;
            while ((m = re.exec(text)) !== null) {
              const raw = (m[1] || "").trim();
              if (raw) names.push(ALIAS[raw] || raw);
            }
            return names;
          }

          const failed = extractFailedNames(log);
          if (failed.length === 0) {
            console.log(JSON.stringify({ status: "success", reason: "全部任务执行成功" }));
          } else {
            const cfgText = nexus.readFile(input.configPath);
            let cfg = null;
            try { cfg = cfgText ? JSON.parse(cfgText) : null; } catch (e) { cfg = null; }
            if (!cfg) {
              console.log(JSON.stringify({ status: "failed", reason: "无法读取或解析 BetterGI 配置文件，已终止重试" }));
            } else {
              const defs = cfg.TaskDefinitions || {};
              const nameToGuid = {};
              for (const guid of Object.keys(defs)) nameToGuid[defs[guid]] = guid;
              const failedGuids = [];
              const unknown = [];
              for (const name of failed) {
                const guid = nameToGuid[name];
                if (guid) failedGuids.push(guid); else unknown.push(name);
              }
              if (unknown.length > 0) {
                console.log(JSON.stringify({ status: "failed", reason: "无法识别失败任务：" + unknown.join("、") + "，为避免误改配置已终止重试" }));
              } else {
                const enabled = cfg.TaskEnabledList || {};
                for (const guid of Object.keys(enabled)) enabled[guid] = failedGuids.indexOf(guid) >= 0;
                cfg.NextTaskId = "";
                nexus.writeFile("NexusPipeline.json", JSON.stringify(cfg, null, 2));
                console.log(JSON.stringify({ status: "failed", reason: "任务执行失败：" + failed.join("、") + "，已调整为仅重试失败任务", replaceConfigs: ["NexusPipeline.json"] }));
              }
            }
          }
        }
        """;

    /// <summary>
    /// 最小配置模板（v0.6.1）：结构键与 BetterGI OneDragon 配置一致，内置 BetterGI 标准任务列表
    /// （领取邮件/自动秘境/自动首领讨伐/自动幽境危战/自动地脉花/领取每日奖励，TaskEnabledList 全 false 由用户按需开启），
    /// 其余值为空或默认。基于真机实际配置整理（TrueEnvironment/config/BetterGI/NexusPipeline.json）：
    /// 内置标准任务定义后，判断脚本的失败任务重试映射（任务名 → GUID）开箱即用。
    /// 不读取用户可能改名的现有配置文件，保证编辑会话始终有一个可用的独立配置。
    /// </summary>
    private const string MinimalConfigTemplate = """
        {
          "TaskEnabledList": {
            "b5ab4590-84ae-4f0d-9716-6281289e9d01": false,
            "ee82d1f1-6cad-4b41-9f72-2b9e5770818a": false,
            "c33f4d50-3a93-4f6b-ad1a-8e7307134a16": false,
            "9a110457-1a84-449f-aae2-e67cb3d70141": false,
            "55fbdba2-7a9b-4627-84d5-b2e5c06b8f7e": false,
            "b55099a7-f826-44aa-b215-475856f4b378": false
          },
          "TaskOrder": [
            "b5ab4590-84ae-4f0d-9716-6281289e9d01",
            "ee82d1f1-6cad-4b41-9f72-2b9e5770818a",
            "c33f4d50-3a93-4f6b-ad1a-8e7307134a16",
            "9a110457-1a84-449f-aae2-e67cb3d70141",
            "55fbdba2-7a9b-4627-84d5-b2e5c06b8f7e",
            "b55099a7-f826-44aa-b215-475856f4b378"
          ],
          "TaskDefinitions": {
            "b5ab4590-84ae-4f0d-9716-6281289e9d01": "领取邮件",
            "ee82d1f1-6cad-4b41-9f72-2b9e5770818a": "自动秘境",
            "c33f4d50-3a93-4f6b-ad1a-8e7307134a16": "自动首领讨伐",
            "9a110457-1a84-449f-aae2-e67cb3d70141": "自动幽境危战",
            "55fbdba2-7a9b-4627-84d5-b2e5c06b8f7e": "自动地脉花",
            "b55099a7-f826-44aa-b215-475856f4b378": "领取每日奖励"
          },
          "Name": "NexusPipeline",
          "NextTaskId": "",
          "CraftingBenchCountry": "枫丹",
          "AdventurersGuildCountry": "挪德卡莱",
          "PartyName": "",
          "DomainName": "",
          "WeeklyDomainEnabled": false,
          "AutoBossName": "",
          "AutoBossStrategyName": "",
          "AutoBossTeamName": "",
          "AutoBossSpecifyRunCount": false,
          "AutoBossRunCount": 1,
          "AutoBossUseTransientResin": false,
          "AutoBossUseFragileResin": false,
          "AutoBossReviveRetryCount": 0,
          "AutoBossReturnToStatueAfterEachRound": false,
          "AutoBossRewardRecognitionEnabled": false,
          "AutoBossTimeout": 0,
          "DailyRewardPartyName": "",
          "MinResinToKeep": 0,
          "SundayEverySelectedValue": "",
          "SundayWeeklySelectedValue": "",
          "SereniteaPotTpType": "",
          "SecretTreasureObjects": [],
          "LeyLineOneDragonMode": false,
          "LeyLineRunMonday": false,
          "LeyLineRunTuesday": false,
          "LeyLineRunWednesday": false,
          "LeyLineRunThursday": false,
          "LeyLineRunFriday": false,
          "LeyLineRunSaturday": false,
          "LeyLineRunSunday": false,
          "LeyLineMondayType": "",
          "LeyLineMondayCountry": "",
          "LeyLineTuesdayType": "",
          "LeyLineTuesdayCountry": "",
          "LeyLineWednesdayType": "",
          "LeyLineWednesdayCountry": "",
          "LeyLineThursdayType": "",
          "LeyLineThursdayCountry": "",
          "LeyLineFridayType": "",
          "LeyLineFridayCountry": "",
          "LeyLineSaturdayType": "",
          "LeyLineSaturdayCountry": "",
          "LeyLineSundayType": "",
          "LeyLineSundayCountry": "",
          "LeyLineRunCount": 0,
          "LeyLineResinExhaustionMode": false,
          "LeyLineOpenModeCountMin": false,
          "MondayPartyName": "",
          "MondayDomainName": "",
          "MondaySelectedValue": "",
          "TuesdayPartyName": "",
          "TuesdayDomainName": "",
          "TuesdaySelectedValue": "",
          "WednesdayPartyName": "",
          "WednesdayDomainName": "",
          "WednesdaySelectedValue": "",
          "ThursdayPartyName": "",
          "ThursdayDomainName": "",
          "ThursdaySelectedValue": "",
          "FridayPartyName": "",
          "FridayDomainName": "",
          "FridaySelectedValue": "",
          "SaturdayPartyName": "",
          "SaturdayDomainName": "",
          "SaturdaySelectedValue": "",
          "SundayPartyName": "",
          "SundayDomainName": "",
          "SundaySelectedValue": "",
          "CompletionAction": "关闭游戏和软件"
        }
        """;
}
