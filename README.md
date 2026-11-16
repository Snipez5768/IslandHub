# PeakPill

**Eine Dynamic Island für Windows 10 und 11.** Eine kleine schwarze Insel oben in der Mitte des Bildschirms zeigt, was gerade passiert – Musik, Nachrichten, Timer, Downloads, wer dein Mikrofon nutzt – und öffnet sich zu kleinen Seiten, wenn du mit der Maus darauf zeigst.

<p align="center"><a href="https://snipez5768.github.io/PeakPill/#feder"><img src="docs/img/island-open-v2.svg" alt="Die Insel öffnet sich zur Musik-Seite" width="100%"></a></p>

## Verstecken und zurückholen

<img src="docs/img/hide-v1.svg" width="100%" alt="Pfeil versteckt die Insel, oben in der Mitte holt der Streifen sie zurück">

## Platz machen

### Adressleiste

<img src="docs/img/browser-v1.svg" width="100%" alt="Klick in die Adressleiste: die Insel fährt hoch, am oberen Rand kommt sie runter">

### YouTube im Vollbild

<img src="docs/img/video-v2.svg" width="100%" alt="Video im Vollbild: die Insel fährt hoch, am oberen Rand kommt sie runter">

## Animationen

### Nachricht

<a href="https://snipez5768.github.io/PeakPill/#nachricht"><img src="docs/img/notification-v2.svg" width="100%" alt="Nachricht"></a>

### Überspringen

<a href="https://snipez5768.github.io/PeakPill/#skip"><img src="docs/img/song-skip-v2.svg" width="100%" alt="Überspringen"></a>

### Neuer Song

<a href="https://snipez5768.github.io/PeakPill/#song"><img src="docs/img/song-change-v3.svg" width="100%" alt="Neuer Song"></a>

### Timer | Wecker

<a href="https://snipez5768.github.io/PeakPill/#wecker"><img src="docs/img/timer-wecker-v2.svg" width="100%" alt="Timer und Wecker"></a>

### … hört zu

<a href="https://snipez5768.github.io/PeakPill/#mikro"><img src="docs/img/listening-v3.svg" width="100%" alt="hört zu"></a>

## Einstellungen

<img src="docs/img/settings-tour-v1.svg" width="100%" alt="Durch die Einstellungen klicken">

## Installieren

1. **`PeakPill-Setup.exe`** unter [Releases](https://github.com/Snipez5768/PeakPill/releases/latest) laden und starten.
2. **Installieren** klicken – ohne Administrator-Rechte, nur für dich. Fehlt die .NET 8 Desktop Runtime, holt der Installer sie von Microsoft (Windows fragt einmal).
3. Die Insel erscheint oben in der Mitte. Neue Versionen meldet sie selbst und installiert sie mit einem Klick.

Entfernen: *Einstellungen › Apps › Installierte Apps › PeakPill*. Ohne Installer geht auch die einzelne **`PeakPill.exe`** (braucht dann die [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)).

PeakPill hieß bis Version 1.2.0 **IslandHub** – Einstellungen und Autostart werden beim ersten Start übernommen.

Selbst bauen: `cd PeakPill` und `dotnet run` – App und Installer zusammen mit `.\build-installer.ps1`.

## Datenschutz

Alles bleibt auf deinem PC (`%AppData%\PeakPill`). Ins Netz geht nur, was du selbst einrichtest – Wetter, Preisalarme, Spotify, Discord – und die Frage an GitHub, ob es eine neue Version gibt. Die Vorschauen in den Einstellungen und auf dieser Seite zeigen nur erfundene Beispiele.

---

<sub>PeakPill ist ein eigenes Projekt und nicht mit Apple verbunden. Die Symbole sind selbst gezeichnet.</sub>
