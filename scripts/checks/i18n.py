"""Translation files of FlixyPix (CONTRIBUTING.md, Translations).

The English text in the code is the key: T("…"), Tn(count, "one", "other"), Tc("context", "…"), TKey("…") in C# and
{local:T '…'} in XAML. This script reads those keys and

  check   (default) compares every language file with the keys: missing, stale and empty entries, placeholders,
          plural forms; finds German texts in the user interface code that are not translated.
          --complete en,fr  turns untranslated entries of these languages into errors.
  sync    adds missing keys to every language file (empty) and removes stale ones; translations stay.
  export  writes the keys with their first places in the code as JSON (for translating).
"""
import json, os, re, sys
from collections import OrderedDict

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
SRC = os.path.join(ROOT, 'src')
LANGUAGES_DIR = os.path.join(SRC, 'FlixyPix.Core', 'Languages')
ALLOWLIST = os.path.join(os.path.dirname(__file__), 'i18n-allowlist.txt')
LANGUAGES = ['de', 'fr', 'es', 'it', 'pt', 'nl', 'pl', 'ja', 'ru']
PLURAL_FORMS = {'ja': ['other'], 'pl': ['one', 'few', 'many'], 'ru': ['one', 'few', 'many'],
                **{code: ['one', 'many', 'other'] for code in ('fr', 'es', 'it', 'pt')}}
# Forms that also stand for numbers other than 1 and therefore need the {0} placeholder.
COUNTED_ONE = {'fr', 'pt', 'ru'}
CONTEXT = '\u0004'
# Projects whose texts reach the user; test drivers inside them are skipped.
UI_PROJECTS = ['FlixyPix.Desktop', 'FlixyPix.Core']
SKIP_FILES = re.compile(r'(Desktop\w*SmokeTest|DesktopPerfProbe|DesktopSheetBenchmark|Texts)\.cs$')


def forms_for(language):
    return PLURAL_FORMS.get(language, ['one', 'other'])


# ---------------------------------------------------------------- C# lexer (string literals and comments only)

LITERAL_START = re.compile(r'(\$+@?|@\$+|@)?("""+|")')


class Literal:
    def __init__(self, start, end, prefix, segments, holes, raw):
        self.start, self.end, self.prefix, self.segments, self.holes, self.raw = start, end, prefix, segments, holes, raw

    @property
    def interpolated(self):
        return '$' in self.prefix

    def value(self):
        """Runtime value of a literal without holes; None for interpolated literals with holes."""
        if self.holes:
            return None
        text = ''.join(self.segments)
        if self.raw:
            return text
        if self.interpolated:
            text = text.replace('{{', '{').replace('}}', '}')
        return unescape(text, '@' in self.prefix)


def literals(src, pos=0, end=None):
    end = len(src) if end is None else end
    i = pos
    while i < end:
        c = src[i]
        if src.startswith('//', i):
            j = src.find('\n', i)
            i = end if j < 0 else j
            continue
        if src.startswith('/*', i):
            j = src.find('*/', i + 2)
            i = end if j < 0 else j + 2
            continue
        if c == "'":
            i = skip_char(src, i)
            continue
        m = LITERAL_START.match(src, i)
        if m and (m.group(1) or c == '"') and not (i > 0 and (src[i - 1].isalnum() or src[i - 1] == '_') and m.group(1)):
            literal = read_literal(src, i, m.group(1) or '', m.group(2))
            yield literal
            i = literal.end
            continue
        i += 1


def skip_char(src, i):
    j = i + 1
    j += 2 if src[j] == '\\' else 1
    while src[j] != "'":
        j += 1
    return j + 1


