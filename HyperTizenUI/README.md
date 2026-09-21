# HyperTizen Control Center

Die UI ist die sichtbare Tizen-9-Web-App des gemeinsamen HyperTizen-Hybrid-Pakets. Sie besitzt die Launcher-Kachel `io.gh.reisxd.HyperTizenUI` und startet beim Öffnen den nativen Service `io.gh.reisxd.HyperTizen` über Application Control.

## Paket und Installation

UI und Service werden gemeinsam als signiertes `.wgt` installiert und aktualisiert. Die UI wird nicht mehr über TizenBrew installiert und benötigt keinen separaten `service.js`-Launcher.

Konfiguration: [`config.xml`](config.xml)

- Package-ID: `io.gh.reisxd.HyperTizen`
- Web-App-ID: `io.gh.reisxd.HyperTizenUI`
- Ziel: Tizen 9, TV-Samsung-Profil
- Launcher-Icon: [`icon.png`](icon.png)

## Lifecycle

Beim Start wartet die UI zunächst auf:

```text
GET http://127.0.0.1:45677/health
```

Erst bei `ready=true` werden die beiden WebSockets geöffnet. Home/Back beendet damit nur die sichtbare UI; der native Service bleibt davon unabhängig im Hintergrund aktiv. Boot-/Restart-Start ist abhängig von TV-Modell und Zertifikat; der UI-Start ist der sichere Fallback.

## Bedienung

- Start/Stop/Pause/Resume des Captures
- Neustart des Service
- SSDP-Suche nach Hyperion/HyperHDR
- Live-Status, FPS, Frames und Fehler
- Live-Logs für die Fehlersuche

Die UI ist für die TV-Fernbedienung optimiert. `Back` beendet die UI.

## Kommunikation

- `ws://127.0.0.1:45677` – Steuerbefehle und Status
- `ws://127.0.0.1:45678` – Log-Streaming
- `GET http://127.0.0.1:45677/health` – Lifecycle-Handshake

Externe Tools können dieselben Ports über die TV-IP verwenden:

```text
ws://<TV-IP>:45677
ws://<TV-IP>:45678
http://<TV-IP>:45677/health
```

`controls.html` und `logs.html` bleiben kompatibel. Die Event-IDs und Steuerbefehle wurden nicht geändert.

## Entwicklung

```text
HyperTizenUI/
├── index.html       # UI und Service-Start per Application Control
├── main.css         # TV-Layout und Statusanimationen
├── config.xml       # Tizen-9-Web-App und gemeinsame Package-ID
├── package.json     # UI-Metadaten
├── icon.png         # Launcher-Icon
└── js/
    └── wsClient.js  # Control-/Log-WebSockets
```

Die TizenTube-Standalone-App war nur Referenz für den Lifecycle. Cobalt-spezifische Metadaten und Einstellungen gehören nicht in diese UI.

## Hardwaretest

Die UI kann im Emulator geöffnet werden, aber die Service- und Capture-Abnahme erfolgt ausschließlich auf einem echten Tizen-9-TV. Bei Problemen zuerst `/health` und `http://<TV-IP>:45678` prüfen.
