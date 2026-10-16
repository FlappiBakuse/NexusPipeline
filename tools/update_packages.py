"""Generate and compare the two transport archives from one frozen application tree."""
from __future__ import annotations

from pathlib import Path
import zipfile

try:
    from . import update_recipe as recipe
except ImportError:
    import update_recipe as recipe


def generate(production: Path, output: Path, tag: str, application: dict, payload: list[dict],
             *, baseline: tuple[dict, list[dict]] | None = None) -> dict:
    recipe.require(application['productVersion'] == tag.removeprefix('v'), 'package target')
    raw = (production / recipe.MANIFEST_PATH).read_bytes()
    bundled = [file for file in payload if file['path'].startswith('plugins/')]
    portable = recipe.create(application, raw, 'portable', bundled_files=bundled)
    delta = recipe.create(application, raw, 'update', *(baseline or (None, None)))
    prefix = f'NexusPipeline-{tag}-win-x64-'
    for suffix, value in (('Portable.zip', portable), ('Update.zip', delta)):
        recipe.generate(production, output / (prefix + suffix), value, recipe.guides(value))
    return verify(output, output / (prefix + 'Portable.zip'), {'tag': tag, 'payloadFiles': payload})


def verify(output: Path, package: Path, metadata: dict) -> dict:
    prefix = f'NexusPipeline-{metadata["tag"]}-win-x64-'
    values = []
    for file, kind in ((package, 'portable'), (output / (prefix + 'Update.zip'), 'update')):
        recipe.require(file.is_file() and not file.is_symlink(), 'package missing/linked')
        with zipfile.ZipFile(file) as archive:
            info = archive.getinfo(recipe.RECIPE_PATH)
            recipe.require(info.file_size <= recipe.MAX_METADATA, 'recipe size')
            value = recipe.parse(archive.read(info))
            recipe.require(value['packageKind'] == kind and value['target']['version'] == metadata['tag'][1:], 'package identity')
        recipe.verify(file, value)
        values.append(value)
    portable, delta = values
    recipe.require(portable['target'] == delta['target'], 'package target mismatch')
    recipe.require(recipe.file_map(portable['includedFiles']) == recipe.file_map(delta['includedFiles'] + delta['reusedFiles']), 'reconstructed tree differs')
    expected = {file['path']: file for file in portable['includedFiles'] + portable['bundledFiles']}
    recipe.require(len(metadata['payloadFiles']) == len(expected)
                   and {file['path']: file for file in metadata['payloadFiles']} == expected, 'production tree differs')
    return {'portableBytes': package.stat().st_size, 'updateBytes': (output / (prefix + 'Update.zip')).stat().st_size,
            'reusedBytes': sum(file['sizeBytes'] for file in delta['reusedFiles']), 'base': delta['base'],
            'manualCover': delta['manualCover'], 'target': portable['target']}
