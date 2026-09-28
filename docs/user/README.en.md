# User guide: key v0.16.9 limits

This page describes the local development version. Check [Releases](https://github.com/FlappiBakuse/NexusPipeline/releases) for what is actually published. The [Chinese guide](README.md) covers installation, operation, recovery, and FAQs in detail.

- The portable package needs both .NET Desktop Runtime 8 x64 and ASP.NET Core Runtime 8 x64. Its offline help page has a separate download button for each runtime. The installer asks before downloading a missing dependency.
- Cancelling a run stops new task dispatch and retries, then waits for owned work to exit before configuration restoration. Check the current user, run, and attempt log segment for progress.
- “Flow ended · some items unverified” means no business completion evidence was established for one or more tasks. It is not success.
- Selective retry includes only independently safe failed items. The Host does not silently replay completed items or risky system actions.
- A newer official OK release may run with restricted task reporting when its base installation and entry point are confirmed. Task outcomes and selective retry remain unverified or disabled until the release-specific rules are checked.
- Configuration repair is off by default. Each supported change requires Preview followed by an explicit Apply; a changed file or active run invalidates the preview.
- Back up the whole instance before upgrading. Preserve `config/`, `data/`, `history/`, `logs/`, `plugins/`, and `.nxp/`. The installer does not claim an unknown portable directory. Uninstall keeps user data by default and does not remove shared .NET runtimes.

Actual clean Windows installation, old binary upgrade, and real game or device validation require separate environments and are not implied by source or synthetic tests.
