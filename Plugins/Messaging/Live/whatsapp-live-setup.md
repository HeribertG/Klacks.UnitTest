# WhatsApp Cloud API — Live-Verifikation einrichten

Anleitung für `WhatsAppLiveVerificationTests`. Analog zu `slack-app-manifest.yaml`,
nur dass Meta kein Manifest kennt — das Setup läuft komplett über die Weboberfläche.

Kostet nichts: Meta stellt beim Anlegen der App automatisch eine Test-Telefonnummer.

## 1. Meta-App anlegen

1. <https://developers.facebook.com/apps> → **Create app**
2. Use case: **Other** → App type: **Business**
3. Im Dashboard bei **WhatsApp** auf **Set up**

Dabei wird automatisch eine Test-Business-Nummer erzeugt und registriert
(belegt: Cloud API „Phone Numbers"-Doku).

## 2. Werte aus dem API-Setup-Panel holen

Linke Navigation → **WhatsApp** → **API Setup**. Dort stehen:

| Wert                         | Wofür                              |
| ---------------------------- | ---------------------------------- |
| **Phone number ID**          | `whatsapp.phoneNumberId`           |
| **WhatsApp Business Account ID** | wird hier **nicht** gebraucht  |
| **Temporary access token**   | `whatsapp.accessToken`             |

**Falle:** Die beiden IDs stehen direkt untereinander und sehen gleich aus.
`ValidateConfigAsync` fragt nur `?fields=id` ab — darauf antwortet die
Business-Account-Node genauso. Mit der falschen ID sieht die Konfiguration also
gültig aus und **jeder** Versand schlägt fehl. `Step2` des Live-Tests prüft genau das.

**Falle:** Das temporäre Token läuft ab. Meta nennt in der Get-Started-Doku keine
konkrete Dauer, sagt aber ausdrücklich, es „expires quickly and is not suitable for
development purposes". Nach Ablauf im API-Setup-Panel neu erzeugen.

## 3. Empfängernummer freischalten

Im selben Panel unter **To** → **Manage phone number list** die eigene
WhatsApp-Nummer hinzufügen. Meta schickt einen Bestätigungscode auf diese Nummer,
der eingegeben werden muss. Ohne diesen Schritt lehnt die Test-Nummer jeden
Versand ab.

Format für `whatsapp.recipient`: Ländervorwahl ohne führende Null und ohne `+`,
z. B. Schweizer `079 123 45 67` → `41791234567`.

Eine harte Obergrenze für die Anzahl freigeschalteter Empfänger liess sich in
der offiziellen Doku **nicht** belegen — kursierende Zahlen sind hier bewusst
nicht übernommen.

## 4. Credentials eintragen

`live-credentials.local.example.json` nach `live-credentials.local.json` kopieren
und den `whatsapp`-Block ausfüllen. Die Datei ist über `*.local.json` in
`Klacks.UnitTest/.gitignore` ausgeschlossen — geprüft mit `git check-ignore`.

## 5. Das 24-Stunden-Fenster öffnen — direkt vor dem Lauf

**Das ist der kritische Schritt.** WhatsApp erlaubt Freitext (`type: text`) nur,
solange ein Kundendienst-Fenster offen ist. Das Fenster öffnet der *Nutzer*,
indem er die Business-Nummer anschreibt; es läuft 24 Stunden und wird bei jeder
weiteren Nachricht des Nutzers zurückgesetzt. Danach sind ausschliesslich
vorab freigegebene Templates zulässig (belegt: Cloud API „Send Messages"-Doku).

Also: **vom Empfänger-Handy aus eine beliebige Nachricht an die Test-Nummer
schicken, unmittelbar bevor der Test läuft.**

Ohne diesen Schritt misst `Step4` das geschlossene Fenster, nicht den Code.

## 6. Lauf starten

```powershell
powershell -File run-live-test.ps1 -Provider WhatsApp
```

## Was die vier Schritte beweisen

| Test  | Beweist                                                                  |
| ----- | ------------------------------------------------------------------------ |
| Step1 | Token und Phone-Number-ID werden von Meta akzeptiert                      |
| Step2 | Die ID ist wirklich eine Phone-Number-Node, keine Business-Account-Node   |
| Step3 | Fehlerpfad: Meta-Fehlertext kommt beim Aufrufer an (`ExtractErrorMessage`) |
| Step4 | Freitext-Versand kommt tatsächlich an                                     |

Step3 sendet an `12025550199` — eine von der NANPA für fiktive Verwendung
reservierte Nummer, die niemandem gehört. Damit ist der Fehlerpfad auch dann
gefahrlos, wenn der Test versehentlich gegen eine produktive Absendernummer läuft.

## Wenn Step3 rot wird

Zwei Deutungen, die der Test bewusst nicht vorwegnimmt:

- Meta liefert einen Fehler, aber `ExtractErrorMessage` findet ihn nicht → unser Parsing
  passt nicht zur echten Payload-Form.
- Meta antwortet mit **200 und einer `wamid`** → dann meldet der Provider Erfolg für eine
  Nachricht, die nie ankommt. Die Unzustellbarkeit käme erst später per Status-Webhook.
  `SendMessageResult.Success` hiesse damit „angenommen", nicht „zugestellt" — der
  gravierendere der beiden Befunde. Die geloggte Id im Testausgabe-Log unterscheidet
  die Fälle.

## Wenn Step4 rot wird

Meldet der Fehler ein Wiederaufnahme- oder 24-Stunden-Limit, dann ist die
Anbindung **technisch in Ordnung, fachlich aber unvollständig**:
`WhatsAppMessagingProvider.SendAsync` sendet ausschliesslich `type: text` und kann
damit keine Konversation eröffnen. Für den Klacks-Anwendungsfall — das Unternehmen
schreibt Mitarbeitende an — ist das der Normalfall, nicht die Ausnahme.

Der Vollausbau wäre `type: template` mit im Meta-Business-Manager freigegebenen
Vorlagen. Das ist eine Produktänderung, kein Testproblem, und deshalb hier bewusst
nicht enthalten.

## Nicht abgedeckt

**Eingehende Nachrichten.** `ValidateWebhook` gegen eine echte Meta-Signatur zu
prüfen, braucht einen öffentlich erreichbaren HTTPS-Endpunkt, den Meta aufrufen
kann. Die Signatur lokal selbst zu berechnen würde nur die eigene Annahme gegen
sich selbst testen — genau der Erkenntnisgewinn, der fehlt. Der Slack-Lauf vom
2026-08-01 war ebenfalls reiner Ausgangs-Test.