def read_literal(src, start, prefix, quotes):
    dollars = prefix.count('$')
    i = start + len(prefix) + len(quotes)
    segments, holes, seg = [], [], i
    if len(quotes) >= 3:
        while True:
            if src.startswith(quotes, i):
                segments.append(src[seg:i])
                return Literal(start, i + len(quotes), prefix, segments, holes, True)
            if dollars and src.startswith('{' * dollars, i) and not src.startswith('{' * (dollars + 1), i):
                segments.append(src[seg:i])
                close = hole_end(src, i + dollars, '}' * dollars)
                holes.append((i + dollars, close))
                i = seg = close + dollars
                continue
            i += 1
    verbatim = '@' in prefix
    while True:
        c = src[i]
        if verbatim and c == '"':
            if src.startswith('""', i):
                i += 2
                continue
            segments.append(src[seg:i])
            return Literal(start, i + 1, prefix, segments, holes, False)
        if not verbatim:
            if c == '\\':
                i += 2
                continue
            if c == '"':
                segments.append(src[seg:i])
                return Literal(start, i + 1, prefix, segments, holes, False)
        if dollars:
            if src.startswith('{{', i) or src.startswith('}}', i):
                i += 2
                continue
            if c == '{':
                segments.append(src[seg:i])
                close = hole_end(src, i + 1, '}')
                holes.append((i + 1, close))
                i = seg = close + 1
                continue
        i += 1


def hole_end(src, i, close):
    depth = 0
    while True:
        c = src[i]
        if depth == 0 and src.startswith(close, i):
            return i
        if c in '([{':
            depth += 1
        elif c in ')]}':
            depth -= 1
        elif c == "'":
            i = skip_char(src, i)
            continue
        else:
            m = LITERAL_START.match(src, i)
            if m and (m.group(1) or c == '"'):
                i = read_literal(src, i, m.group(1) or '', m.group(2)).end
                continue
        i += 1


ESCAPES = {'n': '\n', 't': '\t', 'r': '\r', '0': '\0', '\\': '\\', '"': '"', "'": "'", 'a': '\a', 'b': '\b', 'f': '\f', 'v': '\v', 'e': '\x1b'}


def unescape(text, verbatim):
    if verbatim:
        return text.replace('""', '"')
    out, i = [], 0
    while i < len(text):
        if text[i] == '\\':
            n = text[i + 1]
            if n == 'u':
                out.append(chr(int(text[i + 2:i + 6], 16)))
                i += 6
                continue
            if n == 'x':
                digits = re.match(r'[0-9A-Fa-f]{1,4}', text[i + 2:]).group(0)
                out.append(chr(int(digits, 16)))
                i += 2 + len(digits)
                continue
            out.append(ESCAPES[n])
            i += 2
            continue
        out.append(text[i])
        i += 1
    return ''.join(out)


# ---------------------------------------------------------------- reading the keys

CALL = re.compile(r'(?<![\w.])(?:Texts\.)?(T|Tn|Tc|TKey)\(')


def code_files(projects=None):
    for project in projects or sorted(os.listdir(SRC)):
        base = os.path.join(SRC, project)
        if not os.path.isdir(base):
            continue
        for folder, dirs, files in os.walk(base):
            dirs[:] = [d for d in dirs if d not in ('bin', 'obj')]
            for name in sorted(files):
                if name.endswith(('.cs', '.xaml')):
                    yield os.path.join(folder, name)


def all_literals(src, pos=0, end=None):
    """Every literal, including those inside the holes of interpolated literals."""
    for literal in literals(src, pos, end):
        yield literal
        for start, close in literal.holes:
            yield from all_literals(src, start, close)


def outside_literals(src):
    """Source with the text of every literal replaced by spaces of the same length, so calls are found only in code
    (also in the holes of interpolated literals)."""
    chars = list(src)

    def blank(pos, end):
        for literal in literals(src, pos, end):
            for k in range(literal.start, literal.end):
                if chars[k] != '\n':
                    chars[k] = ' '
            for start, close in literal.holes:
                chars[start:close] = src[start:close]
                blank(start, close)
    blank(0, len(src))
    return ''.join(chars)


def arguments(src, open_paren):
    """Start offsets of the top-level arguments of the call whose '(' is at open_paren, and the closing offset."""
    starts, depth, i = [open_paren + 1], 0, open_paren + 1
    while True:
        c = src[i]
        if c == "'":
            i = skip_char(src, i)
            continue
        m = LITERAL_START.match(src, i)
        if m and (m.group(1) or c == '"'):
            i = read_literal(src, i, m.group(1) or '', m.group(2)).end
            continue
        if c in '([{':
            depth += 1
        elif c in ')]}':
            if depth == 0:
                return starts, i
            depth -= 1
        elif c == ',' and depth == 0:
            starts.append(i + 1)
        i += 1


