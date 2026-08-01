# LINE Messaging API — Live-Verifikation einrichten

Anleitung für `LineLiveVerificationTests`. LINE ist der aussichtsreichste der vier asiatischen
Anbieter: als einziger **ohne Zeitfenster** — Push-Nachrichten gehen jederzeit an jeden Nutzer,
der den Bot als Freund hinzugefügt hat. Es gibt keine Vorbereitung wie bei WhatsApp, wo das
24-Stunden-Fenster unmittelbar vor dem Lauf geöffnet werden muss.

Kostet nichts: LINE hat einen Free Plan mit einem länderabhängigen Freikontingent.
Wie gross es für die Schweiz ist, misst `Step6` — das steht in keiner Doku, die sich abrufen liess.

## 1. LINE Official Account und Messaging-Channel anlegen

1. LINE-Konto auf dem Mobiltelefon vorausgesetzt (dasselbe, das später Empfänger ist).
2. <https://developers.line.biz/console/> öffnen und mit dem LINE-Konto anmelden.
3. Einen **Provider** anlegen (organisatorische Klammer, freier Name).
4. Darin einen Channel vom Typ **Messaging API** erstellen. Dabei entsteht automatisch ein
   LINE Official Account.

**Falle:** LINE kennt mehrere Channel-Typen. Ein **LINE Login**-Channel liefert ebenfalls ein
Token, das aber an keinem Messaging-Endpunkt funktioniert. `Step2` des Live-Tests fängt das ab,
indem es prüft, ob `/v2/bot/info` ein `basicId` liefert.

## 2. Zugangsdaten holen

Im Channel:

| Wo                     | Wert                        | Feld                       |
| ---------------------- | --------------------------- | -------------------------- |
| **Messaging API**-Reiter | **Channel access token**    | `line.channelAccessToken`  |
| **Basic settings**-Reiter | **Channel secret**          | `line.channelSecret`       |
| **Basic settings**-Reiter | **Your user ID**            | `line.userId`              |

Das Channel Access Token muss ggf. erst über **Issue** erzeugt werden. LINE bietet mehrere
Token-Arten an (u. a. kurzlebige und langlebige) — für den Test genügt die einfachste; läuft
sie ab, meldet `Step1` das sauber.

**Zur `userId`:** Sie beginnt mit `U` und ist 33 Zeichen lang. Dass sie unter *Basic settings*
als „Your user ID" steht, liess sich in der offiziellen Doku **nicht belegen** — es ist die in
der Praxis übliche Stelle. Falls sie dort fehlt, ist der andere Weg, dem Bot vom Handy aus zu
schreiben und die `userId` aus dem eingehenden Webhook-Event zu lesen; das setzt allerdings
einen öffentlich erreichbaren Endpunkt voraus.

## 3. Bot als Freund hinzufügen — zwingend

Im **Messaging API**-Reiter steht ein **QR-Code**. Diesen mit dem LINE-App-Scanner auf dem
Mobiltelefon scannen und den Official Account als Freund hinzufügen.

Ohne diesen Schritt lehnt LINE den Push ab. Das ist LINEs Gegenstück zur Empfänger-Freigabeliste
von WhatsApp — nur dauerhaft, nicht als Zeitfenster.

Sinnvoll ausserdem im selben Reiter: **Auto-reply messages** und **Greeting messages**
abschalten, sonst antwortet der Account automatisch auf jede Testnachricht.

## 4. Credentials eintragen

`live-credentials.local.example.json` nach `live-credentials.local.json` kopieren und den
`line`-Block ausfüllen. Die Datei ist über `*.local.json` in `Klacks.UnitTest/.gitignore`
ausgeschlossen.

## 5. Lauf starten

```powershell
powershell -File run-live-test.ps1 -Provider Line
```

## Was die sechs Schritte beweisen

| Test  | Beweist                                                                       |
| ----- | ----------------------------------------------------------------------------- |
| Step1 | Das Channel Access Token wird von LINE akzeptiert                              |
| Step2 | **Misst** das reale Freikontingent und den Verbrauch — vor jedem Versand       |
| Step3 | Das Token gehört zu einem Messaging-API-Channel, nicht zu einem Login-Channel  |
| Step4 | Fehlerpfad: LINEs Fehlertext kommt beim Aufrufer an (`ExtractErrorMessage`)    |
| Step5 | Push-Versand kommt tatsächlich auf dem Mobiltelefon an                         |
| Step6 | **Misst**, wo LINE eine zu lange Nachricht ablehnt                             |

**Ein Lauf verbraucht bis zu drei Nachrichten** aus dem monatlichen Freikontingent (Step4, 5, 6).
Deshalb steht die Kontingent-Abfrage an zweiter Stelle: Nach Step2 ist bekannt, wie viel noch da
ist, bevor überhaupt etwas gesendet wird.

Step4 sendet an `U00000000000000000000000000000000` — syntaktisch gültig, aber von keinem Konto
gehalten. Ein Push an jemanden, der den Bot nicht als Freund hat, scheitert ohnehin.

## Step2 und Step6 sind Messungen, keine Behauptungen

Kein Adapter prüft die Nachrichtenlänge — das ist ein bewusst offener Punkt aus der
Rate-Limit-Arbeit vom 2026-08-01, weil zwölf aus dem Gedächtnis rekonstruierte Limits schlimmer
wären als keine. `Step6` schickt deshalb 5001 Zeichen und **protokolliert**, was LINE sagt,
statt eine Zahl zu behaupten. Geprüft wird nur, dass der Adapter das Ergebnis wahrheitsgemäss
weiterreicht: bei Annahme eine Message-ID, bei Ablehnung eine Fehlermeldung. Eine stille
Ablehnung wäre der eigentliche Fehler.

Ebenso `Step2`: Die Antwortform von `/v2/bot/message/quota` liess sich nicht aus der Doku
abrufen, deshalb wird sie ausgegeben statt geprüft. Nach dem Lauf steht die echte Zahl im
Testprotokoll — und damit im DevKnowledge festhaltbar.

## Wenn Step5 rot wird

Nennt der Fehler den Empfänger, hat das Konto aus `line.userId` den Bot nicht als Freund
hinzugefügt (Schritt 3). Anders als bei WhatsApp gibt es **kein** Zeitfenster, das man
versehentlich verpasst haben könnte.

## Nicht abgedeckt

**Eingehende Nachrichten.** `ValidateWebhook` gegen eine echte `x-line-signature` zu prüfen,
braucht einen öffentlich erreichbaren HTTPS-Endpunkt, den LINE aufrufen kann. Den HMAC lokal
selbst zu berechnen, würde nur die eigene Annahme gegen sich selbst testen. Der Adapter
implementiert den Empfangspfad im Gegensatz zu WeChat, Zalo und KakaoTalk vollständig — er ist
nur nicht live geprüft. Slack und WhatsApp sind ebenfalls reine Ausgangs-Tests.
