using System.Text;
using System.Text.Json;
if (args is ["initial-extensions-recovery", .. var initialExtensionArgs]) return await InitialExtensionCrash.RunAsync(initialExtensionArgs);
if (args is ["database-transaction", .. var databaseArgs]) return DatabaseCrashFixture.Run(databaseArgs);
if (await FixtureExtensions.RunAsync(args) is { } pluginExit) return pluginExit;
if (args is ["installer-shortcut-native", .. var shortcutArgs]) return await InstallerShortcutFixture.RunAsync(shortcutArgs);
if (args is ["account-task-fixture", var accountOutcome, var accountMarker]) return await AccountTaskFixture.RunAsync(accountOutcome, accountMarker);
if (args is ["console-stop-fixture", var stopPid, var stopImage, var stopCreated]) return await ConsoleStopFixture.RunAsync(stopPid, stopImage, stopCreated);
if (args is ["list2"] && File.Exists(Path.Combine(AppContext.BaseDirectory, "emulator-command-fixture.json")))
    return await EmulatorCommandFixture.RunAsync();
if (args is ["provider-device-probe", var deviceInput, var deviceReport]) return await DeviceReaderFixture.ProbeAsync(deviceInput, deviceReport);
if (args is ["retirement-crash", .. var retirementArgs]) return await RetirementCrash.RunAsync(retirementArgs);
if (args is ["maintenance-successor-native", .. var successorArgs]) return await MaintenanceSuccessorFixture.RunAsync(successorArgs);
if (args is ["control-bearer-seed", .. var controlSeedArgs]) return await BearerSeedFixture.RunAsync(controlSeedArgs);
if (args is ["maintenance-startup-native", .. var maintenanceStartupArgs]) return await MaintenanceStartupFixture.RunAsync(maintenanceStartupArgs);
if (args is ["preview-domain-owner", .. var previewDomainArgs]) return await PreviewOwnerFixture.RunAsync(previewDomainArgs);
if (args is ["extension-work-recovery", .. var workRecoveryArgs]) return await WorkCrashFixture.RunAsync(workRecoveryArgs);
if (args is ["extension-installation-recovery", .. var installationRecoveryArgs]) return await InstallationCrashFixture.RunAsync(installationRecoveryArgs);
if (args is ["capture-window-fixture", .. var windowCaptureArgs]) return WindowCaptureFixture.Run(windowCaptureArgs);
if (args is ["notification-scheduled-recovery", .. var notificationScheduledArgs]) return await ScheduledCrashFixture.RunAsync(notificationScheduledArgs);
if (args is ["notification-sender-recovery", .. var notificationSenderArgs]) return await SenderCrashFixture.RunAsync(notificationSenderArgs);
if (args is ["notification-publication-recovery", .. var notificationPublicationArgs]) return PublicationCrashFixture.Run(notificationPublicationArgs);
if (args is ["execution-retry-hook", .. var executionRetryHookArgs]) return await RetryHookFixture.RunAsync(executionRetryHookArgs);
if (args is ["execution-provider-recovery", .. var providerRecoveryArgs]) return await ProviderCrashFixture.RunAsync(providerRecoveryArgs);
if (args is ["provider-fixture-worker", .. var providerArgs]) return await ExecutionProviderFixture.RunAsync(providerArgs);
if (args is ["project-foreign-reader", .. var projectArgs]) return await ProjectDenialFixture.ForeignAsync(projectArgs);
if (args is ["execution-target-recovery", .. var pcTargetRecoveryArgs]) return await TargetCrashFixture.RunAsync(pcTargetRecoveryArgs);
if (args is ["target-recovery-native", .. var targetRecoveryArgs]) return await TargetRecoveryFixture.RunAsync(targetRecoveryArgs);
if (args is ["info", "-v", "all"] && File.Exists(Path.Combine(AppContext.BaseDirectory, "mumu-discovery-fixture.json")))
    return await MuMuDiscoveryFixture.RunAsync(false);
if (args is ["mumu-discovery-fixture-child"] && File.Exists(Path.Combine(AppContext.BaseDirectory, "mumu-discovery-fixture.json")))
    return await MuMuDiscoveryFixture.RunAsync(true);
