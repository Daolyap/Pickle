# Themes

A theme is a JSON file: the terminal palette (also used for the Windows Terminal color scheme), syntax colors, UI
colors for panels and menus, and the prompt. Built-in themes live in `themes/` in this repository; yours go in the themes
folder (`pk paths`) and override a built-in one of the same name.

```powershell
pk theme list                       # every theme, the current one marked
pk theme preview aurora nord        # sample prompts
pk theme set synthwave              # switch (persists "theme" in config.json)
pk theme auto                       # follow the system's light/dark mode
pk theme import "One Half Dark"     # a Windows Terminal color scheme as a Pickle theme
```

## Built-in themes

| Theme | Look |
|---|---|
| `pickle` (default) | Pickle greens, one line, plain segments |
| `powerline`, `tokyo-night` | Powerline blocks (Nerd Font) |
| `dracula`, `catppuccin` | Rounded blocks; catppuccin puts the input on its own line |
| `gruvbox` | Slanted blocks |
| `nord`, `rose-pine`, `minimal`, `classic`, `mono` | Plain text segments (`classic` looks like `PS C:\>`) |
| `solarized-light` | A light background theme |
| `admin` | Red, shown automatically when Pickle runs elevated (`shell.adminTheme`) |
| `aurora` ✦ | Northern-lights gradient drifting across Powerline blocks |
| `synthwave` ✦ | Neon pink, purple and cyan rolling through rounded blocks |
| `matrix` ✦ | Green text with a bright band sweeping through, blinking block cursor |
| `ember` ✦ | Red and orange blocks breathing brighter and back |
| `prism` ✦ | Blocks slowly cycling through the rainbow |

✦ animated. Themes with blocks and icons need a Nerd Font (`pk font install`); without one, Pickle falls back to plain
symbols automatically (`prompt.icons`).

## Writing a theme

Start from a copy of a built-in theme (`themes/*.json` here, or `pk theme show <name>` to look at one) saved in your themes
folder under a new name. Colors are `#RRGGBB`, an ANSI name (`red`, `brightBlack`, which follow the palette), or
`default`.

```jsonc
{
  "name": "mine",
  "description": "Shown by pk theme list",
  "terminal": { "background": "#1B1F1A", "foreground": "#DDE5D6", "black": "…", "…": "all 16 colors, cursorColor, selectionBackground" },
  "syntax": { "command": "#8FC34A", "string": "#E5C07B", "…": "…" },
  "ui": { "accent": "#8FC34A", "panelBackground": "#1B1F1A", "…": "…" },
  "prompt": {
    "separator": "powerline",          // plain, powerline, round, slant or none
    "newlineBeforeInput": false,       // input on its own line below the segments
    "promptChar": "❯",
    "promptCharColor": "#8FC34A",
    "promptCharErrorColor": "#E0605A", // after a failed command
    "continuationPrompt": "∙ ",
    "transientTemplate": "{time} {promptChar} ",   // what stays in scrollback: {promptChar}, {cwd}, {time}
    "left": [
      { "type": "cwd", "foreground": "#1B1F1A", "background": "#82C4FF", "template": " {icon} {value} ", "icon": "", "options": { "maxDepth": "3" } },
      { "type": "git", "foreground": "#1B1F1A", "background": "#8FC34A" }
    ],
    "right": [ { "type": "time", "foreground": "#5C6A55", "options": { "format": "HH:mm" } } ]
  }
}
```

Segment types: `cwd`, `git`, `status`, `duration`, `time`, `user`, `host`, `admin`, `venv`, `node`, `k8s`, `jobs`, `text`
(`options.text`), plus any segment a plugin registers ([plugin guide](plugins.md)). A segment with a `background` is drawn
as a block joined by the separator; without one it's colored text.

## Animation

Add `prompt.animation` to make the prompt move while Pickle waits for input:

```json
"animation": {
  "effect": "wave",
  "colors": ["#3DDC97", "#2EC4B6", "#4D9DE0", "#7B61FF"],
  "frameMs": 100,
  "periodMs": 6000,
  "spread": 0.12,
  "intensity": 0.6,
  "target": "auto",
  "promptChar": true,
  "promptChars": [],
  "promptCharFrames": 1
}
```

| Field | Meaning |
|---|---|
| `effect` | `wave` scrolls `colors` as a gradient (the segments' own colors when empty) · `rainbow` rotates each color's hue · `pulse` breathes toward `colors[0]` (a lighter shade when empty) · `shimmer` sweeps a bright band (`colors[0]`, else white) across · `none` |
| `frameMs` | Milliseconds per frame (40–2000) |
| `periodMs` | Milliseconds for one full cycle |
| `spread` | Phase offset between neighbouring segments (fraction of a cycle), so the motion travels along the prompt |
| `intensity` | How far `pulse` and `shimmer` move a color (0–1) |
| `target` | `auto` animates block backgrounds and the text of plain segments; `background` or `foreground` forces one |
| `promptChar` | Whether the prompt character's color takes part (its error color never does) |
| `promptChars`, `promptCharFrames` | Prompt characters shown in turn (a blinking `▮`/`▯`, a spinner), each for `promptCharFrames` frames; padded to the widest so the input doesn't jump |

The `status` and `admin` segments always keep their colors, and any segment can opt out with
`"options": { "animate": "false" }` (the animated themes do this for the dark clock block). Only the prompt redraws, and
only the cells that changed. Animation pauses after five minutes without a key press. `prompt.animation` in config.json
turns it off (`off`), forces it (`on`), or with `auto` (the default) animates except over SSH, when `NO_COLOR` or
`TERM=dumb` is set, or when Windows' "Show animations in Windows" (Settings → Accessibility → Visual effects) is off.

## Light and dark

`pk theme auto` (or `"theme": "auto"`) shows `lightTheme` (default `solarized-light`) while the system is in light mode
and `darkTheme` (default `pickle`) otherwise. On Windows that's the app mode in Settings → Personalization → Colors,
checked again at every prompt; elsewhere it's the terminal's `COLORFGBG`. `pk theme auto <light> <dark>` sets both
themes. `pk theme set <name>` goes back to a fixed theme.

## Importing a terminal color scheme

`pk theme import` turns a Windows Terminal color scheme into a Pickle theme saved in your themes folder:

```powershell
pk theme import --list                          # schemes in your Windows Terminal settings, plus Terminal's built-in ones
pk theme import "One Half Dark"                 # → one-half-dark, using the powerline layout
pk theme import "Campbell" --layout aurora --name campbell-aurora
pk theme import .\schemes.json --scheme "Neon Night" --layout catppuccin
```

A file can hold one scheme object, an array of them (the format sites like windowsterminalthemes.dev and
iTerm2-Color-Schemes publish), or a whole `settings.json`. The scheme becomes the palette; syntax and UI colors come from
its ANSI slots; the prompt reuses `--layout`'s segments with every color moved to the scheme's color in the slot the
layout used for it, so a blue block stays blue and animated layouts keep moving in the new colors. An existing theme is
only replaced with `--force`.
