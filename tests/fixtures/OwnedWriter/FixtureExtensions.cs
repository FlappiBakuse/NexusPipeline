internal static partial class FixtureExtensions
{
    internal static Task<int?> RunAsync(string[] args)
    {
        Task<int?>? result = null;
        Dispatch(args, ref result);
        return result ?? Task.FromResult<int?>(null);
    }
    static partial void Dispatch(string[] args, ref Task<int?>? result);
}
