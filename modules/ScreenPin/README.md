# Screen Pin

Captures a selected screen region and keeps it in a resizable, movable,
always-on-top floating window. Multiple pins are supported and can be closed
together from the module view.

Use **Capture Region**, drag over the desired area, then use the pin toolbar to
toggle always-on-top or close it.

Known limitations: high-DPI and mixed-DPI displays need further refinement;
annotation, mouse passthrough, full-screen capture, and global shortcuts are
not implemented.

## Localization

Screen Pin supports `en-US` and `zh-CN`. Manifest metadata and the main module
view use the module's `i18n` JSON files through `ModuleContext.Localization`.

The main view implements `ILocalizedModuleView`, so the title, description,
buttons, and pinned count refresh when the toolbox language changes. Newly
created capture overlays and pinned image windows use the current language.
Opened pinned image windows also refresh tooltip and context-menu text when the
main view receives a localization refresh.

Known limitation: screenshot geometry, resize behavior, and mixed-DPI behavior
are intentionally unchanged by localization work and remain tracked separately.
