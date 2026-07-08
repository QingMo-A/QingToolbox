# Window Topmost

Lists ordinary visible windows on the current desktop and lets the user set or
remove their always-on-top state. **Pick Window** selects the window under the
cursor after a three-second delay.

Some elevated windows cannot be changed from a non-elevated process, and
special system windows may ignore the request. Automatic rules and global
shortcuts are not implemented.

## Localization

Window Topmost supports `en-US` and `zh-CN`. Manifest metadata and internal UI
text come from the module's `i18n` JSON files through
`ModuleContext.Localization`.

The view implements `ILocalizedModuleView`, so headings, buttons, table
headers, status messages, and Yes/No values refresh when the toolbox language
changes. Refreshing localization does not re-enumerate windows or intentionally
clear the current selection.
