import importlib.util
import json
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('package_source', Path(__file__).parents[1] / 'release' / 'package-source.py')
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)

class SourcePackageTests(unittest.TestCase):
    def test_arbitrarily_named_private_report_or_catalog_is_rejected(self):
        for format_name in [prefix + suffix for prefix in ['flixypix', 'eazyflixpix'] for suffix in ['-migration-report', '-migration-audit', '-catalog', '-legacy', '-backup', '-original-reference', '-offline-input-v2', '-legacy-validation-v1']]:
            with self.subTest(format=format_name), self.assertRaises(ValueError):
                package.validate_public_data('docs/innocent-name.json', json.dumps({'format': format_name}).encode())

    def test_exchange_requires_documented_test_fixture_location(self):
        with self.assertRaises(ValueError):
            package.validate_public_data('docs/export.jsonl', b'{"type":"header"}\n')
        package.validate_public_data('tests/Core.Tests/Fixtures/synthetic.jsonl', b'{"type":"header"}\n')

    def test_dependency_metadata_is_allowed(self):
        package.validate_public_data('src/Core/packages.lock.json', b'{"version":1,"dependencies":{}}')

    def test_internal_folders_and_binaries_are_rejected_even_when_listed(self):
        for path in ['archive/manifest.json', 'evidence/static-summary.json', 'src/Core/bin/Core.dll', 'tests/tool.exe', 'docs/mmvdb-password.txt']:
            with self.subTest(path=path), self.assertRaises(ValueError):
                package.validate_public_path(path)

    def test_real_user_profile_paths_are_rejected(self):
        with self.assertRaises(ValueError):
            # Assembled here so that this file itself passes the check.
            package.validate_public_data('src/Core/Paths.cs', b'var media = @"C:' + rb'\Users\someone\Videos";')
        package.validate_public_data('src/Core/Paths.cs', rb'// e.g. C:\Users\<name>\Videos or C:\Users\Public\Videos')

    def test_allowlist_selects_files_and_folders_and_honours_exclusions(self):
        rules = package.read_allowlist('# comment\nREADME.md\nsrc/\n!src/private.cs\n')
        selected = package.select(rules, ['README.md', 'src/a.cs', 'src/private.cs', 'docs/plan.md', 'srcx/b.cs'])
        self.assertEqual(['README.md', 'src/a.cs'], selected)

    def test_stale_allowlist_entry_is_reported(self):
        with self.assertRaisesRegex(ValueError, 'CHANGELOG.md'):
            package.select(package.read_allowlist('README.md\nCHANGELOG.md\n'), ['README.md'])

    def test_small_private_blob_sidecars_are_rejected(self):
        for path in ['docs/tiny.bin', 'docs/catalog.jsonl.blobs/hash.dat', 'docs/private.ezl']:
            with self.subTest(path=path), self.assertRaises(ValueError):
                package.validate_public_data(path, b'private binary bytes')

if __name__ == '__main__':
    unittest.main()
