"""Writes each direction's tokens file and checks its contrast.

A direction is a few hues and chromas. Each Material 3 colour role is a tone (CIELAB lightness) of one of five
palettes, at the tones Material 3's schemes use, so a direction is the same scheme built from other colours. Every
text-on-surface and outline pair is checked against WCAG 2.2 AA in light and in dark before the file is written; the
run fails, writing nothing, if any pair falls short.

Run from the repository's root:  python specs/042-theme-and-layout-refresh/directions/generate.py
The standard library is all it needs.
"""

import math
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent

# --- colour -------------------------------------------------------------------------------------------------------

def lch_to_srgb(l, c, h):
    """CIELAB LCh (D65) to an sRGB triple in 0..1, or None when it is outside the gamut."""
    a, b = c * math.cos(math.radians(h)), c * math.sin(math.radians(h))
    fy = (l + 16) / 116
    fx, fz = fy + a / 500, fy - b / 200

    def f_inv(t):
        return t ** 3 if t ** 3 > 0.008856 else (t - 16 / 116) / 7.787

    x, y, z = 0.95047 * f_inv(fx), 1.0 * f_inv(fy), 1.08883 * f_inv(fz)
    r = 3.2404542 * x - 1.5371385 * y - 0.4985314 * z
    g = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z
    bl = 0.0556434 * x - 0.2040259 * y + 1.0572252 * z

    def gamma(u):
        return 12.92 * u if u <= 0.0031308 else 1.055 * u ** (1 / 2.4) - 0.055

    rgb = [gamma(v) for v in (r, g, bl)]
    return rgb if all(-0.0005 <= v <= 1.0005 for v in rgb) else None


def tone(hue, chroma, t):
    """The colour of tone t on a palette, lowering chroma until it fits the gamut. Returns #rrggbb."""
    c = chroma
    while True:
        rgb = lch_to_srgb(t, c, hue)
        if rgb is not None:
            return "#" + "".join(f"{round(min(1, max(0, v)) * 255):02x}" for v in rgb)
        if c == 0:
            raise ValueError(f"tone {t} of hue {hue} has no colour")
        # Down in half steps, ending on a grey exactly, which every tone has, whatever chroma it started from.
        c = max(0, c - 0.5)


def luminance(hex_colour):
    channels = [int(hex_colour[i:i + 2], 16) / 255 for i in (1, 3, 5)]
    lin = [v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4 for v in channels]
    return 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2]


def contrast(a, b):
    la, lb = sorted((luminance(a), luminance(b)), reverse=True)
    return (la + 0.05) / (lb + 0.05)

# --- schemes ------------------------------------------------------------------------------------------------------

# Material 3's tones for each role, light then dark.
ROLES = {
    "primary": ("p", 40, 80), "on-primary": ("p", 100, 20),
    "primary-container": ("p", 90, 30), "on-primary-container": ("p", 10, 90),
    "secondary": ("s", 40, 80), "on-secondary": ("s", 100, 20),
    "secondary-container": ("s", 90, 30), "on-secondary-container": ("s", 10, 90),
    "tertiary": ("t", 40, 80), "on-tertiary": ("t", 100, 20),
    "tertiary-container": ("t", 90, 30), "on-tertiary-container": ("t", 10, 90),
    "error": ("e", 40, 80), "on-error": ("e", 100, 20),
    "error-container": ("e", 90, 30), "on-error-container": ("e", 10, 90),
    "surface": ("n", 98, 6), "on-surface": ("n", 10, 90),
    "surface-variant": ("v", 90, 30), "on-surface-variant": ("v", 30, 80),
    "surface-dim": ("n", 87, 6), "surface-bright": ("n", 98, 24),
    "surface-container-lowest": ("n", 100, 4), "surface-container-low": ("n", 96, 10),
    "surface-container": ("n", 94, 12), "surface-container-high": ("n", 92, 17),
    "surface-container-highest": ("n", 90, 22),
    "outline": ("v", 50, 60), "outline-variant": ("v", 80, 30),
    "inverse-surface": ("n", 20, 90), "inverse-on-surface": ("n", 95, 20), "inverse-primary": ("p", 80, 40),
}

# Pairs that carry text (4.5:1) and pairs that only need to be seen (3:1).
TEXT_PAIRS = [(f"on-{r}", r) for r in ("primary", "secondary", "tertiary", "error")] + \
    [(f"on-{r}-container", f"{r}-container") for r in ("primary", "secondary", "tertiary", "error")] + \
    [("on-surface", s) for s in ("surface", "surface-container-lowest", "surface-container-low", "surface-container",
                                 "surface-container-high", "surface-container-highest", "surface-dim", "surface-bright")] + \
    [("on-surface-variant", s) for s in ("surface", "surface-container-lowest", "surface-container-low",
                                         "surface-container", "surface-container-high", "surface-container-highest")] + \
    [("inverse-on-surface", "inverse-surface"), ("primary", "surface"), ("primary", "surface-container-lowest"),
     ("error", "surface")]
SEEN_PAIRS = [("outline", s) for s in ("surface", "surface-container-lowest", "surface-container-high")]


