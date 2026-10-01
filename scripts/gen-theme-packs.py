#!/usr/bin/env python3
"""
Generates the theme-pack module's themes (src/Pickle.Modules/Themes/Packs/*.json) from a handful of palette values.
Add a palette to PALETTES, run this script, commit the JSON (they are embedded in Pickle.Modules).

    python3 scripts/gen-theme-packs.py
"""
import json
import os

OUT = os.path.join(os.path.dirname(__file__), "..", "src", "Pickle.Modules", "Themes", "Packs")

# name: (style, light, description, palette)
PALETTES = {
    "monokai": ("round", False, "The classic Monokai: warm greens and pinks on charcoal, rounded blocks.",
        dict(bg="#272822", fg="#F8F8F2", black="#272822", red="#F92672", green="#A6E22E", yellow="#E6DB74", blue="#66D9EF", purple="#AE81FF", cyan="#A1EFE4", white="#F8F8F2", bright_black="#75715E", sel="#49483E", comment="#75715E", accent="#A6E22E", bg_alt="#3E3D32")),
    "one-dark": ("round", False, "Atom's One Dark: soft blues and purples, rounded blocks.",
        dict(bg="#282C34", fg="#ABB2BF", black="#282C34", red="#E06C75", green="#98C379", yellow="#E5C07B", blue="#61AFEF", purple="#C678DD", cyan="#56B6C2", white="#ABB2BF", bright_black="#5C6370", sel="#3E4451", comment="#5C6370", accent="#61AFEF", bg_alt="#21252B")),
    "one-light": ("plain", True, "Atom's One Light: a calm light theme with plain segments.",
        dict(bg="#FAFAFA", fg="#383A42", black="#383A42", red="#E45649", green="#50A14F", yellow="#C18401", blue="#4078F2", purple="#A626A4", cyan="#0184BC", white="#A0A1A7", bright_black="#696C77", sel="#E5E5E6", comment="#A0A1A7", accent="#4078F2", bg_alt="#F0F0F0")),
    "ayu-mirage": ("round", False, "Ayu Mirage: muted slate with amber highlights, rounded blocks.",
        dict(bg="#1F2430", fg="#CCCAC2", black="#191E2A", red="#FF6666", green="#87D96C", yellow="#FFCC66", blue="#73D0FF", purple="#DFBFFF", cyan="#95E6CB", white="#CCCAC2", bright_black="#707A8C", sel="#33415E", comment="#707A8C", accent="#FFCC66", bg_alt="#232834")),
    "everforest": ("plain", False, "Everforest: green-tinted forest tones that are easy on the eyes.",
        dict(bg="#2D353B", fg="#D3C6AA", black="#475258", red="#E67E80", green="#A7C080", yellow="#DBBC7F", blue="#7FBBB3", purple="#D699B6", cyan="#83C092", white="#D3C6AA", bright_black="#859289", sel="#475258", comment="#859289", accent="#A7C080", bg_alt="#343F44")),
    "kanagawa": ("powerline", False, "Kanagawa: inks and washi paper after Hokusai's wave, Powerline blocks.",
        dict(bg="#1F1F28", fg="#DCD7BA", black="#16161D", red="#C34043", green="#76946A", yellow="#C0A36E", blue="#7E9CD8", purple="#957FB8", cyan="#6A9589", white="#C8C093", bright_black="#727169", sel="#2D4F67", comment="#727169", accent="#7E9CD8", bg_alt="#2A2A37")),
    "github-dark": ("plain", False, "GitHub's dark theme with plain segments.",
        dict(bg="#0D1117", fg="#C9D1D9", black="#484F58", red="#FF7B72", green="#3FB950", yellow="#D29922", blue="#58A6FF", purple="#BC8CFF", cyan="#39C5CF", white="#B1BAC4", bright_black="#6E7681", sel="#264F78", comment="#8B949E", accent="#58A6FF", bg_alt="#161B22")),
    "github-light": ("plain", True, "GitHub's light theme with plain segments.",
        dict(bg="#FFFFFF", fg="#24292F", black="#24292F", red="#CF222E", green="#1A7F37", yellow="#9A6700", blue="#0969DA", purple="#8250DF", cyan="#1B7C83", white="#6E7781", bright_black="#57606A", sel="#DDF4FF", comment="#6E7781", accent="#0969DA", bg_alt="#F6F8FA")),
    "oxocarbon": ("round", False, "IBM Carbon's Oxocarbon: near-black with electric pink and blue, rounded blocks.",
        dict(bg="#161616", fg="#F2F4F8", black="#262626", red="#EE5396", green="#42BE65", yellow="#FFE97B", blue="#33B1FF", purple="#BE95FF", cyan="#08BDBA", white="#DDE1E6", bright_black="#525252", sel="#393939", comment="#6F6F6F", accent="#78A9FF", bg_alt="#262626")),
    "zenburn": ("plain", False, "Zenburn: low-contrast and gentle, for long sessions.",
        dict(bg="#3F3F3F", fg="#DCDCCC", black="#1E2320", red="#CC9393", green="#7F9F7F", yellow="#F0DFAF", blue="#8CD0D3", purple="#DC8CC3", cyan="#93E0E3", white="#DCDCCC", bright_black="#709080", sel="#5F5F5F", comment="#7F9F7F", accent="#F0DFAF", bg_alt="#4F4F4F")),
    "material-ocean": ("powerline", False, "Material Ocean: deep navy with candy accents, Powerline blocks.",
        dict(bg="#0F111A", fg="#8F93A2", black="#090B10", red="#F07178", green="#C3E88D", yellow="#FFCB6B", blue="#82AAFF", purple="#C792EA", cyan="#89DDFF", white="#EEFFFF", bright_black="#464B5D", sel="#1F2233", comment="#464B5D", accent="#82AAFF", bg_alt="#1A1C25")),
    "night-owl": ("powerline", False, "Night Owl: made for late nights, with Powerline blocks.",
        dict(bg="#011627", fg="#D6DEEB", black="#011627", red="#EF5350", green="#22DA6E", yellow="#ADDB67", blue="#82AAFF", purple="#C792EA", cyan="#21C7A8", white="#FFFFFF", bright_black="#575656", sel="#1D3B53", comment="#637777", accent="#82AAFF", bg_alt="#0B2942")),
}

