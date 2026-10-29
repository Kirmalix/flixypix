import contextlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'checks'))
import translation_review as review


class TranslationReviewTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.language = self.root / 'src/FlixyPix.Core/Languages/en.json'
        self.language.parent.mkdir(parents=True)
        self.language.write_bytes(b'{\r\n  "{0} Dateien": "{0} files",\r\n  "Titel": "Title"\r\n}\r\n')
        self.keys = {'{0} Dateien': {'one': None, 'plural': False, 'places': []},
                     'Titel': {'one': None, 'plural': False, 'places': []}}
        self.extract = patch.object(review.i18n, 'extract', return_value=(self.keys, []))
        self.extract.start()
        self.addCleanup(self.extract.stop)

    def batch(self):
        items = review.inventory(self.root, 'ui', 'en')
        for index, item in enumerate(items):
            item.update(id=index, digest=review.fingerprint(item))
        return {'area': 'ui', 'language': 'en', 'items': items}

    def test_unreviewed_entries_never_count_as_approved(self):
        with contextlib.redirect_stdout(io.StringIO()), patch.object(review.i18n, 'LANGUAGES', ['en']), \
                patch.object(review, 'inventory', return_value=self.batch()['items']):
            self.assertEqual(1, review.summarize(self.root, require_complete=True))

    def test_decisions_must_cover_each_snapshot_item_once(self):
        for decisions in ({'unchanged': [0]}, {'unchanged': [0, 1], 'corrected': {'1': 'Heading'}}):
            with self.subTest(decisions=decisions), self.assertRaises(ValueError):
                review.apply(self.root, self.batch(), decisions)
        self.assertFalse((self.root / review.REGISTER).exists())

    def test_changed_snapshot_rejected_without_overwriting_concurrent_edit(self):
        batch = self.batch()
        self.language.write_text(json.dumps({'{0} Dateien': '{0} documents', 'Titel': 'Title'}), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'changed since snapshot'):
            review.apply(self.root, batch, {'unchanged': [0, 1]})
        self.assertEqual('{0} documents', review.read_json(self.language)['{0} Dateien'])

    def test_invalid_placeholder_correction_cannot_be_recorded(self):
        before = self.language.read_bytes()
        with self.assertRaisesRegex(ValueError, 'Invalid correction'):
            review.apply(self.root, self.batch(), {'unchanged': [1], 'corrected': {'0': '{1} files'}})
        self.assertEqual(before, self.language.read_bytes())
        self.assertFalse((self.root / review.REGISTER).exists())

    def test_unrelated_concurrent_edit_during_validation_is_preserved(self):
        batch = self.batch()
        check = review.i18n.check_entry
        def concurrent_edit(*args):
            review.write_json(self.language, {'{0} Dateien': '{0} files', 'Titel': 'Changed concurrently', 'Neu': 'New'})
            return check(*args)
        with patch.object(review.i18n, 'check_entry', side_effect=concurrent_edit), \
                self.assertRaisesRegex(ValueError, 'changed during apply'):
            review.apply(self.root, batch, {'unchanged': [0], 'corrected': {'1': 'Heading'}})
        self.assertEqual('Changed concurrently', review.read_json(self.language)['Titel'])
        self.assertEqual('New', review.read_json(self.language)['Neu'])
        self.assertFalse((self.root / review.REGISTER).exists())

    def test_content_and_singular_changes_invalidate_the_record(self):
        item = self.batch()['items'][0]
        original = review.fingerprint(item)
        item['source']['one'] = 'Eine Datei'
        self.assertNotEqual(original, review.fingerprint(item))
        item['source']['one'] = None
        item['value'] = '{0} documents'
        self.assertNotEqual(original, review.fingerprint(item))

    def test_new_source_key_is_open_even_before_language_sync(self):
        self.keys['Neu'] = {'one': None, 'plural': False, 'places': []}
        batch = self.batch()
        self.assertEqual('Neu', batch['items'][2]['key'])
        self.assertIsNone(batch['items'][2]['value'])
        with self.assertRaisesRegex(ValueError, 'missing translation'):
            review.apply(self.root, batch, {'unchanged': [0, 1, 2]})
        review.apply(self.root, batch, {'unchanged': [0, 1], 'corrected': {'2': 'New'}})
        self.assertEqual('New', review.read_json(self.language)['Neu'])

    def test_correction_preserves_crlf_and_other_entries_and_records_fingerprint(self):
        review.apply(self.root, self.batch(), {'unchanged': [0], 'corrected': {'1': 'Heading'}})
        raw = self.language.read_bytes()
        self.assertNotIn(b'\n', raw.replace(b'\r\n', b''))
        self.assertEqual('{0} files', review.read_json(self.language)['{0} Dateien'])
        records = review.load_register(self.root)['ui']['en']
        self.assertEqual('corrected', records['Titel']['status'])
        self.assertEqual(review.fingerprint(self.batch()['items'][1]), records['Titel']['digest'])
        review.apply(self.root, self.batch(), {'unchanged': [0, 1]})
        self.assertEqual('corrected', review.load_register(self.root)['ui']['en']['Titel']['status'])

    def test_language_shards_do_not_overwrite_another_review(self):
        other = self.root / review.REGISTER / 'ui-fr.json'
        review.write_json(other, {'version': 1, 'entries': {'other': {'status': 'uncertain', 'digest': 'a'}}})
        before = other.read_bytes()
        review.apply(self.root, self.batch(), {'unchanged': [0, 1]})
        self.assertEqual(before, other.read_bytes())

    def test_pending_snapshot_finds_inserted_and_changed_keys_by_content(self):
        review.apply(self.root, self.batch(), {'unchanged': [0, 1]})
        self.keys['Neu'] = {'one': None, 'plural': False, 'places': []}
        review.write_json(self.language, {'Neu': 'New', '{0} Dateien': '{0} files', 'Titel': 'Heading'})
        target = self.root / 'pending.json'
        with contextlib.redirect_stdout(io.StringIO()):
            review.snapshot(self.root, 'ui', 'en', 0, 100, target, pending=True)
        items = review.read_json(target)['items']
        self.assertEqual(['Neu', 'Titel'], [item['key'] for item in items])
        self.assertEqual([0, 2], [item['id'] for item in items])

    def test_tag_correction_keeps_bom_csv_contract_and_concept_count(self):
        base = self.root / 'assets/tag-vocabularies'
        base.mkdir(parents=True)
        header = '# FlixyPix-Tagpaket 1\r\nRolle;Kategorie;Name;Aliasse;Favorit;Beschreibung\r\n'
        for language, row in [('de', 'Tag;Themen;Natur;;false;\r\n'), ('en', 'Tag;Themes;Natural;;false;\r\n')]:
            (base / (language + '.flixytags.csv')).write_bytes((header + row).encode('utf-8-sig'))
        item = review.inventory(self.root, 'tags', 'en')[0]
        item.update(id=0, digest=review.fingerprint(item))
        replacement = dict(item['value'], Name='Nature')
        review.apply(self.root, {'area': 'tags', 'language': 'en', 'items': [item]}, {'corrected': {'0': replacement}})
        comments, fields, rows, raw = review.read_tags(base / 'en.flixytags.csv')
        self.assertTrue(raw.startswith(b'\xef\xbb\xbf'))
        self.assertEqual(1, len(rows))
        self.assertEqual('Nature', rows[0]['Name'])
        self.assertEqual('Tag', rows[0]['Rolle'])
        self.assertEqual('false', rows[0]['Favorit'])
        self.assertEqual('# FlixyPix-Tagpaket 1\r\n', comments)


if __name__ == '__main__':
    unittest.main()
