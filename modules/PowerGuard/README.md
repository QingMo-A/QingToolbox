# PowerGuard

See [../../docs/POWERGUARD.md](../../docs/POWERGUARD.md) for setup, safety behavior, testing, and packaging.

The controller enforces state-based operation capabilities, clears stale countdown state after monitoring faults, and invalidates final probes after cancel, recovery, or extension. Recent events are coalesced and localized. Automated verification uses fake network and power adapters; UPS state is not supported and QingToolbox must remain active.

Countdown presentation is session-checked before and after window creation. Settings operations commit consistently or preserve the prior state, atomic writes have no copy-overwrite fallback, and unload refuses to dispose synchronization resources while module-owned tasks remain active. A failed disposal attempt can be retried.