ICONS = dict(cwd="", git="", duration="", jobs="", admin="", status="", venv="", node="", k8s="󱃾", time="")


def _luminance(hex_color):
    def channel(v):
        v /= 255.0
        return v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4
    r, g, b = (int(hex_color[i:i + 2], 16) for i in (1, 3, 5))
    return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b)


def contrast(a, b):
    la, lb = _luminance(a), _luminance(b)
    return (max(la, lb) + 0.05) / (min(la, lb) + 0.05)


def on(background, *candidates):
    """The candidate color that reads best on `background`."""
    return max(candidates, key=lambda c: contrast(c, background))


def block(kind, fg, bg, icon=None, **options):
    seg = {"type": kind, "foreground": fg, "background": bg, "template": " {icon} {value} " if icon else " {value} "}
    if icon:
        seg["icon"] = icon
    if options:
        seg["options"] = options
    return seg


def plain(kind, fg, template=None, **options):
    seg = {"type": kind, "foreground": fg}
    if template:
        seg["template"] = template
    if options:
        seg["options"] = options
    return seg


def theme(name, style, light, description, p):
    fg_on_accent = p["bg"]
    bright_white = "#FFFFFF" if not light else p["fg"]
    t = {
        "name": name,
        "description": description,
        "terminal": {
            "background": p["bg"], "foreground": p["fg"], "cursorColor": p["fg"], "selectionBackground": p["sel"],
            "black": p["black"], "red": p["red"], "green": p["green"], "yellow": p["yellow"], "blue": p["blue"], "purple": p["purple"], "cyan": p["cyan"], "white": p["white"],
            "brightBlack": p["bright_black"], "brightRed": p["red"], "brightGreen": p["green"], "brightYellow": p["yellow"], "brightBlue": p["blue"], "brightPurple": p["purple"], "brightCyan": p["cyan"], "brightWhite": bright_white,
        },
        "syntax": {
            "default": "default", "command": p["cyan"], "unknownCommand": p["red"], "parameter": p["bright_black"], "string": p["green"], "number": p["purple"],
            "variable": p["fg"], "operator": p["blue"], "keyword": p["blue"], "comment": p["comment"], "type": p["cyan"], "member": p["fg"], "error": p["red"],
            "suggestion": p["comment"], "selectionBackground": p["sel"], "translated": p["comment"],
        },
        "ui": {
            "accent": p["accent"], "muted": p["bright_black"], "success": p["green"], "warning": p["yellow"], "error": p["red"], "info": p["blue"],
            "panelBackground": p["bg"], "panelForeground": p["fg"], "panelBorder": p["sel"], "highlightBackground": p["sel"], "highlightForeground": bright_white,
            "menuBackground": p["bg_alt"], "menuForeground": p["fg"], "menuSelectedBackground": p["accent"], "menuSelectedForeground": on(p["accent"], p["bg"], p["fg"], "#FFFFFF", "#000000"),
            "menuDescription": p["comment"], "matchHighlight": p["yellow"],
        },
    }
    prompt = {
        "separator": style,
        "newlineBeforeInput": False,
        "promptChar": "❯",
        "promptCharColor": p["accent"],
        "promptCharErrorColor": p["red"],
        "continuationPrompt": "│ " if style != "plain" else "∙ ",
        "transientTemplate": "{promptChar} " if style == "plain" else "{time} {promptChar} ",
    }
    if style == "plain":
        prompt["left"] = [
            plain("admin", p["red"], symbol="ADMIN"),
            plain("venv", p["yellow"], "({value})"),
            plain("cwd", p["accent"], maxDepth="3"),
            {**plain("git", p["green"], "{icon} {value}"), "icon": ICONS["git"]},
            plain("duration", p["yellow"], "took {value}"),
            plain("jobs", p["cyan"], "⚙ {value}"),
        ]
        prompt["right"] = [plain("status", p["red"]), plain("time", p["bright_black"], format="HH:mm")]
    else:
        def f(background):
            return on(background, fg_on_accent, "#FFFFFF", "#000000")

        prompt["left"] = [
            block("admin", f(p["red"]), p["red"], None, symbol=ICONS["admin"]),
            block("cwd", f(p["accent"]), p["accent"], ICONS["cwd"], maxDepth="3"),
            block("git", f(p["green"]), p["green"], ICONS["git"]),
            block("duration", f(p["yellow"]), p["yellow"], ICONS["duration"]),
            block("jobs", f(p["cyan"]), p["cyan"], ICONS["jobs"]),
        ]
        prompt["right"] = [
            block("status", f(p["red"]), p["red"], None, symbol=ICONS["status"]),
            block("venv", f(p["yellow"]), p["yellow"], ICONS["venv"]),
            block("node", f(p["green"]), p["green"], ICONS["node"]),
            block("k8s", f(p["blue"]), p["blue"], ICONS["k8s"]),
            block("time", p["fg"], p["sel"], ICONS["time"], format="HH:mm:ss"),
        ]
    t["prompt"] = prompt
    return t


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, (style, light, description, palette) in PALETTES.items():
        with open(os.path.join(OUT, name + ".json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(theme(name, style, light, description, palette), f, indent=2, ensure_ascii=True)
            f.write("\n")
    print(f"wrote {len(PALETTES)} themes to {os.path.normpath(OUT)}")


if __name__ == "__main__":
    main()
