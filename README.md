# HyperTizen - Tizen 8.0+ Fork

### Color up your Tizen TV with HyperTizen!
HyperTizen is a Hyperion / HyperHDR capturer for Tizen TVs.

---

## ⚠️ Honest Disclaimer

This project **doesn't actually work yet** (or may only partially work). It started with [someone else's excellent work](https://github.com/reisxd/HyperTizen), and most of the "development" was done by burning through way too many AI credits. There's a good chance that half the code is complete gibberish that just *looks* technical. Use at your own risk, and lower your expectations accordingly.

If you somehow find this useful, or just want to support questionable AI-driven development practices:

**[☕ Buy me a coffee/AI credits](https://ko-fi.com/H2H719VB0U)**

---

## About This Fork

This is an **experimental fork** of [HyperTizen](https://github.com/reisxd/HyperTizen) focused on exploring screen capture functionality for **Tizen 8.0+ TVs**. The original HyperTizen uses capture APIs that may have different availability on newer TV models. This fork provides a scaffolding structure for researching and implementing potential capture methods for Tizen 8.0+ compatibility.

### Status: Active Development

This fork is focused on implementing screen capture functionality for **Tizen 8.0+ TVs**.

**⚠️ Pixel Sampling Capture Method**: Implemented using `libvideoenhance.so`; still experimental and requires retesting on hardware after the latest fix.
- The Samsung S90C/Tizen 9 log confirms `ppi_ve_*`, `ScreenCapturePoints=2`, `SleepMS=20ms`, and Pixel Sampling selected.
- Sampling now uses four edge anchors. Each set/wait/read batch is completed before reusing the two native slots; left/right are sampled together, then top/bottom.
- Two 20ms batches imply a theoretical maximum of about 25 frames/s before processing overhead. Actual TV throughput has not yet been measured.
- Brief native read errors reuse a recent valid sample; abrupt color changes are briefly confirmed to suppress one-frame spikes. These filters and the four-edge mapping still need real-TV validation.
- Recent (up to 250ms) valid edge samples are reused through transient errors. If at least two anchors remain reliable, missing edges are estimated from the nearest reliable perimeter anchor; capture fails with per-edge operation/result details if fewer than two remain.
- Abrupt color changes are briefly confirmed to suppress one-frame spikes. The filters and four-edge mapping still need real-TV validation.
- Periodic diagnostics include capture timing, per-edge sample-error summaries, per-anchor position/read status, and the `estimated=` anchor count. The new failure summary emits even when no usable frame is captured and includes the native read return or exception; deploy the build tagged `Read diagnostics v1` to collect it.
- 10-bit RGB is converted to NV12/FlatBuffers. Color range and the existing BT.2020 matrix remain uncalibrated against HyperHDR.

**Latest S90C/Tizen 9 run:** The reference-backed `IVideoCapture::getVideoMainYUV` call returned `-1` with output dimensions `0x0`; its meaning is unknown. T9 Display was intentionally skipped. Pixel Sampling found `ppi_ve_*`, passed the condition test, and position calls returned `0`, but the first real capture failed because fewer than two edge samples were accepted. Three reads remained at `pixel=read entered` with zero recorded errors, so the native read outcome is still unresolved. TCP registration with FlatBuffers succeeded, but no frame was sent after that failed capture. Deploy the build tagged `Read diagnostics v1` and inspect its `API=...` and `sampleStatus` fields before changing the native call or sample mapping.

**Capture Architecture:** `CaptureMethodSelector` tries T9 Video → T9 Display → T8 SDK → T7 SDK → Pixel Sampling. T9 Display currently reports unavailable without making a native call.

---

## How to Use the WebSocket Log Viewer

This fork includes a **real-time browser-based log viewer** that's essential for debugging on your TV.

### Accessing Logs

1. **Start HyperTizen** on your TV
2. **Open your browser** on any device on the same network
3. **Enter your IP:** `<YOUR_TV_IP>`, `45678`
4. The log viewer will automatically connect and display real-time logs

### Log Viewer Features

- **Real-time streaming**: See logs as they happen
- **Auto-reconnect**: Automatically reconnects if connection is lost
- **Exponential backoff**: Smart retry logic prevents connection spam
- **Color-coded output**: Easy to read and filter
- **Persistent across sessions**: Reconnects when TV restarts HyperTizen

### Finding Your TV's IP Address

You can find your TV's IP address in:
- **Settings** → **General** → **Network** → **Network Status** → **IP Settings**

Or use your router's admin panel to find connected devices.

### Example

```
http://192.168.1.100:45678
```

The log viewer (`logs.html`). This is particularly useful for debugging capture issues, monitoring performance, and understanding what's happening on the TV.

---

## Browser-Based Control Panel

In addition to logs, HyperTizen provides a **full control panel** accessible from any browser on your network.

### Accessing the Control Panel

1. **Start HyperTizen** on your TV
2. **Open on your browser** on any device on the same network
3. **Open the control panel:**
   - Download `controls.html` from this repository and open it locally, then enter your TV's IP

### Control Panel Features

The control panel (`controls.html`) provides the same functionality as the HyperTizenUI but through a standard browser:

**Service Control:**
- ▶️ Start/Stop capture
- ⏸️ Pause/Resume capture
- 🔄 Restart HyperTizen service
- 🌈 Rainbow border indicator when capturing

**SSDP Device Management:**
- 🔍 Scan for Hyperion/HyperHDR devices on your network
- ✓ Select and apply devices
- View device details (name, URL)

**Live Monitoring:**
- 📊 Real-time service status (state, FPS, frames captured, errors)
- 📋 Live log streaming (same as logs.html)
- ⏱️ Uptime and connection status
- 🔌 Dual WebSocket status indicators (control + logs)

**WebSocket Connections:**
- Port **45677**: Control WebSocket (send commands)
- Port **45678**: Logs WebSocket (receive logs)
- Auto-reconnect with exponential backoff
- Persistent settings (saves TV IP in browser)

### Example


Open `controls.html` locally and enter:
```
TV IP: 192.168.1.100
Control Port: 45677
Logs Port: 45678
```

The control panel is perfect for:
- Managing HyperTizen from your phone/tablet/computer
- Testing capture without accessing the TV UI
- Monitoring service status during troubleshooting
- Selecting Hyperion/HyperHDR servers without using the TV remote

---

## What Works (and What Doesn't)

### Implemented & Functional

- **WebSocket Log Streaming**: Real-time debugging via browser (port 45678)
- **Browser-Based Control Panel**: Full service control and monitoring (control port 45677, logs port 45678)
- **Architecture Framework**: Structured `ICaptureMethod` interface with automatic fallback selection
- **System Info Detection**: Detects Tizen version and TV capabilities
- **Capture Method Selector**: Tests and selects best available method automatically
- **Log Level Filtering**: Client-side filtering in browser (Debug/Info/Warning/Error/Performance)
- **Pixel Sampling Capture (experimental; latest changes not yet verified on TV):**
   - Four edge anchors, sampled in two correctly sequenced batches when the TV exposes two slots
   - 10-bit RGB to NV12 conversion and FlatBuffers transmission
   - Short stale-sample hold and single-frame abrupt-change rejection
   - On the S90C, the 20ms API wait per batch limits the theoretical capture rate to about 25 FPS

### Partially Implemented

- **T8SDK Capture Method**: Scaffolding exists, core implementation not yet added
- **T7SDK Capture Method**: Scaffolding exists, core implementation not yet added

### Known Issues & Testing Needed

**T9 Capture Methods:**
- ⚠️ **T9 Video still fails on hardware:** The latest reference-backed `getVideoMainYUV` test returned `-1`, with `0x0` dimensions. Its meaning remains unknown; do not label it DRM or unsupported without evidence.
- **T9 Display is fail-closed:** Its native wrapper consumes arguments not represented by the old P/Invoke, and the full request structure/metadata are not known. The method is disabled rather than calling an unverified ABI. The previous `-2` result is from an older build.

**Pixel Sampling Method:**
- ⚠️ **First-frame read outcome unresolved:** The latest log reports three anchors reaching `pixel=read entered` and then fewer than two reliable samples with zero errors. This is inconsistent with the current result/exception bookkeeping. The source now logs a `Read diagnostics v1` summary, even for wholly unusable frames; rebuild/redeploy and inspect `API=...` and `sampleStatus` before changing the P/Invoke ABI.
- **Spatial detail:** One anchor per edge cannot reproduce gradients or multiple colors along the same edge.
- **Dark scenes/flicker:** Test near-black video and true black separately. No brightness floor is applied, so genuine black remains black; verify raw RGB10 and filtered values in the logs.
- **Output image:** Pixel Sampling still creates a 64×48 image with narrow colored edge bands and a black center. Check HyperHDR's source/LED preview if output remains dim; do not change the color range or BT.2020 matrix without testing.
- **Rate:** Two 20ms batches require at least 40ms per output frame (about 25 FPS maximum, before overhead).

### Testing the Pixel Sampling Implementation

To test the pixel sampling capture method on the S90C/Tizen 9:

1. **Build and install** the updated HyperTizen package on your TV
2. **Start the service** and monitor via WebSocket logs
3. **Watch for log messages** showing:
   - `PixelSampling: Library found, available`
   - `CAPTURE METHOD SELECTED: Pixel Sampling`
   - `Points: 2` and `Sleep: 20ms`
   - `Pre-calculated 4 edge anchors` and periodic `RGB10 raw`/`filtered` summaries
   - Capture timing and sample-error summaries
4. **Connect to HyperHDR/Hyperion** and verify ambient lighting displays correctly
5. **Test color accuracy**: Display pure colors (red, green, blue) and verify they appear correctly
6. **Test edge mapping**: Move content along edges and verify LEDs respond in correct direction

### Research Notes on Tizen 8.0+ Capture

- **Standard APIs**: May have different availability on Tizen 8.0+ compared to earlier versions
- **VideoEnhance Library**: `libvideoenhance.so` provides pixel sampling API that works on Tizen 6, 7, and 8+
- **VideoEnhance Library**: the S90C log confirms the Tizen 9 `ppi_ve_*` endpoints and condition query; actual colors and quality still require on-TV validation for each firmware.
- **Alternative Methods**: VTable-based frame capture (T8SDK) and legacy APIs (T7SDK) require further research
- **Tizen 9 methods:** The latest S90C run of the reference-backed T9 Video call returned `-1` with `0x0` dimensions; earlier probes also returned `-6`. These codes are unexplained. T9 Display is disabled pending a verified ABI. Pixel Sampling was selected, but its first real frame did not have two reliable edge samples.
- **Tizen 8/7 methods:** SDK/VTable methods remain unavailable or unimplemented on the tested device.
- **Framework Differences**: Tizen 8.0+ has architectural changes that affect some capture capabilities

---

## Installation

**Note:** Pixel Sampling is implemented but experimental. The latest four-anchor fix still needs testing on the S90C/Tizen 9 before its flicker, brightness, and color behavior can be confirmed.

To install HyperTizen on your Samsung TV running Tizen, you'll need Tizen Studio. You can download it from the [official website](https://developer.samsung.com/smarttv/develop/getting-started/setting-up-sdk/installing-tv-sdk.html).

### Installation Steps

1. Download the latest release from the [releases page](https://github.com/reisxd/HyperTizen/releases/latest) (or build from this fork).

2. Change the Host PC IP address to your PC's IP address by following [this guide](https://developer.samsung.com/smarttv/develop/getting-started/using-sdk/tv-device.html#Connecting-the-TV-and-SDK)

3. Install the package:
```bash
tizen install -n path/to/io.gh.reisxd.HyperTizen.tpk
```

Note that `tizen` is in `C:\tizen-studio\tools\ide\bin` on Windows and in `~/tizen-studio/tools/ide/bin` on Linux.

If you get `install failed[118, -12], reason: Check certificate error` error, you'll have to resign the package (see below).

4. Install TizenBrew to your TV. Follow [this guide](https://github.com/reisxd/TizenBrew/blob/main/docs/README.md).

5. **Install the HyperTizen UI** via TizenBrew's GitHub module manager:

   **Using the Module Manager:**
   - Press the **[GREEN]** button on your remote to open TizenBrew module manager
   - Navigate to "Add GitHub Module"
   - Enter the module path:

   **Install from this fork** (Tizen 8+ with pixel sampling):
   ```
   iceteaSA/HyperTizen/HyperTizenUI
   ```

   **Install from original repo** (Tizen 7 only):
   ```
   reisxd/HyperTizen/HyperTizenUI
   ```

   **Format:**
   ```
   <username>/<repository>/<folder-path>
   ```
   - Installs from the default branch (usually `main`)
   - `username/repository` - GitHub repository owner and name
   - `folder-path` - Path to the app folder within the repository

   > **Note:** To test development branches, you'll need to manually update files on your TV or wait for the branch to be merged to main.

### Resigning the Package

1. Change the Host PC IP address to your PC's IP address by following [this guide](https://developer.samsung.com/smarttv/develop/getting-started/using-sdk/tv-device.html#Connecting-the-TV-and-SDK)

2. After following the guide for the Tizen Studio installation, you have to create a certificate profile. You can follow [this guide](https://developer.samsung.com/smarttv/develop/getting-started/setting-up-sdk/creating-certificates.html).

3. Sign the package:
```bash
tizen package -t tpk -s YourProfileName -o path/to/output/dir -- path/to/io.gh.reisxd.HyperTizen.tpk

# Example:
# tizen package -t tpk -s HyperTizen -o release -- io.gh.reisxd.HyperTizen.tpk
```

4. You should now be able to install the package.

---

## Building from Source

See the original [HyperTizen documentation](./docs/README.md) for general build instructions.

For this fork, additional development tools may be required for testing and debugging the Tizen 8+ capture methods.

---

## Credits

### Original HyperTizen Project

This fork is based on [HyperTizen by reisxd](https://github.com/reisxd/HyperTizen).

Original HyperTizen provides Hyperion/HyperHDR capture support for Tizen TVs running Tizen 7.0 and earlier firmware versions.

### This Fork

Tizen 8.0+ capture research and implementation by the community. Special thanks to:
- Original HyperTizen contributors for the foundational codebase
- TizenBrew project for enabling homebrew development on Samsung TVs
- Everyone testing and contributing to Tizen 8+ capture research

### Related Projects

- **[HyperTizen](https://github.com/reisxd/HyperTizen)** - Original project (Tizen 7 support)
- **[TizenBrew](https://github.com/reisxd/TizenBrew)** - Homebrew for Samsung Tizen TVs
- **[Hyperion](https://hyperion-project.org/)** - Ambient lighting software
- **[HyperHDR](https://github.com/awawa-dev/HyperHDR)** - HDR-capable fork of Hyperion

---

## Contributing

Contributions are welcome! If you have ideas for implementing capture methods or improving the architecture, please:

1. Review the existing capture method scaffolding in `HyperTizen/Capture/`
2. Test your changes on actual Tizen hardware
3. Submit pull requests with detailed explanations
4. Use the WebSocket log viewer to document behavior and test results

---

## License

Same as original HyperTizen project.

---

## Disclaimer

This is experimental software for research and educational purposes. Use at your own risk. This fork is not affiliated with Samsung or the official Tizen project.

This fork provides scaffolding and structure for exploring capture methods on Tizen 8.0+ TVs. Capture functionality is not yet implemented. Compatibility with specific TV models and firmware versions depends on future implementation and testing.