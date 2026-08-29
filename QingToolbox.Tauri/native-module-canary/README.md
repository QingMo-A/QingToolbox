# Native module canary

This is a deliberately small process-profile module. It is not a product
module; it verifies that the Tauri host can launch a manifest-owned executable,
complete the nonce-bound `hello` handshake and shut down the process.

Build and install it into the development resource directory from the repo
root:

```powershell
./scripts/build-tauri-canary.ps1
```
