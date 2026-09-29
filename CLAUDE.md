# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

BingBong is a webhook-triggered audio player system. An HTTP request hits the server, which broadcasts a WebSocket message to connected Windows desktop clients that play a sound on a selected audio device. Primary use case: play alert sounds when e-commerce orders come in.

## Architecture

Two independent components:

- **Server** (`server/`): Node.js Express HTTP server + `ws` WebSocket server. Single file (`server.js`, ~57 lines). HTTP endpoint `GET /bingbong/:sound` broadcasts `"Play <sound>"` to all connected WebSocket clients. Optional PIN authentication on WebSocket connect.
- **Client** (`client/`): C# .NET 8 WPF Windows desktop app. Connects to the server via WebSocket, receives sound trigger messages, plays audio files via NAudio WASAPI on a user-selected audio device. Minimizes to system tray.

Message flow: `HTTP request → Express server → WebSocket broadcast → WPF client → NAudio playback`

### Client Components

- `MainWindow.xaml/.cs` — Sidebar UI with five pages toggled by visibility in code-behind: Sounds (list, add via dialog or drag-drop), Output (device + master volume), Connection (URL, PIN, launch at startup), Integrate (copy-paste trigger snippets generated from config), Activity (log). Tray behaviour lives here too.
- `WebSocketClient.cs` — WebSocket connection with auto-reconnect (exponential backoff 3s–30s)
- `AudioPlayer.cs` — WASAPI device enumeration and audio playback (WAV/MP3/AIFF). The output device is stored and resolved **by friendly name** (`FindDeviceByName`: exact match, then shrinking prefix, then Windows default) at every play, because endpoint IDs change across reboots/re-plugs. Raises `DevicesChanged` (debounced) from an `IMMNotificationClient`.
- `ConfigManager.cs` — JSON config persistence to `%APPDATA%\bingbong\config.json` (`SelectedAudioDeviceName`, `TriggerBaseUrl`, `Volume`, `Volumes`, `Pin`). Legacy `SelectedAudioDeviceId` is migrated to a name on load.
- `App.xaml` — Dark theme: colour/brush resources, icon geometries, and templated styles (`NavButton`, `PlayButton`, `DarkComboBox`, `DarkCheckBox`, `DarkSlider`, `CodeBlock`).

### Message Formats (client accepts three)

1. `"Play <name>"` prefix (recommended, what the server sends)
2. Direct name match (e.g. `"bing bong"`)
3. JSON: `{"sound": "name"}`, `{"name": "name"}`, or `{"play": "name"}`

## Common Commands

### Server

```bash
cd server
npm install
npm start                    # or: node server.js
docker-compose up --build -d # run via Docker
```

Environment: `server/.env` (HTTP_PORT=3260, WS_PORT=3261, PIN=1234)

### Client

```bash
cd client
dotnet restore
dotnet build
dotnet run

# Standalone executable
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

### Testing a sound trigger

```bash
curl http://localhost:3260/bingbong/bing_bong
```

## Key Dependencies

| Component | Key Packages |
|-----------|-------------|
| Server | express 4.18, ws 8.0, dotenv 16.0 |
| Client | NAudio 2.2.1, Newtonsoft.Json 13.0.3, Hardcodet.NotifyIcon.Wpf 1.1.0 |

## Notes

- No test suites or linting configured in either component
- Server runs on Node 22-alpine in Docker
- Client targets .NET 8.0 Windows (WPF)
- Dark theme UI with purple accent (#7C3AED) defined in `App.xaml`
