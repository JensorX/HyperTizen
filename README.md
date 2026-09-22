# HyperTizen – Tizen-9-Hybrid-App

HyperTizen ist ein experimenteller Hyperion/HyperHDR-Capturer für Samsung-TVs. Das Projekt wird als ein gemeinsames Hybrid-Paket gebaut:

- sichtbare Web-App `io.gh.reisxd.HyperTizenUI` mit Launcher-Kachel,
- nativer .NET-Service `io.gh.reisxd.HyperTizen` für Capture, Hyperion-Netzwerk und Logs,
- gemeinsame Package-ID `io.gh.reisxd.HyperTizen`.

Die UI startet den Service beim Öffnen per Tizen Application Control und wartet auf `GET http://127.0.0.1:45677/health`. Dadurch ist keine TizenBrew-UI und kein separater Launcher erforderlich. Tizen-Hybrid-Pakete bündeln die UI und Service-Anwendung in einer installierbaren `.wgt`; die Service-Anwendung bleibt unsichtbar, während die Web-App das Launcher-Icon erhält. Siehe die [offizielle Samsung-Anleitung für UI- und Service-Anwendungen](https://developer.samsung.com/tizen/blog/en/2019/01/04/how-to-package-ui-and-service-applications-together-and-perform-them).

## Aktueller Status

Die Paket-/Lifecycle-Umstellung und die Tizen-9-Interop-Schicht sind implementiert. Die Freigabe der Capture-Funktion ist weiterhin hardwareabhängig und muss auf einem echten Tizen-9-TV erfolgen; Emulatorergebnisse gelten nicht als Abnahme.

Capture-Priorität:

1. `libvideo-capture.so.0.1.0` / kompatible Sonames
2. `libdisplay-capture-api.so.0.0` / kompatible Sonames
3. T8-/T7-Kompatibilitätsadapter
4. `libvideoenhance.so` Pixel-Sampling als Fallback

Die Tizen-9-Adapter verwenden `dlopen`/`dlsym`, typisierte Cdecl-Funktionspointer, wiederverwendete Capture-Buffer und validierte Buffer-Pointer. Native Fehler `-4` (DRM) werden kontrolliert gemeldet; `-95` (nicht unterstützt) führt zum Fallback. Nach drei aufeinanderfolgenden nativen Fehlern wird die aktuelle Methode für die Service-Laufzeit deaktiviert und die nächste Methode getestet.

## Installation

1. Tizen Studio installieren, Developer Mode am TV aktivieren und ein gültiges TV-Zertifikatsprofil konfigurieren.
2. Das signierte gemeinsame `.wgt` bauen oder aus den Releases laden.
3. Das WGT installieren:

   ```bash
   tizen install -n path/to/io.gh.reisxd.HyperTizen.wgt
   ```

4. Die Kachel **HyperTizen** aus dem TV-Launcher starten. Die Web-UI startet den nativen Service automatisch.

Es gibt keine separate TizenBrew-UI-Installation. Boot-/Restart-Verhalten ist auf einem TV-/Zertifikatsprofil best effort; der explizite UI-Start bleibt der zuverlässige Fallback. Native Services benötigen für dauerhaftes Hintergrundverhalten je nach TV und Zertifikat passende Plattformfreigaben.

Das Workspace-Setup steht in [`tizen_workspace.yaml`](tizen_workspace.yaml). Es enthält `HyperTizenUI` und `HyperTizen` und verwendet Tizen API 9 / ARM.

## Bedienung und Endpunkte

Die lokale UI verbindet sich mit:

- `ws://127.0.0.1:45677` – Steuerung
- `ws://127.0.0.1:45678` – Live-Logs
- `http://127.0.0.1:45677/health` – Service-Status für den UI-Lifecycle

Externe Steuerung bleibt erhalten:

- `http://<TV-IP>:45677` für `controls.html` bzw. den Control-WebSocket
- `http://<TV-IP>:45678` für `logs.html` bzw. den Log-WebSocket

`/health` liefert `ready`, `serviceState`, `captureMethod`, `tizenVersion`, `lastError` sowie die Ports. Die bestehenden WebSocket-Event-IDs und Steuerbefehle bleiben kompatibel.

## Einstellungen

Bei der Erstinstallation wird `enabled=true` nur gesetzt, wenn noch keine Präferenz existiert. Danach bleiben Stop-/Start-Entscheidungen des Benutzers erhalten. `diagnosticMode` wird ebenfalls nur initial angelegt und beim Start nicht mehr durch eine Build-Konstante überschrieben.

## Tizen-9-Capture und Performance

Die Zielausgabe ist standardmäßig 480×270 NV12. Strides werden aus dem tatsächlichen Capture-Ergebnis übernommen und bis zum FlatBuffer weitergereicht; es gibt keine feste Stride-Annahme mehr. Die Tizen-9-Adapter verwenden native und Managed-Plane-Buffer pro Auflösung wieder; die älteren Fallbacks behalten ihre bisherige Speicherstrategie.

Zu validieren auf echter Hardware:

- mindestens 10 FPS bei 480×270,
- 30 Minuten Capture ohne Crash oder kontinuierliches Speicherwachstum,
- gültige Frames bei nicht geschützten Inhalten,
- kontrollierter Fehler `-4` bei DRM-Inhalten,
- korrekte Wiederaufnahme nach Service-Abbruch und TV-Standby.

## Build und Signierung

Für einen lokalen Build werden Tizen Studio, das Tizen-.NET-9-SDK und ein aktives Signing-Profil benötigt. Typischer Ablauf:

```bash
tizen build-web -- /path/to/HyperTizenUI
dotnet build HyperTizen/HyperTizen.csproj -c Release
# anschließend das Hybrid-Workspace-WGT mit dem aktiven Tizen-Profil paketieren
```

Die exakten Build-Schritte hängen von der installierten Tizen-Studio-Version und dem Signing-Profil ab. In dieser Entwicklungsumgebung sind `tizen`, `dotnet` und ein echter TV nicht verfügbar; deshalb kann hier kein signiertes WGT und keine Hardwarefreigabe erzeugt werden.

### GitHub Actions

Der Workflow [`build-hypertizen-wgt.yml`](.github/workflows/build-hypertizen-wgt.yml) baut bei `main`, bei `v*.*.*`-Tags oder manuell ein gemeinsames Hybrid-WGT. Er kompiliert den .NET-Service, legt den nativen Teil unter `bin`/`info` und die UI unter `res/wgt` ab und signiert anschließend das Ergebnis mit `tizen.js`.

Dafür müssen im Repository die Actions-Secrets `TIZEN_AUTHOR_KEY` (Base64-kodierte `.p12`-Datei) und `TIZEN_AUTHOR_KEY_PW` (Passwort) hinterlegt sein. Das WGT wird als Actions-Artefakt veröffentlicht; bei einem Versionstag wird zusätzlich ein GitHub Release angelegt. Distributor-Zertifikate werden wie im TizenTube-Workflow über `--privilege public` von `tizen.js` bezogen.

## Debugging

1. Service über die HyperTizen-Kachel starten.
2. `http://<TV-IP>:45678` im Browser öffnen.
3. Auf Capture-Auswahl, Tizen-Version, native Rückgabecodes und FPS achten.
4. Für die lokale UI zusätzlich `http://<TV-IP>:45677/health` prüfen.

DRM-geschützte Quellen wie Netflix oder HDCP-Inhalte sind absichtlich nicht capturebar. Ein `-4` ist in diesem Fall ein erwarteter Schutzfehler und kein Anlass, den Schutz zu umgehen.

## Struktur

- [`HyperTizen/HyperTizen_App.cs`](HyperTizen/HyperTizen_App.cs) – Service-Lifecycle und Application-Control-Antwort
- [`HyperTizen/HyperionClient.cs`](HyperTizen/HyperionClient.cs) – Capture-Loop, Fallback und Hyperion-Verbindung
- [`HyperTizen/Capture/`](HyperTizen/Capture/) – Capture-Methoden und Native-Interop
- [`HyperTizen/Networking.cs`](HyperTizen/Networking.cs) – Hyperion-FlatBuffer-Versand mit echten Strides
- [`HyperTizen/WebSocket/WebSocket.cs`](HyperTizen/WebSocket/WebSocket.cs) – Port 45677 und `/health`
- [`HyperTizenUI/`](HyperTizenUI/) – sichtbare Web-App
- [`tizen_workspace.yaml`](tizen_workspace.yaml) – gemeinsames Hybrid-Workspace

TizenTube wurde nur als Lifecycle-/Standalone-Vorbild untersucht. Cobalt-Metadaten, Cobalt-User-Agent, `nativeID` und YouTube-Content werden nicht verwendet.

## Lizenz

Wie beim ursprünglichen HyperTizen-Projekt. Die Software ist experimentell und nicht mit Samsung oder dem offiziellen Tizen-Projekt verbunden.
