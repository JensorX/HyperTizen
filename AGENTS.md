# AGENTS.md – HyperTizen Arbeitsanleitung

## Projektstatus

HyperTizen ist eine experimentelle Hyperion/HyperHDR-Capture-App für Samsung TVs. Das Repository zielt auf Tizen 9 und enthält ein gemeinsames Hybrid-Paket:

- Web-UI: `io.gh.reisxd.HyperTizenUI`
- nativer .NET-Service: `io.gh.reisxd.HyperTizen`
- Package-ID: `io.gh.reisxd.HyperTizen`

Die UI ist launchbar und startet den Service per Application Control. Die alte TizenBrew-UI und `HyperTizenUI/js/service.js` gehören nicht mehr zum Produkt.

## Pflicht vor Änderungen

1. `README.md` lesen.
2. `.agents` lesen, wenn die Aufgabe Capture, Native-Interop oder Debugging betrifft.
3. `git status --short` prüfen und vorhandene Änderungen nicht überschreiben.
4. Tizen-9-API-Annahmen immer als hardwareabhängig behandeln.

## Architektur

```text
Launcher-Kachel
  └─ HyperTizenUI (Web-App, sichtbare UI)
       └─ Application Control: io.gh.reisxd.HyperTizen
            └─ HyperTizen (native .NET-Service)
                 ├─ ws://127.0.0.1:45677  Steuerung + /health
                 ├─ ws://127.0.0.1:45678  Logs
                 └─ Hyperion/HyperHDR TCP + FlatBuffers
```

Das Workspace-Manifest ist [`tizen_workspace.yaml`](tizen_workspace.yaml). Es muss beide Projekte enthalten und auf API 9 / ARM zeigen. Web- und native Manifest verwenden dieselbe Package-ID, aber unterschiedliche Application-IDs.

## Capture-Priorität

1. `libvideo-capture.so.0.1.0`
2. `libdisplay-capture-api.so.0.0`
3. T8-/T7-Kompatibilitätsadapter
4. `libvideoenhance.so` / Pixel-Sampling

Tizen-9-Native-Interop muss:

- `dlopen`/`dlsym` vor der Verwendung einer optionalen Library durchführen,
- typisierte Cdecl-Delegates statt vermuteter direkter `DllImport`-Pfade verwenden,
- Struct-Größen und Buffer-Größen prüfen,
- Besitz und Grenzen nativer Buffer-Pointer prüfen,
- `Lock`/`Unlock` in `try/finally` ausführen,
- `-4` als kontrollierten DRM-Fehler und `-95` als Fallback-Grund behandeln,
- Buffer pro Auflösung wiederverwenden.

`CaptureResult` muss bei erfolgreichen Frames Breite, Höhe, Y-/UV-Stride, Pixelformat, nativen Fehlercode und Capture-Methode liefern. `Networking` muss die tatsächlichen Strides bis zum NV12-FlatBuffer weitergeben.

Nach drei aufeinanderfolgenden nativen Capture-Fehlern wird die Methode quarantäniert. Danach muss der Selector eine neue Instanz des nächsten Fallbacks testen. Keine Methode darf bei fehlender Firmware-Unterstützung den Serviceprozess beenden.

## Service und UI

- `HyperTizen/HyperTizen_App.cs`: Präferenzen nur bei fehlendem Schlüssel initialisieren; Benutzereinstellungen nicht durch Build-Konstanten überschreiben.
- `HyperTizen/WebSocket/WebSocket.cs`: Port 45677, WebSocket-Kompatibilität und `GET /health` erhalten.
- `HyperTizen/LogWebSocketServer.cs`: Port 45678 erhalten.
- `HyperTizenUI/index.html`: Service starten, Health-Timeout behandeln, danach lokale WebSockets verbinden.
- `HyperTizenUI/js/wsClient.js`: keine Abhängigkeit von `127.0.0.1:8081` oder einem externen Launcher.
- Home/Back darf die UI beenden, aber nicht den Hintergrund-Service stoppen.

TizenTube dient nur als Referenz für Standalone-Lifecycle und Service-Start. Keine Cobalt-Metadaten, `nativeID`, Cobalt-User-Agent oder YouTube-spezifische Dateien übernehmen.

## Testregeln

Emulatoren sind keine Capture-Freigabe. Vor einer Hardwareaussage auf einem echten Tizen-9-TV prüfen:

- UI-Kachel startet und `/health` wird `ready=true`,
- Service-Control, `controls.html` und `logs.html` funktionieren parallel,
- nicht geschützte Quellen liefern NV12-Frames,
- DRM liefert kontrolliert `-4`,
- 30 Minuten Capture ohne Crash oder kontinuierliches Speicherwachstum,
- mindestens 10 FPS bei 480×270 als Zielwert dokumentieren,
- Service-Neustart und Verhalten nach Home/Back prüfen.

Für die Diagnose WebSocket-Logs unter `http://<TV-IP>:45678` verwenden. Modell, Firmware, Capture-Methode, FPS und Fehlercodes dokumentieren.

## Sichere Änderungen

- Für lokale Änderungen `apply_patch` verwenden.
- Keine destruktiven Git-Kommandos ohne ausdrückliche Anweisung.
- Native Aufrufe mit Fehlerbehandlung und Logging versehen.
- Optionalen Bibliotheken nicht blind laden; bekannte Grafik-/Wayland-Libraries nicht scannen oder laden.
- Nach jeder Änderung `git diff --check` ausführen.
- Vor Abschluss README, Installationsdokumentation und diesen Status synchron halten.

## Lokale Validierung

Wenn verfügbar:

```bash
git diff --check
dotnet build HyperTizen/HyperTizen.csproj -c Release
tizen package ...
```

Wenn Tizen Studio, .NET-Tizen-SDK oder TV fehlen, das ausdrücklich als nicht ausgeführten Test dokumentieren. Eine lokale Syntaxprüfung ersetzt keine Hardwareprüfung.
