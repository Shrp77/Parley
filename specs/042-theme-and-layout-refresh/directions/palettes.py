"""Writes the ready-made palettes as colour-only stylesheets, and checks each one's contrast.

A palette is a base colour and a harmony. The base gives the primary; the secondary is the base's hue at low chroma; the
tertiary (the accent) is the base's hue turned by the harmony; the neutrals are the base's hue at very low chroma, so the
whole scheme feels of a piece. This is the rule the console will build in C# (research R9); this file is its prototype,
used to show the palettes before they are built.

Run from the repository's root:  python specs/042-theme-and-layout-refresh/directions/palettes.py
"""

import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from generate import ROLES, check, tone  # noqa: E402

HERE = pathlib.Path(__file__).resolve().parent / "palettes"

# The harmonies: how far round the colour wheel the accent sits from the base.
HARMONIES = {"analogous": -30, "complementary": 180, "split-complementary": 150, "triadic": 120}

PALETTES = {
    "frost": {"name": "Frost", "base": (262, 40), "harmony": "analogous", "accent_chroma": 16},
    "plum-dusk": {"name": "Plum dusk", "base": (320, 45), "harmony": "analogous", "accent_chroma": 40},
    "warm-studio": {"name": "Warm studio", "base": (85, 6), "harmony": None, "accent": (110, 70),
                    "tone_overrides": {("primary", False): 20, ("on-primary", False): 98,
                                       ("tertiary-container", False): 92, ("on-tertiary-container", False): 15,
                                       ("tertiary-container", True): 85, ("on-tertiary-container", True): 15}},
    "ocean": {"name": "Ocean", "base": (205, 40), "harmony": "complementary", "accent_chroma": 45},
    "forest": {"name": "Forest", "base": (140, 38), "harmony": "split-complementary", "accent_chroma": 45,
               "accent": (75, 50)},
    "ember": {"name": "Ember", "base": (40, 50), "harmony": "split-complementary", "accent_chroma": 30},
}


def palettes_of(p):
    hue, chroma = p["base"]
    accent = p.get("accent") or ((hue + HARMONIES[p["harmony"]]) % 360, p.get("accent_chroma", 30))
    return {"p": (hue, chroma), "s": (hue, max(4, chroma / 3)), "t": accent, "e": (25, 60),
            "n": (hue, 4), "v": (hue, 8)}


def scheme(p, dark):
    pal = palettes_of(p)
    out = {}
    for role, (palette, light_tone, dark_tone) in ROLES.items():
        hue, chroma = pal[palette]
        t = p.get("tone_overrides", {}).get((role, dark), dark_tone if dark else light_tone)
        out[role] = tone(hue, chroma, t)
    return out


def block(roles, indent):
    return "\n".join(f"{indent}--md-sys-color-{r}: {v};" for r, v in roles.items())


def main():
    HERE.mkdir(exist_ok=True)
    problems = []
    for key, p in PALETTES.items():
        light, dark = scheme(p, False), scheme(p, True)
        found = check(key, light, "light") + check(key, dark, "dark")
        problems += found
        if found:
            print(f"{key}: FAILS")
            continue
        harmony = p["harmony"] or "a chosen accent"
        css = f"""/* Palette: {p["name"]} (base hue {p["base"][0]}, {harmony}). Feature 042, a preview laid over the console by
   tools/Parley.Screens. Written by palettes.py. Every text pair passes WCAG 2.2 AA in both themes. */
:root {{
{block(light, "    ")}
}}
@media (prefers-color-scheme: dark) {{
  :root:not([data-theme="light"]) {{
{block(dark, "    ")}
  }}
}}
:root[data-theme="dark"] {{
{block(dark, "    ")}
}}
"""
        (HERE / f"{key}.css").write_text(css, encoding="utf-8", newline="\n")
        print(f"{key}: {p['name']}: written, contrast passes")
    for line in problems:
        print("  " + line)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
