"""Exercise the build's companion-file copy against static and shared mpv layouts."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


@unittest.skipUnless(os.name == 'nt', 'The Windows build uses PowerShell')
class MpvPackagingTests(unittest.TestCase):
    def test_static_and_shared_builds_copy_companions_without_requiring_dlls(self):
        build = (Path(__file__).parents[1] / 'build.ps1').read_text(encoding='utf-8-sig')
        command = next(line.strip() for line in build.splitlines()
                       if "Get-ChildItem -LiteralPath '.tools\\media\\mpv' -File" in line)
        for files in [[], ['libbluray.jar'], ['decoder.dll', 'libbluray.jar', 'mpv-register.bat']]:
            with self.subTest(files=files), tempfile.TemporaryDirectory(prefix='flixypix-mpv-copy-') as root:
                root = Path(root)
                source = root / '.tools' / 'media' / 'mpv'
                source.mkdir(parents=True)
                (root / 'output').mkdir()
                for name in files:
                    (source / name).write_bytes(b'synthetic companion')
                result = subprocess.run(
                    ['powershell', '-NoProfile', '-NonInteractive', '-Command',
                     "$ErrorActionPreference='Stop'; $tools='output'; " + command],
                    cwd=root, capture_output=True, text=True)
                self.assertEqual(0, result.returncode, result.stderr)
                expected = {name for name in files if name.endswith(('.dll', '.jar'))}
                self.assertEqual(expected, {p.name for p in (root / 'output').iterdir()})
                for copied in (root / 'output').iterdir():
                    self.assertEqual(b'synthetic companion', copied.read_bytes())
