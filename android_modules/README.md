# Android modules

Downloadable QingToolbox **Android** modules. Import one from the app with the `+` button on
the Modules page, then load it when you want to use it.

Every file here is a **`.qmod`** package: a plain zip holding `manifest.json` plus the
module's `web/` payload. Importing copies it into the app's private storage; loading runs it;
deleting removes it again.

## Packages

| File | Module | Size | Capabilities |
| --- | --- | --- | --- |
| [`qing.text-codec-0.1.0.qmod`](qing.text-codec-0.1.0.qmod) | 文本编解码 / Text Codec | 2.9 KB | `text.codec`, `clipboard.write` |
| [`qing.device-info-0.1.0.qmod`](qing.device-info-0.1.0.qmod) | 设备信息 / Device Info | 2.7 KB | `device.info`, `clipboard.write` |
| [`qing.qr-code-0.1.0.qmod`](qing.qr-code-0.1.0.qmod) | 二维码 / QR Code | 2.7 KB | `graphics.qr`, `graphics.share`, `clipboard.write` |
| [`qing.file-hash-0.1.0.qmod`](qing.file-hash-0.1.0.qmod) | 文件哈希 / File Hash | 2.8 KB | `file.hash`, `clipboard.write` |

[`index.json`](index.json) is the catalog: id, version, capabilities, size, whole-file
SHA-256 and payload hash for each package. The shell does not read it — it exists so a mirror,
a script, or a person can verify what they downloaded.

Download the file, put it anywhere on the phone, then open **模块 → +** in the app and pick it.

## How to import

1. Open the app and go to **模块** (Modules).
2. Tap **+** in the top bar. This opens the system file picker.
3. Pick the `.qmod` file you downloaded. It is verified and copied into app-private storage.
4. The module appears in the list as **未加载** (not loaded). Nothing runs yet.
5. Open it and tap **加载模块** (Load module) to run it. **卸载** releases it again, and
   **删除** removes it from storage.

An imported module never loads itself: importing is a copy, loading is a decision. The list can
be searched, and filtered to **全部 / 已加载 / 未加载**.

## What a module may do

A module runs as a web page inside the app and can only reach Android through the shell's
bridge, and only for the capabilities its manifest declared. Anything the user sees in the
module page is requested from the shell, which performs the work and returns the answer.
Requests to any host other than the module's own package are refused, so a module is offline
by construction.

| Capability | Bridge methods | What the shell does |
| --- | --- | --- |
| *(always allowed)* | `host.info`, `toast.show` | Reports the contract version, locale and declared capabilities |
| `text.codec` | `text.codec` | Base64 and URL encode/decode over UTF-8 |
| `device.info` | `device.snapshot` | Reads public device and app properties |
| `file.hash` | `file.hash` | Opens the system picker, streams MD5/SHA-1/SHA-256 |
| `graphics.qr` | `graphics.qr` | Encodes text into a QR code PNG |
| `graphics.share` | `graphics.share` | Writes the image to app cache and opens the share sheet |
| `clipboard.write` | `clipboard.write` | Writes text to the clipboard |

## Package layout

```
<id>-<version>.qmod
├─ manifest.json     # the entry point; the shell reads it first
└─ web/
   ├─ index.html     # the page named by manifest.entry
   └─ app.js
```

`manifest.json`:

| Field | Notes |
| --- | --- |
| `schemaVersion` | `1` |
| `id` | `qing.<name>`, lowercase, same shape as desktop module ids |
| `version` | Module version, independent of the shell |
| `apiVersion` | Host contract version; the shell refuses a module it cannot honour |
| `displayName`, `description` | A string, or a map keyed by language tag (`zh-CN`, `en-US`) |
| `runtimeType` | `web` — the only runtime this shell implements |
| `entry` | Path to the page inside `web/` that the shell loads |
| `glyph`, `accent` | Optional display hints: a short glyph and a `#RRGGBB` accent |
| `capabilities` | The access the module asks for; anything undeclared is refused |
| `payloadHash` | SHA-256 over every other entry; added by the packer, verified on import |

A module is **only** allowed to ship `manifest.json` and files under `web/`. Anything else —
a `classes.dex`, a path that escapes the package, a duplicate entry — is rejected before a
single file is written to storage.

## Rebuilding

Sources live in [`src/`](src), one folder per module. The packer signs and verifies them:

```bash
python tools/pack_android_modules.py          # rewrite the packages and index.json
python tools/pack_android_modules.py --check  # fail if they are out of date
```

The archives are written deterministically, so the same sources always produce the same bytes.
`payloadHash` uses the same algorithm as the shell
(`MobileModuleManifest.payloadDigest`), and
`PackagedAndroidModulesTest` reads these real `.qmod` files with the shell's importer — which
is what keeps the Python packer and the Kotlin verifier honest with each other.

## Writing your own module

1. Create `src/qing.<name>/` with a `manifest.json` and a `web/index.html`.
2. Add `web/app.js`; `qing` is already available, injected by the shell:

```js
var info = qing.host();                          // { apiVersion, locale, capabilities, ... }
qing.call("text.codec", { operation: "base64-encode", input: "hi" });
qing.invoke("file.hash", { algorithms: ["sha256"] }).then(console.log);  // answers later
qing.can("file.hash");                           // true only if the manifest declared it
qing.copy("text"); qing.toast("done");
```

3. Run the packer, import the result, and check the Modules page.

Pages inherit the active shell theme through `--qing-*` CSS custom properties and a small
stylesheet the shell injects, both provided before your own styles. The classes are
`.qing-card`, `.qing-title`, `.qing-label`, `.qing-input`, `.qing-textarea`, `.qing-button`,
`.qing-toolbar`, `.qing-segments`, `.qing-rows`, `.qing-row`, `.qing-mono`, `.qing-error`,
`.qing-note`, `.qing-empty` and `.qing-image`.
