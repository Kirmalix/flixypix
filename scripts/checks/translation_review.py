"""Content-bound linguistic review inventory, independent of structural i18n checks.

Missing records are OPEN, never implicitly approved. A changed source, singular or
translation invalidates approval. Snapshots include full plural objects. Decisions
must explicitly cover every displayed item, and reject concurrent edits.
"""
import argparse
import csv
import hashlib
import io
import json
from pathlib import Path
import sys

import i18n

ROOT = Path(i18n.ROOT)
REGISTER = Path('docs/reviews/localization-review')
STATUSES = {'unchanged', 'corrected', 'uncertain'}


def digest(value):
    return hashlib.sha256(json.dumps(value, ensure_ascii=False, sort_keys=True,
                                     separators=(',', ':')).encode('utf-8')).hexdigest()


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    old = path.read_bytes() if path.exists() else b''
    newline = '\r\n' if b'\r\n' in old else '\n'
    data = (json.dumps(value, ensure_ascii=False, indent=2) + '\n').replace('\n', newline)
    temporary = path.with_name(path.name + '.review-tmp')
    temporary.write_bytes(data.encode('utf-8-sig' if old.startswith(b'\xef\xbb\xbf') else 'utf-8'))
    temporary.replace(path)


def read_tags(path):
    raw = path.read_bytes()
    lines = raw.decode('utf-8-sig').splitlines(keepends=True)
    comments = ''.join(line for line in lines if line.startswith('#'))
    data = ''.join(line for line in lines if not line.startswith('#'))
    reader = csv.DictReader(io.StringIO(data), delimiter=';')
    return comments, reader.fieldnames, list(reader), raw


def inventory(root, area, language, source_keys=None):
    if area == 'ui':
        entries = read_json(root / 'src/FlixyPix.Core/Languages' / (language + '.json'))
        keys = i18n.extract()[0] if source_keys is None else source_keys
        ordered = list(entries) + [key for key in keys if key not in entries]
        return [{'key': key, 'source': {'text': key, 'one': keys.get(key, {}).get('one')},
                 'value': entries.get(key), 'places': keys.get(key, {}).get('places', [])}
                for key in ordered]
    base = root / 'assets/tag-vocabularies'
    source = read_tags(base / 'de.flixytags.csv')[2]
    rows = read_tags(base / (language + '.flixytags.csv'))[2]
    if len(source) != len(rows):
        raise ValueError('Tag concept counts differ from German')
    return [{'key': str(index + 1), 'source': original, 'value': row}
            for index, (original, row) in enumerate(zip(source, rows))]


def fingerprint(item):
    return digest({'source': item['source'], 'value': item['value']})


def load_register(root):
    path = root / REGISTER
    register = {
        'version': 1, 'reviewer': 'Codex; direct linguistic review, no translation model',
        'ui': {}, 'tags': {}}
    for shard in path.glob('*.json'):
        area, language = shard.stem.split('-', 1)
        register[area][language] = read_json(shard)['entries']
    return register


def summarize(root, require_complete=False):
    register = load_register(root)
    source_keys, _ = i18n.extract()
    failures = 0
    for area in ('ui', 'tags'):
        languages = i18n.LANGUAGES if area == 'ui' else ['de', 'en'] + [code for code in i18n.LANGUAGES if code != 'de']
        for language in languages:
            items = inventory(root, area, language, source_keys)
            records = register[area].get(language, {})
            counts = dict.fromkeys(['unchanged', 'corrected', 'uncertain', 'open', 'stale'], 0)
            for item in items:
                record = records.get(item['key'])
                status = 'open' if record is None else (
                    record['status'] if record.get('digest') == fingerprint(item) else 'stale')
                counts[status] += 1
            orphaned = len(set(records) - {item['key'] for item in items})
            failures += counts['open'] + counts['stale'] + counts['uncertain'] + orphaned
            print(area, language, len(items), json.dumps(counts), 'removed=', orphaned)
    return int(require_complete and failures > 0)


def snapshot(root, area, language, start, count, target, pending=False):
    candidates = list(enumerate(inventory(root, area, language)))
    if pending:
        records = load_register(root)[area].get(language, {})
        candidates = [(index, item) for index, item in candidates
                      if item['key'] not in records or
                      records[item['key']].get('digest') != fingerprint(item) or
                      records[item['key']].get('status') == 'uncertain']
    selected = candidates[start:start + count]
    items = [item for _, item in selected]
    if not items:
        raise ValueError('Empty review batch')
    for index, item in selected:
        item['id'] = index
        item['digest'] = fingerprint(item)
    batch = {'area': area, 'language': language, 'items': items}
    write_json(target, batch)
    for item in items:
        print(str(item['id']) + ' ' + json.dumps(item['key'], ensure_ascii=False) +
              ' => ' + json.dumps(item['value'], ensure_ascii=False))


