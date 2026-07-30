# QingToolbox starter modules

The first public QingToolbox 0.2.1-alpha module set contains three independently packaged, manually loaded modules.

| Module | Purpose | Version | Minimum host | Load mode | System behavior | Package |
| --- | --- | --- | --- | --- | --- | --- |
| TextTools | Local JSON, Base64, URL, case, and line transformations. | 0.1.1 | 0.1.0 | Manual | None; clipboard access occurs only when the user selects Copy Result. | `qing.texttools-0.1.1.qmod` |
| PowerGuard | Confirms a sustained connectivity outage and can request a normal Windows shutdown after an explicit opt-in countdown. | 0.1.0 | 0.1.0 | Manual | Network probes and optional, user-enabled normal shutdown. No service, scheduled task, tray process, remote commands, or forced shutdown. | `qing.powerguard-0.1.0.qmod` |
| WindowTopmost | Lists eligible visible windows and toggles their always-on-top state. | 0.1.1 | 0.1.0 | Manual | Calls bounded Win32 window enumeration and positioning APIs only after user action. | `qing.windowtopmost-0.1.1.qmod` |

Each package has a same-name SHA256 sidecar, for example `qing.texttools-0.1.1.qmod.sha256`.

Known limitations:

- TextTools processes text in memory and does not provide file batching or history.
- PowerGuard does not read UPS state; endpoint reachability is authoritative and physical-link state is diagnostic only.
- WindowTopmost lists eligible top-level windows owned by other processes; protected or elevated windows may reject changes.

Build and packaging scripts consume `QingToolbox.Abstractions` from a separate, exact QingToolbox host worktree. Modules do not reference Shell or Core.
