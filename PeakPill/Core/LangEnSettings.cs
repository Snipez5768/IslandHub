namespace PeakPill.Core;

/// <summary>
/// German → English for the reworked settings texts and the Minimal-Modus (2026-11-17).
/// Same rules as <see cref="LangEn"/>; <see cref="L.Init"/> reads both.
/// </summary>
public static class LangEnSettings
{
    public static readonly (string De, string En)[] Pairs =
    {
        // ---------- Minimal-Modus ----------
        ("Minimal-Modus", "Minimal mode"),
        ("Leere Tabs ausblenden", "Hide empty tabs"),
        ("Sobald dort etwas ist, sind sie wieder da.", "They come back as soon as there is something on them."),
        ("Solange der Verlauf leer ist.", "While the history is empty."),
        ("Solange kein API-Schlüssel hinterlegt ist.", "While no API key is set."),
        ("Solange keine Claude-Code-Sitzung läuft.", "While no Claude Code session is running."),
        ("Solange kein Gerät in der Nähe ist.", "While no device is nearby."),
        ("Solange kein Player offen ist.", "While no player is open."),
        ("Solange keine neuen da sind.", "While there are no new ones."),

        // ---------- shorter explanations ----------
        ("So geht's", "How to"),
        ("Beim Öffnen siehst du, was gerade passiert ist, z. B. nach einem neuen Song die Musik.", "Opening shows what just happened, e.g. the music after a new song."),
        ("Zeigt ihren Namen, bei Bluetooth mit Akkustand.", "Shows their name, with battery level for Bluetooth."),
        ("Eine Antwort aus der Insel wird eingefügt und gleich abgeschickt. Aus: Enter drückst du selbst.", "A reply from the island is pasted and sent right away. Off: you press Enter yourself."),
        ("Die Insel findet den Chat über die Suche der App. Bei gleichen Namen kann sie einen anderen Chat treffen – deshalb ist Aus sicherer.", "The island finds the chat via the app’s search. With equal names it can hit another chat – that’s why Off is safer."),
        ("Was die Insel macht, solange ein Spiel oder Video den ganzen Bildschirm füllt.", "What the island does while a game or video fills the whole screen."),
        ("Stumm schalten, wer im Anruf ist, Stream.", "Mute, who is in the call, stream."),
        ("Mit einer eigenen Discord-App schaltet dich die Insel direkt in Discord stumm.", "With your own Discord app the island mutes you right in Discord."),
        ("discord.com/developers → New Application → unter OAuth2 die Redirect-URL http://127.0.0.1 eintragen → Client-ID und Client-Secret unten einfügen → Verbinden.", "discord.com/developers → New Application → under OAuth2 add the redirect URL http://127.0.0.1 → paste client ID and client secret below → Connect."),
        ("Nur als Ersatz nötig, falls die Insel den Stream nicht selbst starten kann.", "Only needed as a fallback if the island can’t start the stream itself."),
        ("In Discord unter Einstellungen → Tastenkombinationen eine Aktion „Bildschirmübertragung umschalten“ anlegen und hier dieselbe Kombination eintragen, z. B. Strg + Shift + Alt + F9.", "In Discord under Settings → Keybinds add “Toggle Go Live” and enter the same combination here, e.g. Ctrl + Shift + Alt + F9."),
        ("Zeigt im Musik-Tab, was als Nächstes läuft.", "Shows on the Music page what plays next."),
        ("Braucht eine eigene, kostenlose Spotify-App.", "Needs your own free Spotify app."),
        ("developer.spotify.com → Dashboard → Create app → als Redirect URI http://127.0.0.1:47824/callback eintragen, „Web API“ anhaken → Client-ID unten einfügen → Verbinden. Ein Secret brauchst du nicht.", "developer.spotify.com → Dashboard → Create app → add http://127.0.0.1:47824/callback as redirect URI, tick “Web API” → paste the client ID below → Connect. You don’t need a secret."),
        ("Anpfiff, Tore und Abpfiff deines Vereins. Liga: bl1, bl2, bl3 oder dfb.", "Kick-off, goals and full time of your club. League: bl1, bl2, bl3 or dfb."),
        ("Meldet, wenn ein Kanal live geht. Kanäle mit Komma trennen.", "Tells you when a channel goes live. Separate channels with commas."),
        ("Auf dev.twitch.tv eine kostenlose App anlegen und Client-ID und Secret hier eintragen. Das Secret wird verschlüsselt gespeichert.", "Create a free app on dev.twitch.tv and enter its client ID and secret here. The secret is stored encrypted."),
        ("@Name oder Kanal-ID, mit Komma getrennt. Ohne Anmeldung, alle 5 Minuten geprüft.", "@name or channel ID, separated by commas. No sign-in, checked every 5 minutes."),
        ("Meldet jede neue Station. Ein Paket pro Zeile: „Name: Sendungsnummer“.", "Reports every new stage. One parcel per line: “Name: tracking number”."),
        ("Auf developer.dhl.com einen kostenlosen API-Schlüssel holen und unten einfügen. Er wird verschlüsselt gespeichert.", "Get a free API key on developer.dhl.com and paste it below. It is stored encrypted."),
        ("Will eine Website einen Code, öffnet die Insel dein Postfach.", "When a website asks for a code, the island opens your inbox."),
        ("Schauen dem Mauszeiger nach und blinzeln ab und zu.", "They follow the mouse pointer and blink now and then."),
        ("Auf großen Bildschirmen wächst die Insel mit, bei 2560 × 1440 um ein Drittel.", "On big screens the island grows along, by a third on 2560 × 1440."),
        ("Feld anklicken und Kombination drücken. Entf entfernt, Esc bricht ab.", "Click a field and press the combination. Del removes, Esc cancels."),
        ("Strg+Alt ist auf deutschen Tastaturen AltGr. Kürzel mit Q, E, M, 2, 3 oder 7 bis 0 blockieren dort Zeichen wie @, € oder { }.", "On German keyboards Ctrl+Alt is AltGr. Shortcuts with Q, E, M, 2, 3 or 7 to 0 block characters like @, € or { } there."),
        ("Aus: der Verlauf beginnt nach jedem PC-Neustart leer.", "Off: the history starts empty after every PC restart."),
        ("Der Schlüssel liegt verschlüsselt auf diesem PC. Fragen gehen direkt an den Dienst und kosten dort Guthaben. Unter Tastatur gibt es ein Kürzel für den KI-Chat.", "The key is stored encrypted on this PC. Questions go straight to the service and use credit there. Keyboard has a shortcut for the AI chat."),
        ("Unter diesem Namen sehen dich Handy und PCs in der Nähe.", "Phones and PCs nearby see you under this name."),

        // ---------- quick notes ----------
        ("Notizen", "Notes"),
        ("Schnellnotizen", "Quick notes"),
        ("Schnellnotiz schreiben", "Write a quick note"),
        ("Notiz schreiben … (Enter speichert)", "Write a note … (Enter saves)"),
        ("Notiz bearbeiten … (Enter speichert, Esc bricht ab)", "Edit note … (Enter saves, Esc cancels)"),
        ("Speichern (Enter)", "Save (Enter)"),
        ("Noch keine Notizen", "No notes yet"),
        ("Schreib oben los – Enter speichert", "Start typing above – Enter saves"),
        ("Schreib oben los – überall mit {0}", "Start typing above – anywhere with {0}"),
        ("Klicken zum Bearbeiten", "Click to edit"),
        ("Anheften", "Pin"),
        ("Lösen", "Unpin"),
        ("Mit LocalSend senden", "Send with LocalSend"),
        ("gestern", "yesterday"),
        ("Schreibt eine Notiz", "Writes a note"),
        ("Liest deine Notizen", "Reads your notes"),

        // ---------- downloads (the status after the MB so far) ----------
        ("geladen", "so far"),
        ("hängt", "stuck"),

        // ---------- Claude Code hooks ----------
        ("Claude Code (Agenten)", "Claude Code (agents)"),
        ("Zeigt im Agent-Tab, woran Claude gerade arbeitet.", "Shows on the Agent page what Claude is working on."),
        ("Claude Code meldet der Insel jede Aufgabe, den aktuellen Schritt, den Fortschritt der Aufgabenliste und ob es auf dich wartet. Gilt im Terminal, in VS Code und in der Claude-App.",
         "Claude Code tells the island each task, the current step, the progress of its task list and whether it waits for you. Works in the terminal, in VS Code and in the Claude app."),
        ("„Einrichten“ trägt fünf Hooks in deine Claude-Code-Einstellungen ein (%USERPROFILE%\\.claude\\settings.json, vorher wird eine Kopie angelegt). Sie schicken nur an diesen PC (127.0.0.1) und ändern nichts an Claudes Arbeit. Erkannt werden Sitzungen, die nach dem Einrichten starten – laufende einmal neu starten. Von Hand: den Block mit „Kopieren“ in die Datei einfügen.",
         "“Set up” adds five hooks to your Claude Code settings (%USERPROFILE%\\.claude\\settings.json, a copy is kept first). They only send to this PC (127.0.0.1) and change nothing about Claude’s work. Sessions started after setting up are recognized – restart running ones once. By hand: paste the block from “Copy” into the file."),
        ("Testen", "Test"),
        ("Entfernen", "Remove"),
        ("Den Hook-Block für die Datei kopieren", "Copy the hook block for the file"),
        ("Zeigt in der Insel kurz eine Beispiel-Aufgabe", "Briefly shows an example task in the island"),
        ("Eingerichtet · {0} aktiv", "Set up · {0} active"),
        ("Eingerichtet", "Set up"),
        ("Teilweise eingerichtet", "Partly set up"),
        ("Claude Code nicht gefunden", "Claude Code not found"),
        ("Einstellungen nicht lesbar", "Settings unreadable"),
        ("Neue Claude-Sitzungen erscheinen im Agent-Tab.", "New Claude sessions show up on the Agent page."),
        ("Erst Claude Code installieren und einmal starten.", "Install Claude Code and start it once first."),
        ("Fertig – laufende Claude-Sitzungen einmal neu starten.", "Done – restart running Claude sessions once."),
        ("Entfernt – Claude meldet der Insel nichts mehr.", "Removed – Claude no longer tells the island anything."),
        ("Kopiert – in settings.json unter die oberste Klammer einfügen.", "Copied – paste it inside the outermost braces of settings.json."),
        ("Die Claude-Code-Einstellungen lassen sich nicht lesen – bitte von Hand prüfen.", "Claude Code’s settings can’t be read – check them by hand."),
        ("Beispiel: Wetter-Widget bauen", "Example: build a weather widget"),
        ("Liest WeatherService.cs", "Reads WeatherService.cs"),
        ("Bearbeitet WeatherWidget.xaml", "Edits WeatherWidget.xaml"),

        // ---------- settings search ----------
        ("Suchen", "Search"),
        ("Suche leeren", "Clear search"),
        ("Nichts gefunden", "Nothing found"),
    };
}
