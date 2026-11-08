#!/usr/bin/env python3
"""Convert the committed artwork to web PNG and Windows ICO; no image generation.

Optional artwork maintenance tool, using Pillow 12.3.0. Normal builds consume
the committed files and do not need Pillow or an image-generation service.
"""
from pathlib import Path
from PIL import Image

root = Path(__file__).resolve().parent.parent
master = root / "src/GlpiPilot/Assets/brand-master.png"
with Image.open(master) as source:
    if source.width != source.height:
        raise ValueError("The application icon master must be square")
    icon = source.convert("RGBA")
    icon.resize((512, 512), Image.Resampling.LANCZOS).save(root / "web/brand/glpi-pilot-icon.png", optimize=True)
    icon.save(root / "src/GlpiPilot/Assets/app.ico", sizes=[(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
