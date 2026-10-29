import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'checks'))
import i18n  # noqa: E402


class LexerTests(unittest.TestCase):
    def test_literals_include_holes_and_skip_comments(self):
        src = '// "Kommentar"\nvar a = $"{(n == 1 ? T("eins") : T("viele"))} Ende"; var b = @"C:\\x""y";'
        values = [lit.value() for lit in i18n.all_literals(src)]
        self.assertNotIn('Kommentar', values)
        self.assertIn('eins', values)
        self.assertIn('viele', values)
        self.assertIn('C:\\x"y', values)

    def test_calls_inside_holes_are_found(self):
        src = 'x = $"{T("Ton")} · {Tn(n, "1 Medium", "{0:N0} Medien")}";'
        code = i18n.outside_literals(src)
        self.assertEqual(['T', 'Tn'], [m.group(1) for m in i18n.CALL.finditer(code)])

    def test_unescape(self):
        self.assertEqual('a\n"b"\u0001', i18n.unescape('a\\n\\"b\\"\\u0001', False))


class EntryTests(unittest.TestCase):
    plural = {'plural': True}
    plain = {'plural': False}

    def test_placeholders_must_match(self):
        problems, empty = i18n.check_entry('en', '{0:N0} von {1:N0}', self.plain, '{0:N0} of {1:N0}')
        self.assertEqual(([], False), (problems, empty))
        problems, _ = i18n.check_entry('en', '{0:N0} von {1:N0}', self.plain, '{0:N0} of')
        self.assertTrue(problems)

    def test_plural_forms_per_language(self):
        problems, empty = i18n.check_entry('pl', '{0:N0} items', self.plural, {'one': '1 element', 'few': '{0:N0} elementy', 'many': '{0:N0} elementów'})
        self.assertEqual(([], False), (problems, empty))
        # Polish without "few" and "many" is not translated yet.
        problems, empty = i18n.check_entry('pl', '{0:N0} items', self.plural, {'one': '1 element', 'other': '{0:N0} elementów'})
        self.assertEqual(([], True), (problems, empty))
        # Russian "one" also stands for 21, 31 … and needs the count.
        problems, _ = i18n.check_entry('ru', '{0:N0} items', self.plural, {'one': '1 элемент', 'few': '{0:N0} элемента', 'many': '{0:N0} элементов'})
        self.assertTrue(problems)
        problems, _ = i18n.check_entry('en', '{0:N0} items', self.plural, {'one': '1 item', 'other': '{0:N0} items'})
        self.assertEqual([], problems)

    def test_empty_counts_as_untranslated(self):
        self.assertEqual(([], True), i18n.check_entry('fr', 'Speichern', self.plain, ''))

    def test_format_syntax_and_numeric_formats_are_preserved(self):
        for translation in ('{0:N0} {', '{0:N0} }', '{0:F2}', '{1:N0}'):
            with self.subTest(translation=translation):
                self.assertTrue(i18n.check_entry('en', '{0:N0}', self.plain, translation)[0])
        self.assertEqual(([], False), i18n.check_entry('en', '{0} / {1:N0}', self.plain, '{1:N0} / {0}'))
        self.assertEqual(['{0}'], i18n.placeholders('{{literal}} {0}'))
        self.assertTrue(i18n.check_entry('fr', 'Vorlage {name}', self.plain, 'Modèle {nom}')[0])

    def test_million_form_is_required_for_romance_languages(self):
        for language in ('fr', 'es', 'it', 'pt'):
            forms = {'one': '{0:N0} item', 'other': '{0:N0} items'}
            self.assertTrue(i18n.check_entry(language, '{0:N0} items', self.plural, forms)[1])
            forms['many'] = '{0:N0} items'
            self.assertEqual(([], False), i18n.check_entry(language, '{0:N0} items', self.plural, forms))

    def test_german_detection(self):
        self.assertTrue(i18n.looks_german('Einstellungen für die Bibliothek'))
        self.assertTrue(i18n.looks_german('Größe'))
        self.assertFalse(i18n.looks_german('Invalid record.'))
        self.assertFalse(i18n.looks_german('<TextBlock Text="Größe"/>'))

    def test_file_dialog_labels_translate_but_patterns_and_separators_do_not(self):
        source = 'Bilder|*.jpg;*.png|Alle Dateien|*.*'
        self.assertEqual(([], False), i18n.check_entry('fr', source, self.plain, 'Images|*.jpg;*.png|Tous les fichiers|*.*'))
        for bad in ('Images|*.jpeg;*.png|Tous les fichiers|*.*', 'Images|*.png;*.jpg|Tous les fichiers|*.*',
                    'Images|*.jpg;*.png;Tous les fichiers|*.*'):
            with self.subTest(bad=bad):
                self.assertTrue(i18n.check_entry('fr', source, self.plain, bad)[0])

    def test_csv_contract_names_are_not_translated_with_the_prose(self):
        source = 'The header row has no column “Name”.'
        self.assertEqual(([], False), i18n.check_entry('fr', source, self.plain, 'La colonne « Name » est absente.'))
        self.assertTrue(i18n.check_entry('fr', source, self.plain, 'La colonne « Nom » est absente.')[0])
        source = 'Unknown role “{0}” (allowed: Tag, Darsteller, Studio).'
        self.assertTrue(i18n.check_entry('en', source, self.plain, 'Unknown role {0} (allowed: Tag, Performer, Studio).')[0])

    def test_filename_examples_and_offline_commands_retain_literal_syntax(self):
        source = 'Separate name parts with semicolons, e.g. “thumb; folder; cover”.'
        self.assertTrue(i18n.check_entry('fr', source, self.plain, 'Exemple : miniature; dossier; couverture.')[0])
        command = '.\\scripts\\original\\New-OfflineScenario.ps1 -Database ..\\mmvdb.sdf -PasswordFile ..\\mmvdb-password.txt'
        source = '\n'.join([command, 'artifacts\\offline-scenario', 'docs\\original\\windows-sandbox-einrichten.md', 'Offline.wsb'])
        self.assertEqual(([], False), i18n.check_entry('en', source, self.plain, 'Instructions:\n' + source))
        self.assertTrue(i18n.check_entry('en', source, self.plain, source.replace('-Database ..', '-Database..'))[0])


if __name__ == '__main__':
    unittest.main()