def scheme(d, dark):
    palettes = {"p": d["primary"], "s": d["secondary"], "t": d["tertiary"], "e": (25, 60),
                "n": d["neutral"], "v": d["neutral_variant"]}
    out = {}
    for role, (palette, light_tone, dark_tone) in ROLES.items():
        hue, chroma = palettes[palette]
        t = dark_tone if dark else light_tone
        out[role] = tone(hue, chroma, d.get("tone_overrides", {}).get((role, dark), t))
    return out


def check(name, roles, theme):
    problems = []
    for pairs, need in ((TEXT_PAIRS, 4.5), (SEEN_PAIRS, 3.0)):
        for fg, bg in pairs:
            ratio = contrast(roles[fg], roles[bg])
            if ratio < need:
                problems.append(f"{name} {theme}: {fg} on {bg} is {ratio:.2f}:1, needs {need}:1")
    return problems

# --- the directions -----------------------------------------------------------------------------------------------

DIRECTIONS = {
    "a": {
        "name": "Plum dusk",
        "draws_on": "957e… (plum shades, soft transitions, airy) and 7b84… (deep blue-violet, glow, rounded cards)",
        "primary": (320, 45), "secondary": (300, 18), "tertiary": (275, 40),
        "neutral": (315, 5), "neutral_variant": (315, 10),
        "aliases": {"--md-ext-color-card": "var(--md-sys-color-surface-container-low)", "--radius": "20px",
                    "--gap": "1.75rem", "--rule": "transparent"},
        "dark_aliases": {"--md-ext-color-card": "var(--md-sys-color-surface-container)"},
        "shape": {"medium": "16px", "large": "20px", "extra-large": "32px"},
        "elevation": ("0 1px 2px rgb(20 0 20 / 0.20), 0 2px 8px rgb(20 0 20 / 0.10)",
                      "0 2px 4px rgb(20 0 20 / 0.20), 0 6px 18px rgb(20 0 20 / 0.14)",
                      "0 4px 8px rgb(20 0 20 / 0.22), 0 12px 32px rgb(20 0 20 / 0.18)"),
        "preview": """
CARDS { border-color: transparent; border-radius: var(--md-sys-shape-corner-large); box-shadow: var(--md-sys-elevation-1); }
.bubble { border-left-width: 3px; border-left-color: var(--agent); background: linear-gradient(160deg, color-mix(in srgb, var(--md-sys-color-primary) 7%, var(--card)), var(--card) 60%); }
button:not(.quiet):not(.tonal):not(.danger), .button:not(.quiet):not(.tonal) { box-shadow: 0 4px 18px color-mix(in srgb, var(--md-sys-color-primary) 38%, transparent); }
.rail nav a[aria-current] { background: linear-gradient(90deg, var(--md-sys-color-secondary-container), color-mix(in srgb, var(--md-sys-color-secondary-container) 30%, transparent)); }
""",
        "page": ("radial-gradient(1200px 600px at 20% -10%, color-mix(in srgb, var(--md-sys-color-primary) 10%, transparent), transparent 70%), var(--md-sys-color-surface)",
                 "radial-gradient(1200px 700px at 30% -10%, color-mix(in srgb, var(--md-sys-color-primary) 16%, transparent), transparent 70%), var(--md-sys-color-surface)"),
    },
    "b": {
        "name": "Frost",
        "draws_on": "bf0e… (controls raised off a misty background, very airy) and 957e… (soft shades)",
        "primary": (262, 40), "secondary": (250, 14), "tertiary": (235, 16),
        "neutral": (250, 4), "neutral_variant": (250, 8),
        "aliases": {"--md-ext-color-card": "color-mix(in srgb, var(--md-sys-color-surface-container-lowest) 82%, transparent)",
                    "--radius": "18px", "--gap": "2rem", "--rule": "color-mix(in srgb, var(--md-sys-color-outline-variant) 45%, transparent)"},
        "dark_aliases": {"--md-ext-color-card": "color-mix(in srgb, var(--md-sys-color-surface-container-high) 78%, transparent)"},
        "shape": {"medium": "14px", "large": "18px", "extra-large": "28px"},
        "elevation": ("0 1px 2px rgb(30 40 70 / 0.06), 0 4px 14px rgb(30 40 70 / 0.08)",
                      "0 2px 4px rgb(30 40 70 / 0.08), 0 10px 28px rgb(30 40 70 / 0.12)",
                      "0 4px 10px rgb(30 40 70 / 0.10), 0 18px 44px rgb(30 40 70 / 0.16)"),
        "preview": """
CARDS { border-color: color-mix(in srgb, var(--md-sys-color-surface-container-lowest) 70%, transparent); border-radius: var(--md-sys-shape-corner-large); box-shadow: var(--md-sys-elevation-2); backdrop-filter: blur(14px) saturate(1.2); }
.bubble { border-left-width: 3px; border-left-color: var(--agent); }
.rail { background: color-mix(in srgb, var(--md-sys-color-surface-container-lowest) 70%, transparent); backdrop-filter: blur(14px); box-shadow: var(--md-sys-elevation-1); }
button, .button, .chip, main nav.page-links a, .inbox-views a { box-shadow: var(--md-sys-elevation-1); }
""",
        "page": ("linear-gradient(180deg, color-mix(in srgb, var(--md-sys-color-tertiary-container) 55%, var(--md-sys-color-surface)) 0%, var(--md-sys-color-surface) 60%)",
                 "linear-gradient(180deg, color-mix(in srgb, var(--md-sys-color-secondary-container) 45%, var(--md-sys-color-surface)) 0%, var(--md-sys-color-surface) 60%)"),
    },
    "c": {
        "name": "Warm studio",
        "draws_on": "c410… (warm grey surface, pill tabs, cards joined like a diagram, one bright accent) and bf0e… (airy)",
        "primary": (85, 6), "secondary": (80, 8), "tertiary": (110, 70),
        "neutral": (85, 4), "neutral_variant": (85, 7),
        "tone_overrides": {("primary", False): 20, ("on-primary", False): 98,
                           ("tertiary-container", False): 92, ("on-tertiary-container", False): 15,
                           ("tertiary-container", True): 85, ("on-tertiary-container", True): 15},
        "aliases": {"--md-ext-color-card": "var(--md-sys-color-surface-container-lowest)", "--radius": "22px",
                    "--gap": "1.75rem", "--rule": "transparent"},
        "dark_aliases": {"--md-ext-color-card": "var(--md-sys-color-surface-container)"},
        "shape": {"medium": "16px", "large": "22px", "extra-large": "32px"},
        "elevation": ("0 1px 2px rgb(40 35 25 / 0.06), 0 2px 6px rgb(40 35 25 / 0.06)",
                      "0 2px 4px rgb(40 35 25 / 0.08), 0 8px 20px rgb(40 35 25 / 0.10)",
                      "0 4px 8px rgb(40 35 25 / 0.10), 0 14px 36px rgb(40 35 25 / 0.14)"),
        "preview": """
CARDS { border-color: transparent; border-radius: var(--md-sys-shape-corner-large); box-shadow: var(--md-sys-elevation-1); }
.bubble { border-left-width: 3px; border-left-color: var(--agent); }
.inbox-views a[aria-current], .rail nav a[aria-current] { background: var(--md-sys-color-tertiary-container); color: var(--md-sys-color-on-tertiary-container); }
.chip, main nav.page-links a, .inbox-views a { border-color: transparent; background: var(--md-sys-color-surface-container-lowest); box-shadow: var(--md-sys-elevation-1); }
""",
        "page": ("var(--md-sys-color-surface-container)", "var(--md-sys-color-surface)"),
    },
}

