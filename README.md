# BingBong

A webhook-triggered audio player. Send an HTTP request to the server and it plays a sound on a specific speaker connected to a Windows machine. Think: a noise maker that dings an external speaker whenever someone makes a purchase.

## How It Works

```
HTTP GET /bingbong/:sound  →  Node.js Server  →  WebSocket Broadcast  →  C# Windows Client  →  Audio on Selected Speaker
```

The **server** exposes an HTTP endpoint that accepts a sound name. It broadcasts the command over WebSocket to all connected clients. The **client** is a Windows app that listens for these commands and plays the matching audio file through a chosen output device (e.g., a USB speaker).

## Server Setup

The server runs via Docker.

1. Navigate to the server directory:
   ```bash
   cd server
   ```

2. Copy the example environment file and configure it:
   ```bash
   cp .env.example .env
   ```

   | Variable    | Default | Description                          |
   |-------------|---------|--------------------------------------|
   | `HTTP_PORT` | `3000`  | Port for the HTTP webhook endpoint   |
   | `WS_PORT`   | `8080`  | Port for WebSocket client connections|
   | `PIN`       | *(empty)* | Optional PIN to authenticate clients |

3. Start the server:
   ```bash
   docker-compose up --build -d
   ```

## Client Setup

The client is a C# WPF app targeting .NET 8 (Windows only).

### Building

```bash
cd client
dotnet restore
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

This produces a single standalone `SoundRelay.exe` — no .NET runtime installation required.

### Configuring

Launch the app and configure it through the UI:

1. **WebSocket URL** — Point to your server (e.g., `ws://yourserver:8080`)
2. **PIN** — If the server has a PIN set, enter it here
3. **Audio Device** — Select which speaker/output device to play sounds on
4. **Volume** — Adjust playback volume
5. **Sound Mappings** — Map trigger names to audio files on disk (WAV, MP3, AIFF)

Configuration is saved to `%APPDATA%\SoundRelay\config.json`.

### Features

- System tray support — minimizes to tray instead of closing
- Auto-reconnect with exponential backoff (3s–30s)
- Optional launch at Windows startup
- Dark-themed UI

## Usage

Trigger a sound by hitting the HTTP endpoint:

```bash
curl http://yourserver:3260/bingbong/bing_bong
```

The server broadcasts `"Play bing_bong"` to all connected clients. Any client with a sound mapping named `bing_bong` will play the associated audio file.

The response tells you how many clients received the command:

```json
{"sound": "bing_bong", "clients": 1}
```

### Integration

Call the endpoint from anything that can make HTTP requests — Shopify webhooks, Zapier, IFTTT, a cron job, or a simple script.

## Security

The optional PIN prevents unauthorized clients from connecting. Set `PIN` in the server's `.env` and enter the same PIN in the client UI. Clients that don't provide the correct PIN are rejected.

This is basic authentication — if you're exposing the server to the internet, put it behind a reverse proxy with HTTPS.
