# PowerGuard

See [../../docs/POWERGUARD.md](../../docs/POWERGUARD.md) for setup, safety behavior, testing, and packaging.

The controller enforces state-based operation capabilities, clears stale countdown state after monitoring faults, and invalidates final probes after cancel, recovery, or extension. Recent events are coalesced and localized. Automated verification uses fake network and power adapters; UPS state is not supported and QingToolbox must remain active.
