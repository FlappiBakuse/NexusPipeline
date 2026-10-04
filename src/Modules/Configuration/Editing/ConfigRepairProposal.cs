namespace NexusPipeline.Modules.Configuration.Editing;

/// <summary>A repair preview contains only the selected value, never the complete configuration.</summary>
internal sealed record ConfigRepairProposal(
    bool Available,
    string Reason,
    string Plugin,
    string UserId,
    string ScriptId,
    string Field,
    string? OldValue,
    string? ProposedValue,
    string Impact,
    string? Token);