def literal_at(src, pos):
    while src[pos].isspace():
        pos += 1
    m = LITERAL_START.match(src, pos)
    if not m or not (m.group(1) or src[pos] == '"'):
        return None
    return read_literal(src, pos, m.group(1) or '', m.group(2))


def extract():
    """keys: key -> {'plural': bool, 'one': str|None, 'places': [..]}; dynamic: places of calls without a literal."""
    keys, dynamic = OrderedDict(), []

    def add(key, place, plural=False, one=None):
        entry = keys.setdefault(key, {'plural': plural, 'one': one, 'places': []})
        if entry['plural'] != plural:
            raise SystemExit(f'{place}: "{key}" is used both as a plural and as a plain text.')
        entry['places'].append(place)

    for path in code_files():
        rel = os.path.relpath(path, ROOT).replace('\\', '/')
        src = open(path, encoding='utf-8-sig').read()
        if path.endswith('.xaml'):
            for m in re.finditer(r"\{local:T '((?:[^'\\]|\\.)*)'\}", src):
                text = re.sub(r'\\(.)', r'\1', m.group(1))
                text = re.sub(r'&#(x?)([0-9A-Fa-f]+);', lambda e: chr(int(e.group(2), 16 if e.group(1) else 10)), text)
                text = text.replace('&amp;', '&').replace('&lt;', '<').replace('&gt;', '>').replace('&quot;', '"')
                add(text, f'{rel}:{src.count(chr(10), 0, m.start()) + 1}')
            continue
        if SKIP_FILES.search(path):
            continue
        code = outside_literals(src)
        for m in CALL.finditer(code):
            name, paren = m.group(1), m.end() - 1
            place = f'{rel}:{src.count(chr(10), 0, m.start()) + 1}'
            starts, _ = arguments(src, paren)
            if name == 'Tn':
                one, other = (literal_at(src, starts[k]) if len(starts) > k else None for k in (1, 2))
                if one and other and one.value() is not None and other.value() is not None:
                    add(other.value(), place, plural=True, one=one.value())
                else:
                    dynamic.append((place, name))
            elif name == 'Tc':
                context, text = (literal_at(src, starts[k]) if len(starts) > k else None for k in (0, 1))
                if context and text and context.value() is not None and text.value() is not None:
                    add(context.value() + CONTEXT + text.value(), place)
                else:
                    dynamic.append((place, name))
            else:
                text = literal_at(src, starts[0])
                if text and text.value() is not None:
                    add(text.value(), place)
                else:
                    dynamic.append((place, name))
    return keys, dynamic


# ---------------------------------------------------------------- German texts outside T

GERMAN_LETTERS = re.compile('[äöüÄÖÜß„“]')
STOPWORDS = set('der die das den dem des und oder nicht mit für von zu zum zur ein eine einen einem einer ist sind wird werden '
                'wurde wurden bitte alle alles kein keine keinen nach bei beim auf im aus noch schon sich als wie nur auch über '
                'unter hier dort neue neuen dieser diese dieses diesem dein deine deinen du dich dir ihre wenn dann sonst jetzt '
                'ohne gibt kann können muss soll mehr weniger erst seit'.split())


def looks_german(text):
    if re.search(r'<[A-Za-z]', text) and '=' in text:
        return False
    if GERMAN_LETTERS.search(text):
        return True
    words = re.findall(r'[A-Za-zÄÖÜäöüß]+', text)
    return ' ' in text.strip() and any(w in STOPWORDS for w in words)


def untranslated():
    """German-looking literals in the user interface code that no T/Tn/Tc/N call wraps."""
    allowed = set()
    if os.path.exists(ALLOWLIST):
        for line in open(ALLOWLIST, encoding='utf-8'):
            line = line.rstrip('\n')
            if line.strip() and not line.startswith('#'):
                path, _, text = line.partition(': ')
                allowed.add((path, json.loads(text)))
    found = []
    for path in code_files(UI_PROJECTS):
        if not path.endswith('.cs') or SKIP_FILES.search(path):
            continue
        rel = os.path.relpath(path, ROOT).replace('\\', '/')
        src = open(path, encoding='utf-8-sig').read()
        wrapped = set()
        code = outside_literals(src)
        for m in CALL.finditer(code):
            starts, close = arguments(src, m.end() - 1)
            count = {'Tn': 3, 'Tc': 2}.get(m.group(1), 1)
            for k, start in enumerate(starts[:count]):
                literal = literal_at(src, start)
                if literal and (m.group(1) != 'Tn' or k > 0):
                    wrapped.add(literal.start)
        for literal in all_literals(src):
            if literal.start in wrapped:
                continue
            # interpolated literals are checked by their text parts
            text = ''.join(literal.segments) if literal.holes else literal.value()
            if text is None or not looks_german(text):
                continue
            if (rel, text) in allowed:
                continue
            found.append((rel, src.count('\n', 0, literal.start) + 1, text))
    return found


