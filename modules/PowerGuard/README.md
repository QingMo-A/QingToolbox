# PowerGuard

See [../../docs/POWERGUARD.md](../../docs/POWERGUARD.md) for setup, safety behavior, testing, and packaging.

The controller enforces state-based operation capabilities, clears stale countdown state after monitoring faults, and invalidates final probes after cancel, recovery, or extension. Recent events are coalesced and localized. Automated verification uses fake network and power adapters; UPS state is not supported and QingToolbox must remain active.

Countdown presentation is session-checked before and after window creation. Settings operations commit consistently or preserve the prior state, atomic writes have no copy-overwrite fallback, and unload refuses to dispose synchronization resources while module-owned tasks remain active. A failed disposal attempt can be retried.

PowerGuard uses injectable `TimeProvider` monotonic timestamps for every business duration while retaining UTC only for display and event records. Deterministic virtual-time tests cover clock jumps and exact release-candidate boundaries. Probe success events are rate-limited without hiding state transitions. Complete real-machine checks with [../../docs/POWERGUARD-ACCEPTANCE.md](../../docs/POWERGUARD-ACCEPTANCE.md) before unattended deployment. UPS remains unsupported, and PowerGuard still depends on QingToolbox remaining active.
