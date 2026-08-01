# Signal — Live-Verifikation einrichten

Anleitung für `SignalLiveVerificationTests`.

**Signal ist der aufwendigste der bisherigen Live-Tests** — nicht wegen des Codes, sondern weil
es keinen Cloud-Dienst gibt. Slack, WhatsApp, LINE und Teams sprechen mit einem fremden Server;
Signal braucht einen **eigenen laufenden Container**. Das ist der ganze Aufwand.

Dafür hat Signal einen Vorteil, den sonst nur SMS und WhatsApp haben: Der Empfänger ist eine
**Telefonnummer**. Es gibt kein Adressproblem wie bei LINE oder Threema, wo man erst eine
plattformspezifische ID beschaffen muss.

## 1. signal-cli-rest-api starten

Der Adapter spricht mit `bbernhard/signal-cli-rest-api`. Als Container, z. B.:

```bash
docker run -d --name signal-api \
  -p 8080:8080 \
  -v signal-cli-data:/home/.local/share/signal-cli \
  -e MODE=native \
  bbernhard/signal-cli-rest-api
```

Erreichbarkeit prüfen:

```bash
curl http://localhost:8080/v1/accounts
```

Solange keine Nummer registriert ist, liefert das eine leere Liste `[]` — das ist der erwartete
Zustand vor Schritt 2. `Step1` des Tests prüft genau diesen Aufruf.

## 2. Absendernummer registrieren oder verknüpfen

Zwei Wege, beide über die Container-API:

**Verknüpfen (empfohlen)** — die Nummer bleibt auf dem Mobiltelefon nutzbar, der Container wird
ein zusätzliches Gerät:

```bash
# liefert einen QR-Code, den man in Signal unter
# Einstellungen -> Verknuepfte Geraete scannt
curl "http://localhost:8080/v1/qrcodelink?device_name=klacks"
```

**Registrieren** — eine eigene Nummer nur für den Container. Braucht SMS-Empfang auf dieser
Nummer und eine Captcha-Lösung; deutlich mühsamer.

Danach muss die Nummer in der Kontoliste auftauchen:

```bash
curl http://localhost:8080/v1/accounts
# ["+41791234567"]
```

`ValidateConfigAsync` prüft genau das — die Nummer aus der Konfiguration muss in dieser Liste
stehen. Das ist eine echte Prüfung, kein blosser Erreichbarkeitstest.

## 3. Sicherheitshinweis zur API-URL

`ApiUrl` ist eine **einfache HTTP-Adresse ohne jede Authentifizierung**. Der Adapter schickt
keinen Token, weil die API keinen kennt. Wer den Port erreicht, kann in deinem Namen Signal-
Nachrichten senden und mitlesen.

Der Container darf deshalb **niemals über den Host hinaus erreichbar sein**. Nur an `127.0.0.1`
binden oder in einem internen Docker-Netz halten, nie ins Internet exponieren.

## 4. Credentials eintragen

`live-credentials.local.example.json` nach `live-credentials.local.json` kopieren und den
`signal`-Block ausfüllen:

| Feld        | Bedeutung                                            |
| ----------- | ---------------------------------------------------- |
| `apiUrl`    | Basis-URL des Containers, z. B. `http://localhost:8080` |
| `number`    | Registrierte Absendernummer in E.164, z. B. `+41791234567` |
| `recipient` | Empfängernummer für den Testversand                  |

Die Datei ist über `*.local.json` in `Klacks.UnitTest/.gitignore` ausgeschlossen.

## 5. Lauf starten

```powershell
powershell -File run-live-test.ps1 -Provider Signal
```

## Was die vier Schritte beweisen

| Test  | Beweist                                                                    |
| ----- | -------------------------------------------------------------------------- |
| Step1 | Der Container läuft und antwortet auf `/v1/accounts`                        |
| Step2 | Die Absendernummer ist im Container registriert                             |
| Step3 | Fehlerpfad: signal-cli-Fehlertext kommt beim Aufrufer an                    |
| Step4 | Der Versand erreicht das Empfängergerät                                     |

**Warum Step1 und Step2 getrennt sind:** `ValidateConfigAsync` liefert `false` sowohl wenn der
Container nicht erreichbar ist als auch wenn die Nummer fehlt. Ohne die Trennung wäre ein rotes
Step2 zweideutig. So bedeutet es genau eines: Container läuft, Nummer fehlt.

Step3 sendet an `+12025550199` — eine von der NANPA für fiktive Verwendung reservierte Nummer,
die niemandem gehört und sicher kein Signal-Konto ist.

## Nicht abgedeckt

**Eingehende Nachrichten.** Signal ist im Adapter bewusst reiner Ausgangskanal: `ValidateWebhook`
liefert konstant `false`, `ParseWebhookPayload` konstant `null`, und das ausgelieferte Handbuch
sagt das auch. Die fehlenden Empfangs-Schritte sind kein Versäumnis. signal-cli-rest-api böte
zwar Empfang per Polling oder WebSocket an — das wäre eine Produkterweiterung, kein Test.
