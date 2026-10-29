"""Collect notices from restored runtime dependencies; does not inspect original software."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import shutil
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]

def collect(destination: Path):
    assets = json.loads((ROOT / 'src/FlixyPix.Desktop/obj/project.assets.json').read_text(encoding='utf-8-sig'))
    folders = [Path(p) for p in assets['packageFolders']]
    packages = {name.lower(): data for name, data in assets['libraries'].items() if data['type'] == 'package'}
    # Self-contained framework packs are not regular project libraries, but their
    # license and bundled third-party notices still belong in the local output.
    runtime = ROOT / 'artifacts/portable-FlixyPix/FlixyPix.runtimeconfig.json'
    if runtime.exists():
        config = json.loads(runtime.read_text(encoding='utf-8-sig'))['runtimeOptions']
        for framework in config.get('includedFrameworks', []):
            identity = framework['name'].lower() + '.runtime.win-x64/' + framework['version']
            packages[identity] = {'path': identity}
    destination.mkdir(parents=True, exist_ok=True)
    inventory = []
    for identity, library in sorted(packages.items()):
        package = next((folder / library.get('path', identity) for folder in folders if (folder / library.get('path', identity)).is_dir()), None)
        if package is None:
            raise FileNotFoundError('Restored package not found: ' + identity)
        metadata = ET.parse(next(package.glob('*.nuspec'))).getroot()
        nodes = {node.tag.split('}')[-1]: node for node in metadata.iter() if node.tag.split('}')[-1] in {'license', 'projectUrl', 'repository', 'authors'}}
        license_node = nodes.get('license')
        expression = license_node.text if license_node is not None and license_node.get('type') == 'expression' else None
        output = destination / identity.replace('/', '-')
        output.mkdir(exist_ok=True)
        copied = []
        for candidate in package.rglob('*'):
            if candidate.is_file() and any(term in candidate.name.lower() for term in ('license', 'notice', 'copying')) and candidate.stat().st_size < 4 * 1024 * 1024:
                relative = candidate.relative_to(package)
                target = output / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(candidate, target)
                copied.append(relative.as_posix())
        license_url = None
        if expression and not copied:
            license_url = 'https://licenses.nuget.org/' + urllib.parse.quote(expression, safe='')
            with urllib.request.urlopen(license_url, timeout=30) as response:
                data = response.read(1024 * 1024)
            (output / 'LICENSE.html').write_bytes(data)
            copied.append('LICENSE.html')
        inventory.append({'package': identity, 'licenseExpression': expression, 'licenseFiles': copied, 'licenseUrl': license_url,
                          'nuget': 'https://www.nuget.org/packages/' + identity,
                          'projectUrl': nodes['projectUrl'].text if 'projectUrl' in nodes else None})
    (destination / 'dependencies.json').write_text(json.dumps(inventory, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    print(f'Collected dependency notices for {len(inventory)} packages/frameworks')

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/portable-FlixyPix/tools/notices/dependencies')
    collect(parser.parse_args().output)
