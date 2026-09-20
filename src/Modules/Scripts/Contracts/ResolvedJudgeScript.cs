using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace NexusPipeline.Modules.Scripts.Contracts;


/// <summary>本次运行使用的判断脚本来源描述。</summary>
internal sealed record ResolvedJudgeScript(
    bool Enabled,
    string Language,
    string SourceKind,
    string SourcePath,
    string ContentHash);
