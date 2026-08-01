# Microsoft Teams — Live-Verifikation einrichten

Anleitung für `TeamsLiveVerificationTests`.

Teams ist der **am schnellsten testbare** Provider: keine App-Registrierung, kein Bot-Dienst,
kein Azure-Eintrag. Es genügt ein Power-Automate-Workflow mit eingehendem Webhook. Wenn der
Anbieter in Klacks bereits konfiguriert ist, sind die Zugangsdaten schon vorhanden — dann ist
der Lauf eine Minute Arbeit.

## ⚠ Jeder Lauf postet zwei sichtbare Karten

Das ist die wichtigste Eigenheit. `ValidateConfigAsync` hat **keinen lesenden Pfad** — es
verschickt eine Karte mit dem Text `Klacks connection test`. Ein Verbindungstest ist bei Teams
also immer für alle im Kanal sichtbar.

Der Testlauf erzeugt daher zwei Karten: die Verbindungsprüfung und die eigentliche Testnachricht.
Der Fehlerpfad postet nichts (die URL ist dort absichtlich beschädigt).

**Empfehlung:** einen eigenen Testkanal anlegen, statt in einen Team-Kanal zu posten, den
Kollegen lesen.

## 1. Workflow mit Webhook anlegen

Falls noch keiner existiert — dasselbe Vorgehen wie im ausgelieferten Handbuch:

1. In Teams die App **Workflows** öffnen.
2. **Neuer Flow** → Vorlage **„Post to a channel when a webhook request is received"**.
3. Team und Kanal wählen, in den gepostet werden soll.
4. **Flow erstellen**, dann die erzeugte **HTTP-URL** kopieren
   (`https://prod-XX.westeurope.logic.azure.com/workflows/...`).

Die URL wird nur einmal angezeigt.

## 2. Die URL ist ein Passwort

Die Autorisierung steckt als Signatur **in der URL selbst**. Wer sie hat, kann in den Kanal
posten — es gibt keinen zweiten Faktor. Deshalb:

- nie in ein Ticket, einen Chat oder einen Commit kopieren;
- nur in `live-credentials.local.json`, die über `*.local.json` git-ignoriert ist;
- bei Verdacht auf Verlust den Flow löschen und neu anlegen, das entwertet die alte URL.

## 3. Credentials eintragen

`live-credentials.local.example.json` nach `live-credentials.local.json` kopieren und den
`teams`-Block ausfüllen — nur `webhookUrl`.

## 4. Lauf starten

```powershell
powershell -File run-live-test.ps1 -Provider Teams
```

## Was die drei Schritte beweisen — und was nicht

| Test  | Beweist                                                                  |
| ----- | ------------------------------------------------------------------------ |
| Step1 | Der Workflow nimmt die Verbindungsprüfung an (**sichtbare Karte**)        |
| Step2 | Der Workflow nimmt eine Adaptive Card an (**sichtbare Karte**)            |
| Step3 | Fehlerpfad: eine beschädigte URL wird als Fehler gemeldet, nicht verschluckt |

**Grenze, die dieser Test nicht überwinden kann:** Ein Power-Automate-Flow beantwortet den
HTTP-Aufruf, ohne eine Nachrichten-Kennung zurückzugeben — und `SendMessageResult` trägt für
Teams entsprechend keine. `Success = true` heisst deshalb **„der Flow hat die Anfrage
angenommen"**, nicht „die Karte steht im Kanal". Dazwischen liegt der ganze Flow: Er kann
deaktiviert sein, an einer späteren Aktion scheitern oder in einen umbenannten Kanal posten.

**Die einzige echte Bestätigung ist ein Blick in den Kanal.** Ein grüner Testlauf ersetzt das
nicht. Deshalb behauptet `Step2` das auch nicht, sondern schreibt den Hinweis ins Protokoll.

`Step2` prüft ausserdem ausdrücklich, dass **keine** Nachrichten-Kennung zurückkommt. Schlägt
diese Zusicherung eines Tages fehl, hat Microsoft das Verhalten geändert — dann sollte der
Adapter die Kennung übernehmen, damit Zustellung überhaupt nachweisbar wird.

## Nicht abgedeckt

**Eingehende Nachrichten und Direktnachrichten.** Der Workflow-Webhook ist ein reiner
Eingangskanal in Richtung Teams: Antworten aus Teams erreichen Klacks nicht, und der Empfänger
wird ignoriert — jede Nachricht geht in den einen Kanal, der im Flow hinterlegt ist. Für mehrere
Kanäle braucht es mehrere Flows und mehrere Teams-Anbieter in Klacks. All das steht so im
ausgelieferten Handbuch; die fehlenden Empfangs-Schritte sind kein Versäumnis.