def apply(root, batch, decisions):
    area, language = batch['area'], batch['language']
    content_path = (root / 'src/FlixyPix.Core/Languages' / (language + '.json') if area == 'ui'
                    else root / 'assets/tag-vocabularies' / (language + '.flixytags.csv'))
    content_before = content_path.read_bytes()
    record_path = root / REGISTER / (area + '-' + language + '.json')
    record_before = record_path.read_bytes() if record_path.exists() else None
    originals = {str(item['id']): item for item in batch['items']}
    groups = [set(map(str, decisions.get(status, {}))) for status in STATUSES]
    if set.union(*groups) != set(originals) or sum(map(len, groups)) != len(originals):
        raise ValueError('Decisions must cover every batch ID exactly once')
    current = {item['key']: item for item in inventory(root, area, language)}
    for item in originals.values():
        if item['key'] not in current or fingerprint(current[item['key']]) != item['digest']:
            raise ValueError('Text changed since snapshot: ' + item['key'])
    register = load_register(root)
    records = register[area].setdefault(language, {})
    source_keys = i18n.extract()[0] if area == 'ui' else {}
    for status in STATUSES:
        values = decisions.get(status, {})
        for index in values:
            item = current[originals[str(index)]['key']]
            old_digest = fingerprint(item)
            if status == 'corrected':
                replacement = values[index]
                expected = type(item['value'])
                if area == 'ui' and item['value'] is None:
                    expected = dict if source_keys[item['key']]['plural'] else str
                if type(replacement) is not expected:
                    raise ValueError('Translation type changed: ' + item['key'])
                if area == 'ui':
                    info = source_keys.get(item['key'])
                    if info is None:
                        raise ValueError('Key no longer used in source: ' + item['key'])
                    problems, empty = i18n.check_entry(language, item['key'], info, replacement)
                    if problems or empty:
                        raise ValueError('Invalid correction: ' + item['key'] + ': ' + '; '.join(problems))
                item['value'] = replacement
                if fingerprint(item) == old_digest:
                    raise ValueError('Correction did not change text: ' + item['key'])
            elif status == 'unchanged' and item['value'] is None:
                raise ValueError('Cannot approve a missing translation: ' + item['key'])
            record = {'status': status, 'digest': fingerprint(item)}
            if status == 'corrected':
                record['previous_digest'] = old_digest
            if status == 'uncertain':
                record['note'] = values[index]
            if status == 'unchanged' and records.get(item['key'], {}).get('status') == 'corrected':
                record = records[item['key']]
                record['digest'] = fingerprint(item)
            records[item['key']] = record
    # A second writer may change unrelated entries after inventory() read the
    # file. Do not replace its complete language file or its review records.
    if content_path.read_bytes() != content_before or (
            record_path.read_bytes() if record_path.exists() else None) != record_before:
        raise ValueError('Language or review register changed during apply; retry with a fresh snapshot')
    if decisions.get('corrected'):
        if area == 'ui':
            path = root / 'src/FlixyPix.Core/Languages' / (language + '.json')
            write_json(path, {key: item['value'] for key, item in current.items() if item['value'] is not None})
        else:
            path = root / 'assets/tag-vocabularies' / (language + '.flixytags.csv')
            comments, header, _, raw = read_tags(path)
            newline = '\r\n' if b'\r\n' in raw else '\n'
            output = io.StringIO(newline='')
            output.write(comments)
            writer = csv.DictWriter(output, fieldnames=header, delimiter=';', lineterminator=newline)
            writer.writeheader()
            writer.writerows(item['value'] for item in current.values())
            path.write_bytes(output.getvalue().encode('utf-8-sig'))
    write_json(root / REGISTER / (area + '-' + language + '.json'), {
        'version': 1, 'reviewer': register['reviewer'], 'entries': records})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    subs = parser.add_subparsers(dest='command', required=True)
    check = subs.add_parser('status')
    check.add_argument('--require-complete', action='store_true')
    show = subs.add_parser('snapshot')
    show.add_argument('language')
    show.add_argument('start', type=int)
    show.add_argument('count', type=int)
    show.add_argument('target', type=Path)
    show.add_argument('--area', choices=['ui', 'tags'], default='ui')
    show.add_argument('--pending', action='store_true', help='Only open, changed or uncertain entries; IDs remain current inventory positions')
    record = subs.add_parser('apply')
    record.add_argument('snapshot', type=Path)
    record.add_argument('decisions', type=Path)
    args = parser.parse_args()
    if args.command == 'status':
        return summarize(ROOT, args.require_complete)
    if args.command == 'snapshot':
        snapshot(ROOT, args.area, args.language, args.start, args.count, args.target, args.pending)
    else:
        apply(ROOT, read_json(args.snapshot), read_json(args.decisions))
    return 0


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    sys.exit(main())
