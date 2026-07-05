# Icon Assets

QingToolbox Shell uses a selected subset of Nieobie Game Icon Pack from
`svg/no-padding`, licensed under CC0 1.0 Universal.

| Target file | Upstream source path | Purpose |
|---|---|---|
| `home.svg` | `svg/no-padding/6-buildings/house.svg` | Sidebar Home |
| `modules.svg` | `svg/no-padding/8-ui/grid.svg` | Sidebar Modules |
| `running.svg` | `svg/no-padding/8-ui/circle-ring.svg` | Sidebar Running |
| `settings.svg` | `svg/no-padding/8-ui/settings.svg` | Sidebar Settings |
| `pin.svg` | `svg/no-padding/2-items/pushpin.svg` | Sidebar Pin |
| `refresh.svg` | `svg/no-padding/8-ui/refresh.svg` | Refresh modules |
| `load.svg` | `svg/no-padding/9-media/download.svg` | Load module |
| `activate.svg` | `svg/no-padding/9-media/play.svg` | Activate module |
| `deactivate.svg` | `svg/no-padding/9-media/pause.svg` | Deactivate module |
| `unload.svg` | `svg/no-padding/9-media/trash.svg` | Unload module |
| `open.svg` | `svg/no-padding/8-ui/menu-open.svg` | Open module view |
| `close.svg` | `svg/no-padding/8-ui/cross.svg` | Close module view |

## Module icons

Modules may set `"icon": "icon.svg"` in `module.json`. The path is resolved relative
to the deployed module directory and currently only SVG is supported. If the file is
missing, invalid, or omitted, the Shell displays its packaged `modules.svg` fallback.

The Hello development module includes its own `icon.svg`, derived from the same
Nieobie `svg/no-padding/8-ui/grid.svg` source.
