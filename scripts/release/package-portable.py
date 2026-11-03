"""Create a local portable ZIP and hash manifest from the verified publish directory."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]


def validate_runtime_path(name):
    parts = Path(name).parts
    if any(part.lower() in {'data', 'logs', 'temp', 'languages', '.beads', '.local', '.instances'} or part.lower().startswith(('.storage-', '.relocation-')) for part in parts):
        raise ValueError('Runtime data excluded from portable ZIP: ' + name)
    base = Path(name).name.lower()
    if base.startswith(('storage-mode.', 'appsettings.')) or base == 'appsettings.json' or base.endswith(('.log', '.tmp', '.bak')):
        raise ValueError('Runtime configuration excluded from portable ZIP: ' + name)


def package(source=None, output=None, version=None):
    version = version or ET.parse(ROOT / 'Directory.Build.props').findtext('.//InformationalVersion')
    source = Path(source) if source is not None else ROOT / 'artifacts/portable-FlixyPix'
    custom_output = output is not None
    if not (source / 'FlixyPix.exe').is_file() or not (source / 'tools/notices/dependencies/dependencies.json').is_file():
        raise ValueError('Build the complete portable application and notices first.')
    output = Path(output) if output is not None else ROOT / ('artifacts/FlixyPix-' + version + '-win-x64.zip')
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix('.zip.tmp')
    entries = []
    allowed = set(json.loads((source / 'release-files.json').read_text(encoding='utf-8')))
    if 'portable.flag' not in allowed:
        raise ValueError('Portable marker missing from release allowlist.')
    for path in sorted(source.rglob('*')):
        if path.is_symlink() or getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
            raise ValueError('Linked files are not accepted in the portable package.')
        validate_runtime_path(path.relative_to(source).as_posix())
        if not path.is_file():
            continue
        if path.relative_to(source).as_posix().startswith('content-packs/'):
            raise ValueError('Name lists of the original are read from its installation, never shipped: ' + path.name)
        if path.suffix.lower() in {'.sdf', '.db', '.ezl', '.jsonl', '.flixypixbackup', '.ezfpbackup'} or path.name.lower() == 'mmvdb-password.txt':
            raise ValueError('Private catalog data reached the portable output.')
        relative = path.relative_to(source).as_posix()
        if relative != 'release-files.json' and relative not in allowed:
            raise ValueError('File absent from build allowlist: ' + relative)
        with path.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        entries.append({'path': path.relative_to(source).as_posix(), 'bytes': path.stat().st_size, 'sha256': digest})
    manifest = {'product': 'FlixyPix', 'version': version, 'files': entries}
    try:
        with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
            for entry in entries:
                archive.write(source / entry['path'], 'FlixyPix/' + entry['path'])
            archive.writestr('FlixyPix/package-manifest.json', json.dumps(manifest, indent=2) + '\n')
        with zipfile.ZipFile(temporary) as archive:
            if archive.testzip() is not None:
                raise ValueError('Portable ZIP failed its CRC check.')
        temporary.replace(output)
        with output.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        output.with_suffix('.zip.sha256').write_text(digest + '  ' + output.name + '\n', encoding='ascii')
        (output.with_suffix('.manifest.json') if custom_output else ROOT / 'artifacts/portable-manifest.json').write_text(json.dumps({**manifest, 'sha256': digest}, indent=2) + '\n', encoding='utf-8')
        print(f'Packaged {len(entries)} portable files: {output.name}; SHA256 {digest}')
    finally:
        temporary.unlink(missing_ok=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',type=Path)
    parser.add_argument('--output',type=Path)
    parser.add_argument('--version',help='Version for the manifest; default: InformationalVersion of Directory.Build.props')
    args=parser.parse_args()
    package(args.source,args.output,args.version)