if (args is ["execution-hook-observe", .. var hookObserveArgs]) return await HookInterpreterFixture.RunAsync(hookObserveArgs);
if (args is ["execution-hooks-recovery", .. var executionHooksRecoveryArgs]) return await HooksCrashFixture.RunAsync(executionHooksRecoveryArgs);
if (args is ["execution-hook", .. var executionHookArgs]) return await ExecutionHookFixture.RunAsync(executionHookArgs);
if (args is ["file-log-output", .. var fileLogArgs]) return await LogOutputFixture.RunAsync(fileLogArgs);
if (args is ["process-output", .. var processOutputArgs]) return await ProcessOutputFixture.RunAsync(processOutputArgs);
if (args is ["execution-terminal-recovery", .. var executionTerminalArgs]) return await TerminalCrashFixture.RunAsync(executionTerminalArgs);
if (args is ["configuration-run-recovery", .. var runRecoveryArgs]) return await RunCrashFixture.RunAsync(runRecoveryArgs);
if (args is ["configuration-edit-controller", .. var controllerArgs]) return await ControllerCrashFixture.RunAsync(controllerArgs);
if (args is ["configuration-editor-session", .. var editorSessionArgs]) return await EditorCrashFixture.RunAsync(editorSessionArgs);
if (args is ["configuration-editor-writer", .. var editorWriterArgs]) return await EditorWriterFixture.RunAsync(editorWriterArgs);
if (args is ["configuration-edit", .. var editArgs]) return EditCrashFixture.Run(editArgs);
if (args is ["configuration-batch", .. var batchArgs]) return BatchCrashFixture.Run(batchArgs);
if (args is ["startup-registration", .. var startupRegistrationArgs]) return StartupCrashFixture.Run(startupRegistrationArgs);

if (args is ["native-ui-harness", .. var nativeUiArgs]) return await NativeUiHarness.RunAsync(nativeUiArgs);
if (args is ["secret-store-read-harness", .. var secretHarnessArgs]) return await SecretStoreFixture.HarnessAsync(secretHarnessArgs);
if (args is ["secret-store-read", .. var secretReadArgs]) return await SecretStoreFixture.ReadAsync(secretReadArgs);

if (args is ["media-plugin", .. var mediaArgs]) return await MediaPluginFixture.RunAsync(mediaArgs);
if (args is ["media-plugin-harness", .. var mediaHarnessArgs]) return await PluginSliceHarness.RunAsync(mediaHarnessArgs, "media-plugin");
if (args is ["history-dataset", .. var datasetArgs]) return HistoryDatasetFixture.Run(datasetArgs);
if (args is ["history-dataset-harness", .. var datasetHarnessArgs]) return await HistoryDatasetHarness.RunAsync(datasetHarnessArgs);
if (args is ["history-query", .. var queryArgs]) return HistoryDatasetFixture.QueryExisting(queryArgs);
if (args is ["history-query-harness", .. var queryHarnessArgs]) return await HistoryDatasetHarness.RunAsync(queryHarnessArgs, "history-query");
if (args is ["history-ui", .. var historyUiArgs]) return HistoryUiFixture.Run(historyUiArgs);
if (args is ["history-ui-harness", .. var historyUiHarnessArgs]) return await HistoryDatasetHarness.RunAsync(historyUiHarnessArgs, "history-ui");
if (args is ["history-ui-verify", .. var historyUiVerifyArgs]) return HistoryUiFixture.Run(historyUiVerifyArgs, reuse: true);
if (args is ["history-ui-verify-harness", .. var historyUiVerifyHarnessArgs]) return await HistoryDatasetHarness.RunAsync(historyUiVerifyHarnessArgs, "history-ui-verify");
if (args is ["native-engine-harness", .. var engineHarnessArgs]) return await NativeEngineHarness.RunAsync(engineHarnessArgs);