CARDS = ".bubble, .letter, .card-section, .view-card, .grid, .convo-head, .act-cards li, .settings-section fieldset"

# --- writing ------------------------------------------------------------------------------------------------------

def block(roles, aliases):
    lines = [f"    --md-sys-color-{role}: {value};" for role, value in roles.items()]
    lines += [f"    {name}: {value};" for name, value in aliases.items()]
    return "\n".join(lines)


def write(letter, d):
    light, dark = scheme(d, False), scheme(d, True)
    problems = check(letter, light, "light") + check(letter, dark, "dark")
    if problems:
        return problems

    shape = "\n".join(f"    --md-sys-shape-corner-{k}: {v};" for k, v in d["shape"].items())
    elevation = "\n".join(f"    --md-sys-elevation-{i + 1}: {v};" for i, v in enumerate(d["elevation"]))
    dark_aliases = dict(d["aliases"], **d.get("dark_aliases", {}))

    css = f"""/* Direction {letter.upper()}: {d["name"]}. Feature 042, a preview laid over the console by tools/Parley.Screens.
   Draws on: {d["draws_on"]}.
   Written by generate.py; edit the direction there, not here. Every text pair passes WCAG 2.2 AA in both themes. */

:root {{
{block(light, d["aliases"])}
{shape}
{elevation}
}}

@media (prefers-color-scheme: dark) {{
  :root:not([data-theme="light"]) {{
{block(dark, dark_aliases)}
  }}
}}

:root[data-theme="dark"] {{
{block(dark, dark_aliases)}
}}

/* Preview only, below this line: the page behind the cards, and the few component rules that show this direction's
   character (cards without borders, raised controls). The build passes turn these into proper tokens and styles. */
{d["preview"].replace("CARDS", CARDS).strip()}
body {{ background: {d["page"][0]}; background-attachment: fixed; }}
@media (prefers-color-scheme: dark) {{ :root:not([data-theme="light"]) body {{ background: {d["page"][1]}; background-attachment: fixed; }} }}
:root[data-theme="dark"] body {{ background: {d["page"][1]}; background-attachment: fixed; }}
"""
    (HERE / f"{letter}.css").write_text(css, encoding="utf-8", newline="\n")
    return []


def main():
    problems = []
    for letter, d in DIRECTIONS.items():
        found = write(letter, d)
        problems += found
        print(f"{letter}: {d['name']}: " + ("FAILS" if found else "written, contrast passes"))
    for p in problems:
        print("  " + p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
