# QingToolbox official modules

The official QingToolbox module set contains independently packaged, manually loaded modules.

| Module | Purpose | Version | Minimum host | Load mode | System behavior | Package |
| --- | --- | --- | --- | --- | --- | --- |
| Qing Launcher | Launches user-added and desktop Windows applications, with a module-owned global hotkey and built-in Everything path search. | 0.2.2 | 0.2.6-alpha | Manual | Reads explicitly dropped or desktop shortcut targets, queries its bundled Everything runtime, and starts only a selected result after user action. | `qing.launcher-0.2.2.qmod` |
| TextTools | Local JSON, Base64, URL, case, and line transformations. | 0.1.1 | 0.1.0 | Manual | None; clipboard access occurs only when the user selects Copy Result. | `qing.texttools-0.1.1.qmod` |
| PowerGuard | Confirms a sustained connectivity outage and can request a normal Windows shutdown after an explicit opt-in countdown. | 0.1.1 | 0.1.0 | Manual | Network probes and optional, user-enabled normal shutdown. No service, scheduled task, tray process, remote commands, or forced shutdown. | `qing.powerguard-0.1.1.qmod` |
| WindowTopmost | Lists eligible visible windows and toggles their always-on-top state. | 0.1.1 | 0.1.0 | Manual | Calls bounded Win32 window enumeration and positioning APIs only after user action. | `qing.windowtopmost-0.1.1.qmod` |
| ScreenPin | Captures a selected screen region and keeps it visible in a resizable floating window. | 0.1.1 | 0.1.0 | Manual | Captures only after explicit user action and creates user-controlled floating image windows. | `qing.screenpin-0.1.1.qmod` |
| QingTransfer | Discovers nearby QingToolbox devices with local DNS-SD; D0 has no transfer or pairing action. | 0.1.0 | 0.1.0 | Manual | Advertises an ephemeral TCP endpoint and closes unexpected connections; no data protocol. | `qing.qingtransfer-0.1.0.qmod` |

Each package has a same-name SHA256 sidecar, for example `qing.texttools-0.1.1.qmod.sha256`. Newly built packages also contain exactly one root-level `qmod.json` identity envelope for the host's verified staging and overwrite-update pipeline.

Known limitations:

- TextTools processes text in memory and does not provide file batching or history.
- PowerGuard does not read UPS state; endpoint reachability is authoritative and physical-link state is diagnostic only.
- WindowTopmost lists eligible top-level windows owned by other processes; protected or elevated windows may reject changes.

Build and packaging scripts consume `QingToolbox.Abstractions` from a separate, exact QingToolbox host worktree. Modules do not reference Shell or Core.