# ---------------------------------------------------------------- language files

def load(language):
    path = os.path.join(LANGUAGES_DIR, language + '.json')
    if not os.path.exists(path):
        return OrderedDict()
    return json.load(open(path, encoding='utf-8'), object_pairs_hook=OrderedDict)


def save(language, entries):
    path = os.path.join(LANGUAGES_DIR, language + '.json')
    ordered = OrderedDict(sorted(entries.items(), key=lambda pair: (pair[0].casefold(), pair[0])))
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(json.dumps(ordered, ensure_ascii=False, indent=2) + '\n')


PLACEHOLDER = re.compile(r'(?<!\{)\{(\d+)(?:[,:][^{}]*)?\}(?!\})')


def placeholders(text):
    # Named tokens in the rename-template help are literal user syntax, not string.Format items.
    text = re.sub(r'\{(?:name|index|ext)\}', '', text)
    # Match .NET composite formatting, including literal {{ and }}. A regex alone silently
    # accepts unmatched braces and would allow translations to crash string.Format at runtime.
    result, position = [], 0
    while position < len(text):
        if text[position:position + 2] in ('{{', '}}'):
            position += 2
        elif text[position] == '{':
            match = re.match(r'\{(\d+)(?:,\s*-?\d+\s*)?(?::[^{}]*)?\}', text[position:])
            if not match:
                raise ValueError('invalid composite format at opening brace')
            result.append(match.group(0))
            position += len(match.group(0))
        elif text[position] == '}':
            raise ValueError('unmatched closing brace')
        else:
            position += 1
    return sorted(result)


def numbers(text):
    return {re.match(r'\{(\d+)', value).group(1) for value in placeholders(text)}


def check_entry(language, key, info, value):
    try:
        return check_valid_entry(language, key, info, value)
    except (ValueError, TypeError) as error:
        return [f'{language}: invalid format in "{key}": {error}.'], False


def technical_contracts(source, translation):
    """Literal input syntax must survive translation of the surrounding prose."""
    literals = {
        'The header row has no column “Name”.': ['Name'],
        'Separate name parts with semicolons, e.g. “thumb; folder; cover”.': ['thumb; folder; cover'],
        'Unknown role “{0}” (allowed: Tag, Darsteller, Studio).': ['Tag', 'Darsteller', 'Studio'],
        'Unknown value “{0}” for favorite (ja or nein).': ['ja', 'nein'],
        '“plot” becomes the text below the details.': ['plot'],
    }.get(source, [])
    if 'New-OfflineScenario.ps1' in source:
        literals = literals + [
            '.\\scripts\\original\\New-OfflineScenario.ps1 -Database ..\\mmvdb.sdf -PasswordFile ..\\mmvdb-password.txt',
            'artifacts\\offline-scenario', 'docs\\original\\windows-sandbox-einrichten.md', 'Offline.wsb']
    problems = ['missing literal ' + literal for literal in literals if literal not in translation]
    if '|' in source and '*.' in source:
        wildcards = lambda text: re.findall(r'\*\.[\w*]+', text)
        if wildcards(source) != wildcards(translation) or source.count('|') != translation.count('|'):
            problems.append('file-dialog filter patterns or separators changed')
    return problems


