# bingbong

A lightweight Windows desktop application that listens on a WebSocket for messages and plays audio files through a specific output device.

## Features

- **WebSocket Listener** — Connects to any WebSocket server and listens for trigger messages
- **Auto-Reconnect** — Exponential backoff reconnection (3s → 6s → 12s → ... up to 30s max)
- **Device-Specific Audio** — Choose exactly which audio output device to play through (e.g., desk speakers, not headphones)
- **Device Remembered By Name** — The output device is stored by its label, not its Windows ID, so it survives reboots and re-plugs. If the exact label is gone the app trims the name from the end until something matches (handles ` [2]` / `.2` suffixes), and otherwise uses the Windows default until the device returns. Device changes are picked up live.
- **Sound Mappings** — Map trigger names to audio files (WAV, MP3, AIFF); drag a file onto the window to add it
- **Integrate Page** — Copy-paste trigger URL, curl, and Node.js snippets filled in with your server address and PIN
- **System Tray** — Minimizes to tray, shows notifications on connection errors
- **Dark Theme UI** — Sidebar pages: Sounds, Output, Connection, Integrate, Activity

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (to build)
- Or just the .NET 8 Runtime (to run a published build)

## Build & Run

```bash
cd bingbong
dotnet restore
dotnet build
dotnet run
```

## Publish a Self-Contained Executable

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

This produces a single `bingbong.exe` in the `./publish` folder that runs without needing .NET installed.

## WebSocket Message Formats

The app recognizes several message formats:

### 1. "Play" prefix (recommended)
```
Play bing bong
Play alert
Play victory
```

### 2. Direct name match
```
bing bong
alert
victory
```

### 3. JSON format
```json
{"sound": "bing bong"}
{"name": "alert"}
{"play": "victory"}
```

Matching is **case-insensitive**, so `Play BING BONG` and `play bing bong` both work.

## Configuration

Settings are saved to `%APPDATA%\bingbong\config.json` and persist across restarts.

### Config fields:
| Field | Description | Default |
|-------|-------------|---------|
| `WebSocketUrl` | WebSocket server address | `ws://localhost:8080` |
| `ReconnectDelayMs` | Initial reconnect delay | `3000` |
| `MaxReconnectDelayMs` | Max reconnect delay (backoff cap) | `30000` |
| `SelectedAudioDeviceName` | Friendly name of the output device (matched by name, then shrinking prefix) | `""` (Windows default) |
| `TriggerBaseUrl` | HTTP address used in the Integrate snippets; blank derives it from `WebSocketUrl` | `""` |
| `Volume` | Master playback volume (0.0–1.0) | `1.0` |
| `Volumes` | Per-sound volume, keyed by sound name | `{}` |
| `Pin` | Server PIN sent on connect | `""` |

Sounds themselves are the files in `%APPDATA%ingbong\sounds\`; the trigger name is the file name without its extension.
A legacy `SelectedAudioDeviceId` is converted to the device's name on first load and dropped.
| `MinimizeToTray` | Minimize to tray on close | `true` |
| `StartMinimized` | Start minimized | `false` |

## Testing with a Simple WebSocket Server

You can test with a quick Node.js server:

```javascript
// test-server.js
const WebSocket = require('ws');
const wss = new WebSocket.Server({ port: 8080 });

wss.on('connection', (ws) => {
    console.log('Client connected');

    // Send a test sound every 5 seconds
    setInterval(() => {
        ws.send('Play bing bong');
        console.log('Sent: Play bing bong');
    }, 5000);
});

console.log('WebSocket server running on ws://localhost:8080');
```

Or with Python:

```python
# test_server.py
import asyncio
import websockets

async def handler(websocket):
    print("Client connected")
    while True:
        await asyncio.sleep(5)
        await websocket.send("Play bing bong")
        print("Sent: Play bing bong")

async def main():
    async with websockets.serve(handler, "localhost", 8080):
        print("WebSocket server running on ws://localhost:8080")
        await asyncio.Future()

asyncio.run(main())
```

## Architecture

| File | Purpose |
|------|---------|
| `MainWindow.xaml/.cs` | WPF UI — sidebar pages (Sounds, Output, Connection, Integrate, Activity), system tray |
| `WebSocketClient.cs` | Async WebSocket with auto-reconnect |
| `AudioPlayer.cs` | NAudio WASAPI playback; name-based device matching; device-change notifications |
| `ConfigManager.cs` | JSON config persistence |

## NuGet Dependencies

- **NAudio** (2.2.1) — Audio playback with device selection via WASAPI
- **Newtonsoft.Json** (13.0.3) — Config serialization
- **Hardcodet.NotifyIcon.Wpf** (1.1.0) — System tray integration
