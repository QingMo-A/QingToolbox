# Native module canary

This is a deliberately small process-profile module. It is not a product
module; it verifies that the Tauri host can launch a manifest-owned executable,
complete the nonce-bound `hello` handshake, correlate a manifest-declared
`ping` invoke, and shut down the process.

The host also supplies `QINGTOOLBOX_MODULE_DATA_DIR` for module-owned state;
the canary deliberately does not write to it.

Build and install it into the development resource directory from the repo
root:

```powershell
./scripts/build-tauri-canary.ps1
```