def check_valid_entry(language, key, info, value):
    """Problems of one entry, and whether it is untranslated."""
    problems = []
    shown = key.split(CONTEXT, 1)[-1]
    if info['plural']:
        if not isinstance(value, dict):
            return [f'{language}: "{shown}" is a plural and needs the forms {forms_for(language)}.'], value in (None, '')
        empty = any(not value.get(form) for form in forms_for(language))
        for form, text in value.items():
            if form not in forms_for(language) and form != 'other':
                problems.append(f'{language}: "{shown}" has the form "{form}", which {language} does not use.')
            if not text:
                continue
            problems.extend(f'{language}: "{shown}" ({form}): {issue}.' for issue in technical_contracts(shown, text))
            extra = numbers(text) - numbers(shown) - {'0'}
            if extra:
                problems.append(f'{language}: "{shown}" ({form}) uses unknown placeholders {sorted(extra)}.')
            needed = numbers(shown) - ({'0'} if form == 'one' and language not in COUNTED_ONE else set())
            if needed - numbers(text):
                problems.append(f'{language}: "{shown}" ({form}) lacks the placeholders {sorted(needed - numbers(text))}.')
            source_formats = set(placeholders(shown))
            if set(placeholders(text)) - source_formats:
                problems.append(f'{language}: "{shown}" ({form}) changes a placeholder format.')
        return problems, empty
    if isinstance(value, dict):
        return [f'{language}: "{shown}" is a plain text, not a plural.'], False
    if not value:
        return [], True
    problems.extend(f'{language}: "{shown}": {issue}.' for issue in technical_contracts(shown, value))
    if placeholders(value) != placeholders(shown):
        problems.append(f'{language}: placeholders differ in "{shown}" → "{value}".')
    if sorted(re.findall(r'\{(?:name|index|ext)\}', shown)) != sorted(re.findall(r'\{(?:name|index|ext)\}', value)):
        problems.append(f'{language}: rename-template tokens differ in "{shown}".')
    return problems, False


def check(complete):
    keys, dynamic = extract()
    errors, report = [], []
    for language in LANGUAGES:
        entries = load(language)
        missing = [k for k in keys if k not in entries]
        stale = [k for k in entries if k not in keys]
        untranslated_count = 0
        for key, info in keys.items():
            if key not in entries:
                continue
            problems, empty = check_entry(language, key, info, entries[key])
            errors.extend(problems)
            untranslated_count += empty
        if missing:
            errors.append(f'{language}: {len(missing)} keys missing (run: python scripts/checks/i18n.py sync), e.g. "{missing[0]}".')
        if stale:
            errors.append(f'{language}: {len(stale)} stale entries (run: python scripts/checks/i18n.py sync), e.g. "{stale[0]}".')
        if language in complete and untranslated_count:
            errors.append(f'{language}: {untranslated_count} entries are not translated yet.')
        report.append(f'{language} {len(keys) - untranslated_count - len(missing)}/{len(keys)}')
    leftovers = untranslated()
    for path, line, text in leftovers:
        errors.append(f'{path}:{line}: German text outside T(…): {json.dumps(text, ensure_ascii=False)}')
    print(f'{len(keys)} texts · translated: ' + ' · '.join(report))
    if dynamic:
        print(f'{len(dynamic)} calls with computed texts (their keys must appear elsewhere, for example with TKey("…")).')
    for error in errors:
        print('ERROR ' + error)
    return 1 if errors else 0


def sync():
    keys, _ = extract()
    for language in LANGUAGES:
        entries = load(language)
        result = OrderedDict()
        for key, info in keys.items():
            value = entries.get(key)
            if info['plural']:
                old = value if isinstance(value, dict) else {}
                result[key] = OrderedDict((form, old.get(form, '')) for form in forms_for(language))
            else:
                result[key] = value if isinstance(value, str) else ''
        removed = [k for k in entries if k not in keys]
        save(language, result)
        print(f'{language}: {len(result)} entries, {len(removed)} removed')


def export(target):
    keys, _ = extract()
    data = [{'key': k, 'one': v['one'], 'plural': v['plural'], 'places': v['places'][:3]} for k, v in keys.items()]
    with open(target, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
    print(f'{len(data)} keys written to {target}')


if __name__ == '__main__':
    command = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith('--') else 'check'
    if command == 'sync':
        sync()
    elif command == 'export':
        export(sys.argv[2])
    else:
        complete = set()
        if '--complete' in sys.argv:
            complete = set(sys.argv[sys.argv.index('--complete') + 1].split(','))
        sys.exit(check(complete))
