#!/usr/bin/env python3
"""Builds the My City app icon SVGs and PNGs ("Top-down blocks" design).

Run from the project folder:  python3 Art/Icon/make_icons.py
Needs rsvg-convert. Writes SVGs next to this script and PNGs to Assets/Icons/.

The art is drawn on a 512 grid. Android adaptive layers use a 768 view
(-128..640) so the 512 core lands in the 72dp visible area of the 108dp layer.
"""
import pathlib
import subprocess

HERE = pathlib.Path(__file__).resolve().parent
OUT = HERE.parent.parent / "Assets" / "Icons"

ASPHALT = "#3a3f4a"
SIDEWALK = "#c9c3b5"

SCENE = f"""
  <rect x="-160" y="-160" width="832" height="832" fill="{ASPHALT}"/>
  <g fill="{SIDEWALK}">
    <rect x="-160" y="-160" width="356" height="356" rx="20"/><rect x="316" y="-160" width="356" height="356" rx="20"/>
    <rect x="-160" y="316" width="356" height="356" rx="20"/><rect x="316" y="316" width="356" height="356" rx="20"/>
  </g>
  <rect x="-160" y="-160" width="260" height="340" fill="#b5543a"/>
  <rect x="108" y="-160" width="72" height="254" fill="#e0d6c2"/><rect x="108" y="102" width="72" height="78" fill="#8fa3b8"/>
  <rect x="332" y="-160" width="340" height="246" fill="#8fa3b8"/>
  <rect x="332" y="94" width="96" height="86" fill="#d68a4c"/><rect x="436" y="94" width="236" height="86" fill="#e0d6c2"/>
  <rect x="-160" y="332" width="340" height="340" fill="#e0d6c2"/>
  <rect x="20" y="360" width="70" height="70" fill="#c6bba5"/><rect x="-90" y="460" width="80" height="60" fill="#c6bba5"/>
  <rect x="332" y="332" width="340" height="340" fill="#6f9a5b"/>
  <g fill="#4d7d43">
    <circle cx="372" cy="372" r="22"/><circle cx="440" cy="390" r="26"/><circle cx="390" cy="450" r="24"/><circle cx="470" cy="460" r="20"/>
    <circle cx="540" cy="380" r="24"/><circle cx="560" cy="470" r="22"/><circle cx="420" cy="540" r="26"/><circle cx="510" cy="560" r="24"/>
  </g>
  <g fill="#ffd34d">
    {''.join(f'<rect x="252" y="{y}" width="8" height="34" rx="3"/>' for y in (-110, -50, 10, 70, 130, 348, 408, 468, 528, 588))}
    {''.join(f'<rect x="{x}" y="252" width="34" height="8" rx="3"/>' for x in (-110, -50, 10, 70, 130, 348, 408, 468, 528, 588))}
  </g>
  <g fill="#eceae4">
    {''.join(f'<rect x="{x}" y="172" width="12" height="20"/><rect x="{x}" y="320" width="12" height="20"/>' for x in (210, 232, 268, 290))}
    {''.join(f'<rect x="172" y="{y}" width="20" height="12"/><rect x="320" y="{y}" width="20" height="12"/>' for y in (210, 232, 268, 290))}
  </g>
"""

TAXI = """
  <g transform="translate(256 256) rotate(-28) scale(1.2) translate(-256 -256)">
    <rect x="222" y="184" width="80" height="168" rx="28" fill="#000" opacity=".35" filter="url(#soft)"/>
    <g fill="#15171e">
      <rect x="212" y="188" width="10" height="24" rx="3"/><rect x="290" y="188" width="10" height="24" rx="3"/>
      <rect x="212" y="300" width="10" height="24" rx="3"/><rect x="290" y="300" width="10" height="24" rx="3"/>
    </g>
    <rect x="216" y="172" width="80" height="168" rx="28" fill="#ffc727"/>
    <rect x="226" y="212" width="60" height="30" rx="8" fill="#1b2140"/>
    <rect x="228" y="300" width="56" height="20" rx="6" fill="#1b2140"/>
    <rect x="234" y="258" width="44" height="16" rx="4" fill="#fff3c4"/>
    <rect x="224" y="176" width="14" height="8" rx="3" fill="#fff6d6"/><rect x="274" y="176" width="14" height="8" rx="3" fill="#fff6d6"/>
    <rect x="224" y="330" width="14" height="6" rx="2" fill="#e5413a"/><rect x="274" y="330" width="14" height="6" rx="2" fill="#e5413a"/>
  </g>
"""

DEFS = '<defs><filter id="soft" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur stdDeviation="6"/></filter>{clip}</defs>'


def svg(view, body, clip=""):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{view}">'
            + DEFS.format(clip=clip) + body + "</svg>\n")


FULL = "0 0 512 512"
ADAPTIVE = "-128 -128 768 768"

files = {
    # name: (svg text, png size)
    "icon_legacy": (svg(FULL, f'<g clip-path="url(#c)">{SCENE}{TAXI}</g>',
                        '<clipPath id="c"><rect width="512" height="512" rx="112"/></clipPath>'), 512),
    "icon_round": (svg(FULL, f'<g clip-path="url(#c)">{SCENE}{TAXI}</g>',
                       '<clipPath id="c"><circle cx="256" cy="256" r="256"/></clipPath>'), 192),
    "icon_adaptive_bg": (svg(ADAPTIVE, SCENE), 432),
    "icon_adaptive_fg": (svg(ADAPTIVE, TAXI), 432),
}

OUT.mkdir(parents=True, exist_ok=True)
for name, (text, size) in files.items():
    src = HERE / f"{name}.svg"
    src.write_text(text)
    subprocess.run(["rsvg-convert", "-w", str(size), "-h", str(size), "-o", str(OUT / f"{name}.png"), str(src)], check=True)
    print(f"{name}: {size}px")
