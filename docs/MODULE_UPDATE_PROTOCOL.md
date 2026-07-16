# QingToolbox module update metadata protocol v1

QingToolbox keeps one official module per directory so each module owns its source, package manifest, localization, documentation, tests, and release history. The update protocol preserves that independence instead of building one large catalog.

## Index and update manifests

`modules/index.json` is a lightweight discovery map. It contains only the protocol version, official source identity, and an Ordinal-sorted mapping from canonical moduleId to a safe repository-relative `update.json` path:

```json
{
  "schemaVersion": 1,
  "sourceId": "qingtoolbox-official",
  "modules": {
    "qing.powerguard": {
      "updateManifest": "PowerGuard/update.json"
    }
  }
}
```

Each module's `module.json` describes the module package and entry point. Its `update.json` describes only versions that have actually been published as immutable, verified assets. A source version change is not a release and is invisible to update clients until a valid release entry is added.

An unpublished module uses:

```json
{
  "schemaVersion": 1,
  "moduleId": "qing.example",
  "publisher": "QingMo-A",
  "releases": []
}
```

An empty `releases` array means no version is currently offered through this source. It does not mean the module is missing or unusable.

## Release entry

The following is a protocol example, not a claim that this asset exists:

```json
{
  "version": "0.1.1-alpha",
  "channel": "preview",
  "moduleApiVersion": "experimental-0.1",
  "minimumHostVersion": "0.1.0-alpha",
  "maximumHostVersionExclusive": "0.2.0-alpha",
  "publishedAt": "2026-07-20T08:00:00Z",
  "package": {
    "fileName": "QingToolbox.PowerGuard-0.1.1-alpha.qmod",
    "url": "https://github.com/QingMo-A/QingToolbox/releases/download/modules-powerguard-v0.1.1-alpha/QingToolbox.PowerGuard-0.1.1-alpha.qmod",
    "size": 183542,
    "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
  },
  "releaseNotes": {
    "zh-CN": "协议格式示例，不代表真实发布。",
    "en-US": "Protocol example only; this is not a real release."
  }
}
```

- `version`, `minimumHostVersion`, and the optional `maximumHostVersionExclusive` use SemVer 2.0. The maximum is an exclusive upper bound and, when present, must be greater than the minimum.
- `preview` accepts prerelease or stable SemVer. `stable` accepts stable SemVer only.
- `moduleApiVersion` identifies the required module contract.
- `publishedAt` is a UTC ISO 8601 timestamp ending in `Z`.
- `package.fileName` is a plain `.qmod` name. `url` is an immutable HTTPS GitHub Release Asset under `QingMo-A/QingToolbox`; branch raw files, Actions artifacts, and mutable `latest/download` aliases are forbidden.
- `size` detects truncation and `sha256` verifies package bytes. SHA256 is not publisher authentication. Protocol v1 does not yet provide digital signatures.
- `releaseNotes` requires nonempty `zh-CN` and `en-US` text.
- Versions are unique and sorted by SemVer from highest to lowest; build metadata does not affect precedence.

Protocol v1 defines only the official source. Third-party sources and publisher trust/signing belong to later phases. A future first host integration will be read-only detection: it will not download, install, replace, or load module DLLs. Detection failures must never prevent an installed module from running. Import and Refresh continue to read manifests without loading module DLLs.

## Safe publishing order

1. Modify module source.
2. Update the source `module.json` version.
3. Build the module.
4. Run module tests.
5. Package the `.qmod`.
6. Audit package contents.
7. Calculate SHA256 and size.
8. Upload an immutable GitHub Release Asset.
9. Download that asset again from GitHub.
10. Verify the downloaded size and SHA256.
11. Confirm the versioned URL is immutable.
12. Add the release to that module's `update.json`.
13. Run metadata validation.
14. Commit and push the modules branch.

The wrong order is to add `update.json` metadata before uploading and re-verifying the asset. That temporarily advertises unavailable or unverified bytes and is prohibited. Updating `update.json` is deliberately the final publishing action.
