"""Package the public source tree of a commit: only files on the allowlist (public-files.txt), each checked for private data."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[2]
ALLOWLIST = Path(__file__).with_name('public-files.txt')
PRIVATE_SUFFIXES = {'.exe', '.dll', '.pdb', '.sdf', '.db', '.bak', '.flixypixbackup', '.ezfpbackup', '.bin', '.ezl'}
# An absolute path into a real user profile; documentation placeholders use %LOCALAPPDATA% or <name>.
USER_PROFILE = re.compile(rb'[A-Za-z]:\\{1,2}Users\\{1,2}(?!Public\b|Default\b)[^\\%<>"\s]+', re.IGNORECASE)


def read_allowlist(text):
    """Include and exclude rules: a file path, or a folder ending in '/'."""
    rules = []
    for line in text.splitlines():
        line = line.strip()
        if line and not line.startswith('#'):
            rules.append((not line.startswith('!'), line.lstrip('!')))
    return rules


def matches(rule, path):
    return path.startswith(rule) if rule.endswith('/') else path == rule


def select(rules, paths):
    """Allowlisted paths; raises when a rule matches nothing, so stale entries surface."""
    selected = []
    for path in paths:
        decision = False
        for include, rule in rules:
            if matches(rule, path):
                decision = include
        if decision:
            selected.append(path)
    unused = [rule for include, rule in rules if include and not any(matches(rule, path) for path in paths)]
    if unused:
        raise ValueError('Allowlist entries without a committed file: ' + ', '.join(unused))
    return selected


def validate_public_path(name):
    path = PurePosixPath(name)
    if path.is_absolute() or '..' in path.parts or any(part in {'bin', 'obj', '.tools', 'derived', 'artifacts', 'archive', 'evidence', '__pycache__'} for part in path.parts):
        raise ValueError('Private/build artifact reached source package: ' + name)
    if path.suffix.lower() in PRIVATE_SUFFIXES or path.name.lower() == 'mmvdb-password.txt':
        raise ValueError('Private binary/database/password excluded: ' + name)


def validate_public_data(name, data):
    path = PurePosixPath(name)
    if path.suffix.lower() in {'.bin', '.ezl'} or any(part.lower().endswith('.blobs') for part in path.parts):
        raise ValueError('Private binary sidecar excluded: ' + name)
    fixture = path.parts[0] == 'tests' and 'Fixtures' in path.parts
    if path.suffix.lower() == '.jsonl' and not fixture:
        raise ValueError('Private catalog exchange excluded: ' + name)
    if path.suffix.lower() == '.json' and not fixture:
        value = json.loads(data.decode('utf-8-sig'))
        if isinstance(value, dict) and value.get('format', '').replace('eazyflixpix-', 'flixypix-', 1) in {
                'flixypix-migration-report', 'flixypix-migration-audit', 'flixypix-catalog', 'flixypix-legacy', 'flixypix-backup',
                'flixypix-original-reference', 'flixypix-offline-input-v2', 'flixypix-legacy-validation-v1'}:
            raise ValueError('Private catalog/migration report excluded: ' + name)
    if USER_PROFILE.search(data):
        raise ValueError('Absolute user profile path in ' + name)
    if len(data) > 4 * 1024 * 1024:
        raise ValueError('Unexpected large source artifact: ' + name)


def git(*arguments):
    return subprocess.check_output(['git', *arguments], cwd=ROOT)


def package(commit, tree=None):
    revision = git('rev-parse', '--verify', commit + '^{commit}').decode().strip()
    committed = [p for p in git('ls-tree', '-r', '-z', '--name-only', revision).decode('utf-8').split('\0') if p]
    rules = read_allowlist(git('show', revision + ':scripts/release/public-files.txt').decode('utf-8'))
    selected = select(rules, committed)
    output = ROOT / 'artifacts/FlixyPix-source.zip'
    output.parent.mkdir(exist_ok=True)
    if tree is not None:
        if tree.exists():
            raise ValueError('The public tree folder must not exist yet: ' + str(tree))
        tree.mkdir(parents=True)
    identities = []
    temporary = output.with_suffix('.zip.tmp')
    try:
        with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
            for name in selected:
                validate_public_path(name)
                data = git('show', revision + ':' + name)
                validate_public_data(name, data)
                archive.writestr('FlixyPix/' + name, data)
                if tree is not None:
                    target = tree.joinpath(*PurePosixPath(name).parts)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(data)
                identities.append({'path': name, 'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data)})
        temporary.replace(output)
    except BaseException:
        if tree is not None:
            shutil.rmtree(tree, ignore_errors=True)
        raise
    finally:
        temporary.unlink(missing_ok=True)
    digest = hashlib.sha256(output.read_bytes()).hexdigest()
    output.with_suffix('.zip.sha256').write_text(digest + '  ' + output.name + '\n', encoding='ascii')
    (ROOT / 'artifacts/source-manifest.json').write_text(json.dumps({'commit': revision, 'sha256': digest, 'files': identities}, indent=2) + '\n', encoding='utf-8')
    print(f'Packaged {len(identities)} of {len(committed)} committed files from {revision[:10]}: {output.name}; SHA256 {digest}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--commit', default='HEAD', help='commit to package (default: HEAD); uncommitted changes are never included')
    parser.add_argument('--tree', type=Path, help='also write the public files into this new folder, e.g. to build it or start the public history')
    arguments = parser.parse_args()
    package(arguments.commit, arguments.tree)
