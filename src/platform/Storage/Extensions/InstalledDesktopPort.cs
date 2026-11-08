using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Globalization;
using NexusPipeline.Core.Desktop;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Secrets;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Processes;
using CorePolicy = NexusPipeline.Core.Desktop.BrowserLoginPolicy;
using SdkPolicy = NexusPipeline.Extensibility.Lifecycle.BrowserLoginPolicy;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledDesktopPort(InstalledExtensionConsumption consumption, DesktopPresence? presence,
    NativeClientSessions? nativeClients, BrowserLoginSessions? browserLogins, ExtensionScope scope, ExtensionManifest manifest) : IExtensionDesktop
{
    private bool Allowed => manifest.Capabilities.Contains("ui.contribute", StringComparer.Ordinal);
    private bool Captures => manifest.Permissions.DesktopAccess?.Contains("interactive-login", StringComparer.Ordinal) == true;

    public ValueTask<IReadOnlyList<DesktopWindow>> ReadWindowsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var use = consumption.Begin();
        if (manifest.Permissions.DesktopAccess?.Contains("window-inventory", StringComparer.Ordinal) != true)
            throw new UnauthorizedAccessException("desktop_window_inventory_denied");
        var actual = RequireNative(); actual.RequireCurrent();
        var observed = WindowSnapshotProbe.Capture(null, "", cancellationToken);
        if (observed.Truncated) throw new IOException("desktop_window_inventory_incomplete");
        var result = new List<DesktopWindow>();
        foreach (var window in observed.Windows)
        {
            cancellationToken.ThrowIfCancellationRequested(); actual.RequireCurrent(); use.RequireCurrent();
            if (!long.TryParse(window.Hwnd, NumberStyles.None, CultureInfo.InvariantCulture, out var handle) ||
                handle is < 1 or > 9007199254740991) continue;
            using var process = JobNative.OpenProcess(0x1000, false, window.Pid);
            if (process.IsInvalid) continue;
            try
            {
                var identity = ProcessIdentityReader.ReadMetadata(process, window.Pid);
                result.Add(new(handle, identity.ProcessId, window.Title, identity.ImagePath, new(identity.CreatedAtUtc)));
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        cancellationToken.ThrowIfCancellationRequested(); actual.RequireCurrent(); use.RequireCurrent();
        return ValueTask.FromResult<IReadOnlyList<DesktopWindow>>(result.ToArray());
    }

    public ValueTask<DesktopClientState> ReadClientAsync(CancellationToken cancellationToken)
    {
        if (!Allowed) throw new UnauthorizedAccessException("extension_frontend_undeclared");
        cancellationToken.ThrowIfCancellationRequested(); using var use = consumption.Begin();
        var bound = false; var captures = false;
        if (consumption.ClientId is { } client && nativeClients is not null)
        {
            try
            {
                var actual = nativeClients.Require(client); actual.RequireCurrent(); bound = true;
                captures = Captures && browserLogins?.CanCapture(actual) == true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException) { }
        }
        use.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new DesktopClientState(bound, captures));
    }

    public ValueTask RequireClientAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var use = consumption.Begin();
        RequireNative().RequireCurrent(); use.RequireCurrent(); return ValueTask.CompletedTask;
    }

    public ValueTask<Guid?> ReadSessionAsync(CancellationToken cancellationToken)
    {
        if (!Allowed) throw new UnauthorizedAccessException("extension_frontend_undeclared");
        cancellationToken.ThrowIfCancellationRequested(); using var use = consumption.Begin();
        var current = presence?.Current; use.RequireCurrent(); return ValueTask.FromResult(current);
    }

    public async ValueTask<BrowserLoginCapture> CaptureAsync(SdkPolicy policy, CancellationToken cancellationToken)
        => await CaptureCoreAsync(policy, null, cancellationToken).ConfigureAwait(false);

    public ValueTask<BrowserLoginCapture> CaptureValidatedAsync(SdkPolicy policy,
        Func<BrowserLoginCapture, CancellationToken, ValueTask<bool>> validate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validate);
        return CaptureCoreAsync(policy, validate, cancellationToken);
    }

    private async ValueTask<BrowserLoginCapture> CaptureCoreAsync(SdkPolicy policy,
        Func<BrowserLoginCapture, CancellationToken, ValueTask<bool>>? validate, CancellationToken cancellationToken)
    {
        using var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
        var actual = RequireNative();
        if (!Captures || browserLogins is null || consumption.FrontendOperationId is not { } operation)
            throw new UnauthorizedAccessException("browser_login_permission_required");
        var frozen = BrowserPolicyRules.Freeze(Copy(policy), manifest.Permissions.LoginOrigins ?? []);
        void Current() { actual.RequireCurrent(); use.RequireCurrent(); }
        async ValueTask<bool> Validate(Core.Desktop.BrowserLoginMaterial candidate, CancellationToken cancellation)
        {
            Current();
            using var capture = CopyCapture(candidate);
            var accepted = await validate!(capture, cancellation).ConfigureAwait(false);
            Current(); return accepted;
        }
        using var material = await browserLogins.CaptureAsync(actual, manifest.ExtensionId, scope.ActivationId, operation,
            frozen, Current, consumption.CallbackLifetime, cancellationToken, validate is null ? null : Validate).ConfigureAwait(false);
        Current(); cancellationToken.ThrowIfCancellationRequested(); return CopyCapture(material);
    }

    private static BrowserLoginCapture CopyCapture(Core.Desktop.BrowserLoginMaterial material)
    {
        var drafts = new Dictionary<string, SecretDraft>(StringComparer.Ordinal);
        try
        {
            foreach (var (id, secret) in material.Values)
            { SecretDraft? draft = null; secret.Use(value => draft = new(value)); drafts.Add(id, draft!); }
            return new(drafts);
        }
        catch { foreach (var draft in drafts.Values) draft.Dispose(); throw; }
    }

    private NativeClientAccess RequireNative()
    {
        if (!Allowed || !consumption.IsDesktopCall || consumption.ClientId is not { } client || nativeClients is null)
            throw new UnauthorizedAccessException("native_frontend_required");
        return nativeClients.Require(client);
    }

    private static CorePolicy Copy(SdkPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Origins is null || policy.PopupOrigins is null || policy.Cookies is null || policy.Storage is null || policy.JsonRequests is null ||
            policy.ReadySets is null || policy.Checks is null || policy.EqualSets is null || policy.Origins.Count > 16 || policy.PopupOrigins.Count > 8 ||
            policy.Cookies.Count > 16 || policy.Storage.Count > 8 || policy.JsonRequests.Count > 4 || policy.ReadySets.Count > 16 ||
            policy.Checks.Count > 28 || policy.EqualSets.Count > 8) throw new ArgumentException("browser_login_policy_invalid");
        try
        {
            return new(policy.Id, policy.StartUrl, policy.Origins.ToImmutableArray(), policy.PopupOrigins.ToImmutableArray(),
                policy.Cookies.Select(rule => new Core.Desktop.LoginCookieRule(rule.Id, rule.Domain, rule.Path, rule.Name)).ToImmutableArray(),
                policy.Storage.Select(rule => new Core.Desktop.LoginStorageRule(rule.Id, rule.Origin, rule.Storage, rule.Key)).ToImmutableArray(),
                policy.JsonRequests.Select(rule => new Core.Desktop.LoginJsonRule(rule.Id, rule.Origin, rule.Endpoint, rule.Method, rule.Selector.ToImmutableArray())).ToImmutableArray(),
                policy.ReadySets.Select(set => set.ToImmutableArray()).ToImmutableArray(),
                policy.Checks.Select(check => new Core.Desktop.LoginValueCheck(check.Id, check.Kind, check.Path.ToImmutableArray(), check.MaximumLength)).ToImmutableArray(),
                policy.EqualSets.Select(set => set.ToImmutableArray()).ToImmutableArray());
        }
        catch (Exception error) when (error is NullReferenceException or ArgumentNullException)
        { throw new ArgumentException("browser_login_policy_invalid", nameof(policy)); }
    }
}
