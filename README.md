# IslandHub

**Eine Dynamic Island für Windows 10 und 11.** Eine kleine schwarze Insel oben in der Mitte des Bildschirms zeigt, was gerade passiert – Musik, Nachrichten, Timer, Downloads, wer dein Mikrofon nutzt – und öffnet sich zu kleinen Seiten, wenn du mit der Maus darauf zeigst.

<p align="center"><a href="https://snipez5768.github.io/IslandHub/#feder"><img src="docs/img/island-open-v2.svg" alt="Die Insel öffnet sich zur Musik-Seite" width="100%"></a></p>

**[▶ Alle Animationen zum Ausprobieren – mit Reglern](https://snipez5768.github.io/IslandHub/)** · **[⬇ Download](https://github.com/Snipez5768/IslandHub/releases/latest)**

> GitHub zeigt hier nur Bilder. **Klick auf eine Animation** – dann öffnet sie sich auf der Demo-Seite zum Selbst-Ausprobieren.

---

## Was sie kann

| | |
|---|---|
| 🎵 **Musik** | Cover, Titel, Spulen, Weiter/Zurück aus jeder App (Spotify, YouTube im Browser …). Bei Spotify Warteschlange und eigene Lautstärke. |
| 📋 **Zwischenablage** | Verlauf mit Text, Links, Bildern und Dateien, Suche, Anheften. Stapel-Modus: mehrmals kopieren, jedes Strg+V fügt das nächste ein. |
| 📸 **Bildschirmfoto** | Bereich, Fenster, Bildschirm, GIF – mit Editor und Texterkennung (offline). |
| 🔔 **Mitteilungen** | Spiegelt Windows-Benachrichtigungen. Erst eine schmale Laufzeile, mit der Maus darauf die volle Karte. Codes aus Nachrichten mit einem Klick kopieren. |
| ⏱ **Timer · Wecker** | Räder wie beim iPhone, ein eingefräster Umschalter zwischen Dauer und Uhrzeit. |
| 🎙 **Live** | Wer gerade Mikrofon, Kamera oder Bildschirm nutzt – mit Stummschalten, Discord-Anruf und Stream. |
| ✳ **Agent** | Fortschritt von Claude Code direkt in der Insel. |
| 🎮 **Spiele** | Spiele und Programme mit einem Klick starten, Steam-Kachel mit allen Spielen per Rechtsklick. |
| ⬇ **Downloads** | Steam- und Browser-Downloads mit Fortschrittsring. |
| ✉️ **Bestätigungscodes** | Fragt eine Seite nach einem Code, holt die Insel deinen E-Mail-Tab nach vorne. |

## Animationen

Alles bewegt sich auf einer Feder wie bei iOS – nichts fliegt über den Bildschirm, alles passiert an der Insel.

### Nachricht
Erst schwingt eine Glocke, dann wird die Insel zu einer schmalen Laufzeile. Mit der Maus darauf öffnet sich die ganze Karte.

<a href="https://snipez5768.github.io/IslandHub/#nachricht"><img src="docs/img/notification-v2.svg" width="100%" alt="Nachricht: Glocke, Laufzeile, Karte"></a>

### Überspringen
**Weiter:** der Balken läuft bis zum Ende, das Cover schiebt sich von links nach rechts. **Zurück:** der Balken springt auf voll und läuft nach links, das Cover kommt von rechts.

<a href="https://snipez5768.github.io/IslandHub/#skip"><img src="docs/img/song-skip-v2.svg" width="100%" alt="Song überspringen"></a>

### Neuer Song
In der geschlossenen Insel gleitet das alte Cover raus, das neue federt rein, wird kurz größer und färbt die Insel.

<a href="https://snipez5768.github.io/IslandHub/#song"><img src="docs/img/song-change-v3.svg" width="100%" alt="Neuer Song"></a>

### Timer | Wecker
Der Seitenname ist ein eingefräster Umschalter – dieselben Räder stellen eine Dauer oder eine Uhrzeit.

<a href="https://snipez5768.github.io/IslandHub/#wecker"><img src="docs/img/timer-wecker-v2.svg" width="100%" alt="Timer und Wecker"></a>

### … hört zu
Nutzt eine App dein Mikrofon, sagt die Insel es selbst. Danach bleibt eine Welle als kleine Blase, die sich nur bewegt, wenn du sprichst.

<a href="https://snipez5768.github.io/IslandHub/#mikro"><img src="docs/img/listening-v3.svg" width="100%" alt="Claude hört zu"></a>

### In den Einstellungen
Unter **Animationen** spielt jede Karte ihre Animation ab – mit „Auf der Insel ansehen“ auch auf der echten Insel.

<img src="docs/img/settings-anim.png" width="525" alt="Einstellungen › Animationen">

Geschwindigkeit, Dämpfung und mehr kannst du auf der **[Demo-Seite](https://snipez5768.github.io/IslandHub/)** selbst einstellen.

## Installieren

1. Die neueste **`IslandHub.exe`** unter [Releases](https://github.com/Snipez5768/IslandHub/releases/latest) laden.
2. Starten – die Insel erscheint oben in der Mitte. Braucht die [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
3. Neue Versionen meldet die Insel selbst und installiert sie mit einem Klick.

Selbst bauen: `cd IslandHub` und `dotnet run`.

## Datenschutz

Alles bleibt auf deinem PC (`%AppData%\IslandHub`). Ins Netz geht nur, was du selbst einrichtest – Wetter, Preisalarme, Spotify, Discord – und die Frage an GitHub, ob es eine neue Version gibt. Die Vorschauen in den Einstellungen und auf dieser Seite zeigen nur erfundene Beispiele.

---

<sub>IslandHub ist ein eigenes Projekt und nicht mit Apple verbunden. Die Symbole sind selbst gezeichnet.</sub>
