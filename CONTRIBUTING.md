# Contributing to FlixyPix

Thank you for your interest. FlixyPix is maintained by one person in their spare time, so
answers can take a while. Small, focused contributions are the easiest to review.

## Reporting problems

Open an issue with the FlixyPix version, your Windows version, what you did, what you expected
and what happened. Attach the error log if there is one (`%LOCALAPPDATA%\FlixyPix\logs`).

Please do not attach catalogs, backups, screenshots of private media or file lists you do not
want to make public. Report security problems privately, see [SECURITY.md](SECURITY.md).

## Translations

Texts in the code are English and serve as keys. Each other language has one file,
`src/FlixyPix.Core/Languages/<code>.json`, that maps the English text to the translation.
Placeholders such as `{0}` and `{0:N0}` must stay as they are.

To correct a translation, change the value in the language file and open a pull request. Say
briefly why the new wording is better. You can also try a correction without building: copy the
file to `%LOCALAPPDATA%\FlixyPix\languages\<code>.json` and restart FlixyPix.

`python scripts/checks/i18n.py --complete de,fr,es,it,pt,nl,pl,ja,ru` checks that every language
covers every text.

## Code

1. Build and test with `.\scripts\bootstrap.ps1` and `.\scripts\build.ps1` (see the README).
2. Keep a change to one topic and match the style of the surrounding code.
3. Add or adjust tests for changed behavior. Tests use synthetic data only.
4. New user-visible texts go through `T("…")`; add their translations (or leave them to the
   maintainer, the check above lists what is missing).
5. Commit messages: a short summary line in English, then a body if the reason is not obvious.

By contributing you agree that your contribution is licensed under the GPL-3.0-or-later, and,
for `src/FlixyPix.LegacyExporter`, also under the MIT License.
