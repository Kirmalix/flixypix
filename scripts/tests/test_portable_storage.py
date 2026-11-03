import importlib.util
from pathlib import Path
import unittest
spec = importlib.util.spec_from_file_location('portable', Path(__file__).parents[1] / 'release/package-portable.py')
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)
class PortableStorageTests(unittest.TestCase):
    def test_runtime_files_never_enter_update_zip(self):
        for name in ['Data/catalog.db','Data/unknown.bin','storage-mode.json','storage-mode.last-good.json','appsettings.json','appsettings.last-good.json','logs/error.log','temp/input.conf','.storage-switch/journal.json','.storage-access.lock','.storage-switch.completed-abc/0.data','Data/Library/.relocation-pending/journal.json']:
            with self.subTest(name=name), self.assertRaises(ValueError):
                package.validate_runtime_path(name)
    def test_program_files_are_allowed(self):
        for name in ['FlixyPix.exe','FlixyPix.runtimeconfig.json','portable.flag','tools/mpv.exe','tag-vocabularies/en.flixytags.csv']:
            package.validate_runtime_path(name)

    def test_extracting_update_keeps_all_runtime_files_byte_identical(self):
        import tempfile, json, zipfile
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as work:
            root = Path(work)
            (root / 'Directory.Build.props').write_text('<Project><InformationalVersion>test</InformationalVersion></Project>')
            source = root / 'artifacts/portable-FlixyPix'
            (source / 'tools/notices/dependencies').mkdir(parents=True)
            files = {'FlixyPix.exe': b'new-program', 'portable.flag': b'', 'tools/notices/dependencies/dependencies.json': b'{}'}
            for name, data in files.items():
                (source / name).write_bytes(data)
            (source / 'release-files.json').write_text(json.dumps(list(files)))
            with patch.object(package, 'ROOT', root):
                package.package()
            destination = root / 'installed'
            runtime = {'Data/appsettings.json': b'preferences', 'Data/Library/catalog.db': b'catalog', 'Data/custom-unknown.file': b'private', 'storage-mode.json': b'{"mode":"profile"}', 'my-notes.txt': b'personal'}
            for name, data in runtime.items():
                path = destination / 'FlixyPix' / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(data)
            with zipfile.ZipFile(root / 'artifacts/FlixyPix-test-win-x64.zip') as archive:
                archive.extractall(destination)
            for name, data in runtime.items():
                self.assertEqual(data,(destination / 'FlixyPix' / name).read_bytes())
