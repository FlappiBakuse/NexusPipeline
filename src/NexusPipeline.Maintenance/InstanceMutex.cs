namespace NexusPipeline.Maintenance;
internal static class InstanceMutex
{
    internal static string Name
    {
        get
        {
#if NEXUS_TEST_HOST
            string scope = Environment.GetEnvironmentVariable("NEXUS_SYSTEM_RUNTIME_NAME")?.Trim() ?? "";
            return string.IsNullOrWhiteSpace(scope) ? "NexusPipeline.TestHost.SingleInstance" : $"NexusPipeline.TestHost.SingleInstance.{scope}";
#else
            return "NexusPipeline.SingleInstance";
#endif
        }
    }
    internal static Mutex? Acquire()
    {
        var mutex = new Mutex(false, Name);
        bool acquired;
        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (acquired) return mutex;
        mutex.Dispose(); return null;
    }
}