if (args is ["configuration", .. var configurationArgs]) return await ConfigurationCrashFixture.RunAsync(configurationArgs);
if (args is ["startup-journal", .. var startupArgs]) return await StartupJournalFixture.RunAsync(startupArgs);
if (args is ["startup-recovery-owner", .. var recoveryOwnerArgs]) return await StartupJournalFixture.OwnRecoveryAsync(recoveryOwnerArgs);
if (args is ["file-cleanup", .. var cleanupArgs]) return CleanupCrashFixture.Run(cleanupArgs);
if (args is ["asset-preparation", .. var assetArgs]) return AssetCrashFixture.Run(assetArgs);
if (args is ["secret-preparation", .. var secretPrepareArgs]) return SecretCrashFixture.Run(secretPrepareArgs);
if (args is ["secret-document", .. var secretDocumentArgs]) return DocumentCrashFixture.Run(secretDocumentArgs);
if (args is ["manual-work", .. var manualWorkArgs]) return await ManualCrashFixture.RunAsync(manualWorkArgs);
if (args is ["effect-gate", .. var gateArgs]) return EffectCrashFixture.Run(gateArgs);
if (args is ["payload", .. var payloadArgs]) return PayloadCrashFixture.Run(payloadArgs);
if (args is ["run-attempt", .. var attemptArgs]) return AttemptCrashFixture.Run(attemptArgs);
if (args is ["desktop-exit", .. var exitArgs]) return await DesktopExitFixture.RunAsync(exitArgs);
if (args is ["desktop-http", var address, var requestPath, var resultPath, var forgedPid, .. var desktopOptions] &&
    (desktopOptions.Length == 0 || desktopOptions is ["mode"]))
{
    using var http = new HttpClient { BaseAddress = new Uri(address) };
    http.DefaultRequestHeaders.Add("X-Nexus-Process-Id", forgedPid);
    http.DefaultRequestHeaders.Add("X-Nexus-Client", "native");
    using var session = await http.GetAsync("control/v1/desktop/session");
    using var content = new ByteArrayContent(File.ReadAllBytes(requestPath));
    content.Headers.ContentType = new("application/json");
    using var action = await http.PostAsync(desktopOptions.Length == 0 ? "control/v1/desktop/authorize" : "control/v1/desktop/mode", content);
    var result = JsonSerializer.SerializeToUtf8Bytes(new { sessionStatus = (int)session.StatusCode, actionStatus = (int)action.StatusCode });
    var stagingResult = resultPath + ".prepared";
    using (var output = new FileStream(stagingResult, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        output.Write(result);
        output.Flush(flushToDisk: true);
    }
    File.Move(stagingResult, resultPath);
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
if (args is ["tcp-client", var portValue, var readyPath, var claimedPid] && int.TryParse(portValue, out var tcpPort))
{
    using var client = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetwork);
    await client.ConnectAsync(System.Net.IPAddress.Loopback, tcpPort);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(new { claimedProcessId = int.Parse(claimedPid), actualProcessId = Environment.ProcessId });
    await client.GetStream().WriteAsync(bytes);
    await client.GetStream().WriteAsync(new byte[] { 10 });
    using (var ready = new FileStream(readyPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
    {
        ready.Write(bytes);
        ready.Flush(flushToDisk: true);
    }
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
if (args is ["maa-agent-fixture", var agentMarker, var agentIdentifier]) return MaaAgentFixture.Run(agentMarker, agentIdentifier);
if (args is ["startup-child-fixture", var startupExecutable, var startupMarker]) return await StartupChildFixture.RunAsync(startupExecutable, startupMarker);
if (args is ["initialization-fixture", var initializationMarker, var initializationOutcome])
{
    OwnedFixtureEvidence.RequirePath(initializationMarker);
    File.AppendAllText(initializationMarker, "initialized\n");
    if (initializationOutcome == "cancel") await Task.Delay(Timeout.InfiniteTimeSpan);
    return initializationOutcome == "success" ? 0 : 7;
}
if (args is ["desktop-environment", var environmentOutput])
{
    var names = new[] { "CHROME_CRASHPAD_PIPE_NAME", "CHROME_CRASHPAD_SERVER_URL", "BREAKPAD_DUMP_LOCATION", "ELECTRON_RUN_AS_NODE" };
    File.WriteAllBytes(environmentOutput + ".prepared", JsonSerializer.SerializeToUtf8Bytes(new
        { present = names.Where(name => Environment.GetEnvironmentVariable(name) is not null).ToArray() }));
    File.Move(environmentOutput + ".prepared", environmentOutput);
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
if (args is not [var outputPath, var nonce]) return 2;
var staging = outputPath + ".prepared";
using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(new { processId = Environment.ProcessId, nonce });
    file.Write(bytes);
    file.Flush(flushToDisk: true);
}
File.Move(staging, outputPath);
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
