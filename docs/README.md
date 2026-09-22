# HyperTizen – Build und Installation

HyperTizen wird für Tizen 9 als ein signiertes Hybrid-WGT gebaut. Das Paket enthält:

- die sichtbare Web-App `io.gh.reisxd.HyperTizenUI`,
- den nativen .NET-Service `io.gh.reisxd.HyperTizen`,
- die gemeinsame Package-ID `io.gh.reisxd.HyperTizen`.

Die Web-App startet den Service beim Öffnen mit Tizen Application Control. Eine separate TizenBrew-Installation oder ein externer Launcher ist nicht mehr erforderlich.

## Voraussetzungen

- Samsung-TV mit Tizen 9,
- Tizen Studio mit Web-, .NET- und TV-Komponenten,
- aktiviertes Developer Mode am TV,
- gültiges Samsung-TV-Signing-Profil,
- Hyperion/HyperHDR im selben Netzwerk,
- echter Tizen-9-TV für Capture- und Performance-Tests.

## Workspace

[`../tizen_workspace.yaml`](../tizen_workspace.yaml) ist als `hybrid` konfiguriert und listet `HyperTizenUI` mit dem abhängigen Serviceprojekt `HyperTizen`. Das Ziel ist API 9 und ARM.

Die Web-App-Konfiguration liegt in [`../HyperTizenUI/config.xml`](../HyperTizenUI/config.xml), die native Service-Konfiguration in [`../HyperTizen/HyperTizen/tizen-manifest.xml`](../HyperTizen/tizen-manifest.xml).

## Bauen und Signieren

Die Tizen-Core-CLI erzeugt zunächst das Web-WGT. Der .NET-Build erzeugt separat das Service-TPK. Beide Pakete werden anschließend mit `tz sign-pack hybrid` zu einem signierten WGT verbunden:

```bash
tz build --proj-dir "$PWD/../HyperTizenUI" --build-type Release --sign-profile hypertizen-ci
tz pack --proj-dir "$PWD/../HyperTizenUI" --type wgt \
	--out-path "$PWD/../HyperTizenUI.wgt" \
	--profiles-path "$PWD/../profiles.xml" \
	--sign-profile hypertizen-ci
dotnet build ../HyperTizen/HyperTizen.csproj -c Release \
	-p:TizenCreateTpkOnBuild=true
SERVICE_TPK=$(find "$PWD/../HyperTizen/bin/Release" -type f -name '*.tpk' -print -quit)

tz sign-pack hybrid \
	--web-pkg "$PWD/../HyperTizenUI.wgt" \
	--dotnet-pkg "$SERVICE_TPK" \
	--final-pkg "$PWD/../HyperTizen.wgt" \
	--profiles-path "$PWD/../profiles.xml" \
	--sign-profile hypertizen-ci \
	--deps-type hybrid
```

Die Ausgabe muss ein gemeinsames `.wgt` sein, nicht ein separates `.tpk` plus eine TizenBrew-App. Installation:

```bash
tizen install -n path/to/io.gh.reisxd.HyperTizen.wgt
```

Danach die Kachel **HyperTizen** starten.

### GitHub Actions

Der Workflow [`../.github/workflows/build-hypertizen-wgt.yml`](../.github/workflows/build-hypertizen-wgt.yml) erstellt das signierte Standalone-WGT bei Pushes auf `main`, `v*.*.*`-Tags oder über `workflow_dispatch`. Er verwendet die GitHub-Umgebungsvariablen `GITHUB_WORKSPACE`, `GITHUB_SHA`, `GITHUB_REF_NAME` und `RUNNER_TEMP` sowie die Repository-Secrets `TIZEN_AUTHOR_KEY` und `TIZEN_AUTHOR_KEY_PW`.

`TIZEN_AUTHOR_KEY` muss die Base64-kodierte Tizen-Author-`.p12` enthalten. Der Runner installiert die Tizen-10-Core-CLI und die Tizen-.NET-Workload, erzeugt aus beiden Secrets ein temporäres `profiles.xml`, baut das Web-WGT und das .NET-TPK getrennt und signiert anschließend mit `tz sign-pack hybrid`. Das Ergebnis steht als Actions-Artefakt bereit; ein `v*.*.*`-Tag erzeugt zusätzlich ein GitHub Release.

## Diagnose nach der Installation

- Lokaler Health-Handshake: `http://127.0.0.1:45677/health`
- Steuerung: `ws://<TV-IP>:45677`
- Logs: `http://<TV-IP>:45678`
- Browser-Control-Panel: `controls.html`

Der Health-Endpunkt liefert Service-State, aktive Capture-Methode, Tizen-Version, letzten Fehler und die verwendeten Ports. Die UI wartet auf `ready=true`, bevor sie WebSockets verbindet.

## Lifecycle-Hinweise

Der Service ist eine native Tizen-Service-Anwendung. Samsung unterstützt explizites Starten eines Service aus einer UI im selben Paket. Auto-Restart und Boot-Ausführung hängen von TV-Modell und Zertifikatsstufe ab; deshalb startet die UI den Service beim Öffnen nochmals explizit. Wird die UI beendet, läuft der Service unabhängig weiter.

## Capture-Abnahme

Die Capture-Priorität ist:

1. `libvideo-capture.so.0.1.0`
2. `libdisplay-capture-api.so.0.0`
3. T8-/T7-Kompatibilität
4. `libvideoenhance.so`

Native Bibliotheken werden vor der Verwendung geprüft. Buffer werden wiederverwendet, Strides bis zu FlatBuffers weitergegeben und nach drei aufeinanderfolgenden nativen Fehlern wird auf die nächste Methode gewechselt. `-4` bedeutet erwarteten DRM-Schutz; `-95` bedeutet fehlende Firmware-Unterstützung.

Freigabekriterien: mindestens 10 FPS bei 480×270, 30 Minuten ohne Crash oder kontinuierliches Speicherwachstum und gültige NV12-Frames mit nicht geschützten Quellen. Emulatoren sind dafür nicht ausreichend.
