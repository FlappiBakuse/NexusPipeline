# User Guide

For official downloads and published versions, see [Releases](https://github.com/FlappiBakuse/NexusPipeline/releases). This guide describes how the current source works. Clean installation, upgrades from older builds, and use with a real game still require validation in the corresponding environments.

## Installation and prerequisites

For a first installation, choose either the installer or the portable ZIP. Both start with the same application files: `nexus-pipeline.exe`, `wwwroot/`, `plugins/`, and `README.md`. The bundled EmulatorSupport and LiveScreenshot plugins are disabled by default; enable them on the Plugins page when needed. The installer accepts an empty local directory, or the original directory of an instance registered to the same user when its identity matches. It does not migrate or take over an unknown portable directory. Extract the portable ZIP into an empty directory instead of overwriting an older instance.

The application requires .NET Desktop Runtime 8 x64 and ASP.NET Core Runtime 8 x64. If a dependency is missing, the installer asks about each one and downloads a verified package from a fixed Microsoft URL. The portable build retains .NET's missing-runtime prompt. The two precise download buttons are also available on the offline [`wwwroot/help/runtime-prerequisites.html`](../../frontend/public/help/runtime-prerequisites.html) page. Installing a .NET prerequisite may require system authorization; if you cancel, you can run the installer again.

## Running, cancelling, and logs

Start a script or queue from the Scheduling Center. After you click Cancel, the current run stops accepting further dispatches and retries. The Host requests that processes owned by this run stop, then restores configuration after its writer exits. The stop request and process exit may happen at different times. If a process fails to exit or configuration cannot be restored, preserve the evidence and inspect run history and diagnostics. Do not manually empty the recovery workspace under `data/`.

Logs are segmented by user, queue run, and attempt. After switching users or retrying, look in the current segment for new stdout, stderr, or file logs. Earlier segments remain available in history details. Startup readiness and lack-of-logs timeouts are measured separately. A visible program window does not mean stdout has reported business progress. If no new output arrives before the timeout, the attempt ends; a safe retry is considered only after cleanup is confirmed.

## Outcomes and retries

“Flow ended · some items unverified” means the process ended but at least one task lacks sufficient evidence of its business outcome. It does not mean the task succeeded. A task shown as “skipped because already satisfied” needs evidence from the plugin for this run. A checked configuration option or a generic completion banner alone does not prove a reward was claimed.

Selective retry includes only items that the plugin and Host can both establish are independent, safe, and unsuccessful. Successful items, items with unclear identity, and high-risk finishing actions such as shutdown or restart are not replayed more broadly. If diagnostics say an action can affect later queue entries, check the queue and upstream configuration first. The Host does not rewrite these actions automatically.

A newer official OK-series release can run in restricted mode when its installation identity, entry point, and basic configuration are confirmed. Startup, cancellation, timeouts, and history remain available, while older advanced outcome rules and selective retry are disabled. An update or repair in progress, an untrusted source, or unsuitable basic configuration produces a specific blocking reason. A normal process exit in restricted mode still leaves the outcome unverified.

An OK launcher window remaining open after its tasks finish is not by itself a failure. If its `running` flag remains set but the embedded worker has been confirmed to have exited, the Host prompts you and permits only the basic restricted flow. It blocks a real worker or a process whose identity cannot be read. The Host does not edit `app.json` for you.

## Optional direct MaaFramework project driver

Install and enable MaaFrameworkDriver separately from the store, then restart. In the script editor, select `maa-framework` and an approved project root. After the PI project is read, choose a Controller, Resource, task order, and options. Preview the configuration, authorize each save, and keep it as an independent configuration. Bind a user and add it to an existing queue; Start, Cancel, and History use the existing entry points. Existing MaaEnd or MaaStellaSora specialized instances are not converted automatically.

For Win32, explicitly select the matching window. For ADB, specify the exact client and serial. You may import a supported MXU instance or MaaPiCli configuration in read-only mode; the source files are not modified. A supported single PC launch action from MXU can be mapped to the Host's game launch settings after explicit confirmation. Agent and pretask code belongs to the project: process isolation is not a file or network sandbox, and an engine completion can still be shown as unverified. See `docs/MAAFRAMEWORK_DRIVER.md` in the [official Plugins repository](https://github.com/FlappiBakuse/NexusPipeline-Plugins) for driver scope and project considerations. The store's actual catalog determines whether the driver is listed.

## Configuration repair

“Allow configuration repair” is off by default. Once enabled, preview a specific suggestion shown on the page, then click Apply for that suggestion. The preview token is bound to the current plugin repair declaration, user, script, configuration snapshot, and file bytes. Preview again if a file or plugin capability changes, or if a run or edit conflicts. At present, only the user-level `after_finish` system action declared by March7thAssistant 0.3.1 can be suggested for automatic change to `None`; edit other configuration manually.

## Upgrading, uninstalling, and recovery

Exit the application and back up the entire instance directory before upgrading. Include at least `config/`, `data/`, `history/`, `logs/`, `plugins/`, and `.nxp/`. The built-in updater preserves user directories. The older v0.16.8 worker only replaced the EXE and `wwwroot/`; the first v0.16.9 startup supplied `README.md` from the staged package only if it was missing. For a manual upgrade, replace only `nexus-pipeline.exe`, `wwwroot/`, and `README.md`; do not overwrite your `plugins/`. The installer cannot take over an unknown older directory in place.

Uninstalling preserves user data by default. It removes the fixed directories confirmed to belong to this instance only if you explicitly select deletion of this instance's user data before uninstalling. Deletion is refused if a link or unresolved recovery state is found; unknown files and external game or script paths remain. Shared .NET runtimes are not removed. After an uninstall that preserved data, the same user may reinstall at the original path and explicitly confirm reuse of that data. The installer does not claim other data merely because of the directory name. If installation, updating, or uninstallation reports a pending recovery journal, backup, file lock, or remaining files, preserve the state and follow the [recovery guide](../architecture/recovery.md).

An installer may report “installation incomplete” (exit code 12) after writing program files. It then compensates the Apps & Features registration and native uninstaller according to the confirmed transaction result: an install that never started or was confirmed rolled back restores the prior version; a committed new version retains its new registration. If the phase cannot yet be confirmed, checkpoints and transaction evidence remain, and the next trusted installer run first attempts recovery. Do not manually delete the registration, `unins*`, `.nxp-update`, or `.nxp-backup`, or treat that directory as empty and overwrite it.

## FAQ

**Why does a finished run not show success?** Check the unverified reason in task details and this attempt's log segment. Without independent confirmation from upstream, the Host cannot infer success.

**Why does a newer official release have only restricted mode?** Advanced decisions require evidence for that release's configuration and log contract. If basic startup can be confirmed, it can still run, but outcomes remain unverified.

**Why does the installer reject an old directory?** It accepts only an empty directory or an instance registered to the current Windows user, protecting unknown portable installations and user data. Use the built-in updater or the manual steps above for an older portable instance.

**Why can't I apply a configuration repair?** Check the switch and preview token, whether the file has changed, and whether a run or configuration transaction is occupying it. Then preview again.

**Can I test with a real account?** Local automated acceptance does not include real accounts, games, devices, or bots. Validate those separately in an authorized environment.
