# FlixyPix 0.5.0 — Windows x64

Dies ist eine portable Ausgabe für Windows 10/11 (64 Bit), ohne Installer und ohne Codesignatur. Windows SmartScreen kann beim ersten Start eine Bestätigung verlangen. Sichere Katalog und Mediendateien vor einem Update. Die Version 0.5.0 verwendet Katalogschema 17; vor einer Umstellung wird eine Migrationssicherung angelegt. Ältere Programmversionen können einen aktualisierten Katalog möglicherweise nicht mehr öffnen.

Die Oberfläche folgt der Windows-Anzeigesprache. Einstellungen → Darstellung → Sprache erlaubt eine feste Wahl nach Neustart. Zahlen und Datum behalten das Windows-Regionalformat. Die Übersetzungen sind KI-gestützt entstanden und geprüft, aber nicht von Muttersprachlern; Korrekturen sind willkommen.

Unter `tag-vocabularies` liegt für jede der zehn Sprachen ein `.flixytags.csv`-Paket mit denselben 62 Adult-Genres. Im Verzeichnis über den Paketimport auswählen und die Vorschau prüfen. Der Import ergänzt Vokabular und Rollen; vorhandene Medien werden nicht automatisch getaggt. Ein Sprachwechsel benennt bestehende Tags nicht um.

„Adult Content Pack“ im Verzeichnis bietet mit einer installierten EazyFlixPix 4 außerdem deren Darsteller- und Studiolisten und die Originalnamen der Genres („English (original)“) an. FlixyPix liest sie aus dieser Installation und liefert sie nicht mit; nur dann erscheint das Angebot auch auf der leeren Startseite. Ein abgeschlossener Import lässt sich nur im noch offenen Dialog rückgängig machen.

`FlixyPix.exe` startet die lokale Medienbibliothek. Beim ersten normalen Start werden bisherige Einstellungen erkannt und eine frühere Standardbibliothek kopiert; die bisherigen Daten bleiben erhalten. Eigene Bibliotheksordner werden weiterverwendet. Die .NET-Laufzeit ist enthalten. Den Unterordner `tools` mit FFmpeg, ffprobe und mpv zusammen mit der Anwendung aufbewahren.

Dateien und Ordner importieren oder auf das Fenster ziehen. Beim Import bleiben Quelldateien an ihrem Ort. Bilder, Tags, Bewertungen, Beschreibungen, Kategorien, Sammlungen und Suchvorgaben werden im eigenen SQLite-Katalog gespeichert. Automatische Vorschauen und Bildseiten verwenden einen gemeinsamen Decoder.

- **Medienordner:** Hinzufügen ▾ → Medienordner … gleicht Ordner mit dem Katalog ab. Neue Dateien werden angeboten, verschobene und umbenannte neu zugeordnet, fehlende gemeldet; Quelldateien bleiben unverändert. Beobachtete Ordner meldet ein Hinweisbalken, F5 prüft sofort.
- **Ordnen:** Erweiterte Suche, Vokabular, Ordner-/Tagfavoriten, Dateinamenparser, Tagregeln und Dubletten.
- **Katalog:** Migration in eine neue Bibliothek, Snapshot-Abgleich mit Berichten, JSON/CSV, ZIP-/RAR-Bildarchive und vollständige Sicherungen.
- **Aufträge:** Konvertierung, Clips, GIFs, Zuschnitt, Videoverbindung und eingereihte Szenenbilder.
- **Werkzeuge:** Mediengrafiken, Szenenvorschaugenerator, NFO, Dateiaktionen und Sammlungen. Unter Dateiverwaltung sind Stapelaktionen für die mit Strg/Umschalt markierten Medien verfügbar.

Der Player bietet Pause, Seek, Lautstärke, Geschwindigkeit, Audio-/Untertitelwahl, Vollbild und zeitabhängige Ansichten. Szenen und Bookmarks lassen sich bearbeiten, ordnen und mit gespeicherten Grafiken verknüpfen. Bildfenster bieten Zoom, Rotation, Seiten-/Framewechsel und Animation mit Pause.

Katalog: `%LOCALAPPDATA%\FlixyPix\Library`.
Einstellungen: `%LOCALAPPDATA%\FlixyPix\appsettings.json`.
Ein anderer Bibliotheksordner wird nach Neustart wirksam. Ältere Kataloge erhalten vor der Schemamigration eine konsistente Sicherung.

JSON-Version 4 enthält Katalog, gespeicherte Grafikbytes, historische Entwürfe und bewahrte Binärdaten in geprüften Abschnitten. Ältere Versionen 1, 2 und 3 bleiben lesbar. Externe Mediendateien werden nur auf ausdrückliche Auswahl im vollständigen `.flixypixbackup`-Paket mitgesichert. Wiederherstellung prüft alle Prüfsummen und benötigt einen neuen Zielordner. Der bestehende Katalog wird dabei nicht ersetzt.

U: ist als schreibgeschützte Quelle voreingestellt. Ein offline geschaltetes Medienlaufwerk verhindert keine Katalogmigration. Der Import erhält seine Pfade und bereitet keine Medien automatisch vor. Historische Such-, Parser- und Auftragsentwürfe bleiben inaktiv. Tagbilder lassen sich unter Ordnen verwalten.

Menü → Hilfe (F1) öffnet die Offline-Hilfe; Strg+F1 zeigt die aktuellen Tastenkürzel. Menü → Über FlixyPix zeigt die Version.

Strg+O importiert, Strg+F sucht, Strg+S speichert Details, F5 prüft die Medienordner. Eingabe öffnet ein Medium; Entf entfernt den Katalogeintrag. Dateien endgültig zu löschen ist eine eigene bestätigte Aktion.

Dies ist neu geschriebene Software, keine vollständige Paritätsbescheinigung für EazyFlixPix 4.8.4. Der Funktionsvergleich mit dem Original steht noch aus. Die Originalanwendung wird von dieser Ausgabe nicht gestartet; eine EazyFlixPix-Bibliothek übernimmt **Hinzufügen ▾ → Bibliothek aus EazyFlixPix 4.8.4 übernehmen …** mit einem mitgelieferten Werkzeug aus einer Kopie der Datenbank. Dafür braucht es Microsoft SQL Server Compact 4.0, das FlixyPix nicht mitliefert: entweder eine vorhandene EazyFlixPix-Installation oder das Microsoft-Paket SQL Server Compact 4.0 SP1 (x86). Bibliotheken lassen sich optional mit eigenem Passwort verschlüsseln. Onlineanbieter und Live-TV fehlen.

FlixyPix ist freie Software unter der GNU General Public License, Version 3 oder später, siehe `LICENSE`. Das Werkzeug unter `legacy-import` steht zusätzlich unter der MIT-Lizenz. Drittkomponenten behalten ihre eigenen Lizenzen: Übersicht in `THIRD-PARTY-NOTICES.md`, Texte unter `tools/notices`. Quellcode: https://github.com/Kirmalix/flixypix. FlixyPix ist ein unabhängiges Projekt ohne Verbindung zu den Autoren von EazyFlixPix.

Unter Katalog → Altdaten / Migration → Historische Funktionen übernehmen lassen sich belegte Such-, Parser- und Auftragsvorgaben nach Vorschau ausdrücklich übernehmen. Unbekannte aktive Optionen bleiben blockiert. Übernommene Aufträge starten erst nach dem separaten Start im Warteschlangenfenster. Alte Tagbildordner können im Tagbildfenster mit Zuordnungsvorschau kopiert werden.
