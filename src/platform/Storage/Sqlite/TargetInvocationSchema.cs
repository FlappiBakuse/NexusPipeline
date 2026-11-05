namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class TargetInvocationSchema
{
    // A retry owns a new target and Job. The journal validates one root per invocation; earlier roots remain immutable.
    internal const string Sql = """

        DROP INDEX execution_pc_target_one_root;
        DROP INDEX execution_android_command_one_root;
        CREATE INDEX execution_target_roots_step ON execution_target_job_roots(journal_id,step_id);
        """;
}
