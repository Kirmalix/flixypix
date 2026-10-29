"""Create a local portable ZIP and hash manifest from the verified publish directory."""
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]


def package():
    version = ET.parse(ROOT / 'Directory.Build.props').findtext('.//InformationalVersion')
    source = ROOT / 'artifacts/portable-FlixyPix'
    if not (source / 'FlixyPix.exe').is_file() or not (source / 'tools/notices/dependencies/dependencies.json').is_file():
        raise ValueError('Build the complete portable application and notices first.')
    output = ROOT / ('artifacts/FlixyPix-' + version + '-win-x64.zip')
    temporary = output.with_suffix('.zip.tmp')
    entries = []
    for path in sorted(source.rglob('*')):
        if path.is_symlink() or getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
            raise ValueError('Linked files are not accepted in the portable package.')
        if not path.is_file():
            continue
        if path.relative_to(source).as_posix().startswith('content-packs/'):
            raise ValueError('Name lists of the original are read from its installation, never shipped: ' + path.name)
        if path.suffix.lower() in {'.sdf', '.db', '.ezl', '.jsonl', '.flixypixbackup', '.ezfpbackup'} or path.name.lower() == 'mmvdb-password.txt':
            raise ValueError('Private catalog data reached the portable output.')
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
        (ROOT / 'artifacts/portable-manifest.json').write_text(json.dumps({**manifest, 'sha256': digest}, indent=2) + '\n', encoding='utf-8')
        print(f'Packaged {len(entries)} portable files: {output.name}; SHA256 {digest}')
    finally:
        temporary.unlink(missing_ok=True)


if __name__ == '__main__':
    package()
