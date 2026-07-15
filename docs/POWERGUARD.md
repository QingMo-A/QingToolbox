# PowerGuard

PowerGuard is an in-process QingToolbox module for unattended Windows computers. Once explicitly loaded and activated, it combines the Windows network-availability signal with two independent, low-traffic HTTP connectivity checks. A single failed endpoint is not treated as an outage.

Defaults are a two-minute startup grace period, one minute of continuous offline confirmation, a ten-minute shutdown countdown, and thirty seconds of stable connectivity recovery. The only power action is a normal Windows shutdown through the fixed System32 `shutdown.exe /s /t 0` command. `/f` is never used; applications with unsaved work or system policy may block or delay shutdown.

For unattended use, enable QingToolbox login startup in the host settings, then enable **Start with toolbox** on the PowerGuard module card. PowerGuard does not modify host settings or authorize itself. Closing the module view does not stop monitoring; deactivating or unloading the module does.

Choosing **Cancel this automatic shutdown** persists suppression in the module data directory for only the current continuous outage. Stable connectivity clears suppression and rearms the next outage. After process restart, an unsuppressed outage always passes through the full grace, confirmation, and countdown sequence; countdown time is never persisted.

The **Test warning window** uses an isolated 30-second preview and never invokes the power action. Test connectivity loss and normal shutdown on a dedicated test machine or virtual machine before deployment. PowerGuard does not read UPS battery state and is not a replacement for UPS-vendor power-management software. It depends on the QingToolbox process remaining active.

Runtime data is stored under the `ModuleContext.DataDirectory`: `settings.json` for normalized settings and `events.jsonl` for UTC event summaries. Events rotate near 1 MiB to `events.previous.jsonl`. No IP address, machine name, username, remote telemetry, or full exception stack is recorded. The module runs with the current user's permissions; import only trusted `.qmod` packages.

Build and verify:

```powershell
./scripts/verify-modules.ps1 -Configuration Release -QingToolboxHostRoot "F:\QingToolbox-toolbox"
./scripts/package-powerguard.ps1 -Configuration Release -QingToolboxHostRoot "F:\QingToolbox-toolbox"
```

The package is written to `artifacts/modules/QingToolbox.PowerGuard-0.1.0.qmod` with a SHA256 sidecar. Build artifacts are not committed.
