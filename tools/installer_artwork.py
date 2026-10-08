"""Derive installer PNG artwork from the existing application icon."""
from pathlib import Path
import hashlib
import subprocess
import base64
import struct


def generate(icon: Path, output: Path) -> dict:
    def quoted(path: Path) -> str:
        return "'" + str(path.resolve()).replace("'", "''") + "'"
    raw = icon.read_bytes()
    reserved, kind, count = struct.unpack_from('<HHH', raw)
    if reserved != 0 or kind != 1 or not 1 <= count <= 32:
        raise ValueError('Invalid application icon')
    layers = []
    for index in range(count):
        width, height, _, _, _, bits, size, offset = struct.unpack_from('<BBBBHHII', raw, 6 + index * 16)
        layer = raw[offset:offset + size]
        if len(layer) != size:
            raise ValueError('Truncated application icon')
        if layer.startswith(b'\x89PNG\r\n\x1a\n'):
            layers.append(((width or 256) * (height or 256), bits, layer))
    if not layers:
        raise ValueError('Application icon has no PNG layer')
    png = base64.b64encode(max(layers)[2]).decode('ascii')
    script = f"""$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$stream=New-Object System.IO.MemoryStream(,[Convert]::FromBase64String('{png}'))
$source=[System.Drawing.Image]::FromStream($stream)
foreach($entry in @(@('wizard.png',164,314,96),@('wizard-small.png',55,55,40))) {{
 $image=New-Object System.Drawing.Bitmap([int]$entry[1],[int]$entry[2])
 $graphics=[System.Drawing.Graphics]::FromImage($image)
 $graphics.Clear([System.Drawing.Color]::White)
 $graphics.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
 $side=[int]$entry[3]
 $graphics.DrawImage($source,[int](($entry[1]-$side)/2),[int](($entry[2]-$side)/2),$side,$side)
 $image.Save((Join-Path {quoted(output)} $entry[0]),[System.Drawing.Imaging.ImageFormat]::Png)
 $graphics.Dispose();$image.Dispose()
}}
$source.Dispose();$stream.Dispose()
"""
    script_path = output / '.artwork.ps1'
    with script_path.open('x', encoding='utf-8-sig') as stream:
        stream.write(script)
    try:
        subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', str(script_path)], check=True)
    finally:
        script_path.unlink()
    return {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in [icon, output/'wizard.png', output/'wizard-small.png']}
