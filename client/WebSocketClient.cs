using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace bingbong
{
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting
    }

    public class WebSocketClient : IDisposable
    {
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private readonly object _lock = new();
        private bool _disposed;
        private bool _intentionalDisconnect;

        public int ReconnectDelayMs { get; set; } = 3000;
        public int MaxReconnectDelayMs { get; set; } = 30000;
        public string Pin { get; set; } = string.Empty;

        public event Action<string>? MessageReceived;
        public event Action<ConnectionState>? StateChanged;
        public event Action<string>? Error;
        public event Action<string>? Log;

        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

        public async Task ConnectAsync(string url)
        {
            _intentionalDisconnect = false;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            await ConnectWithRetriesAsync(url, token);
        }

        private async Task ConnectWithRetriesAsync(string url, CancellationToken token)
        {
            int delay = ReconnectDelayMs;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    SetState(ConnectionState.Connecting);
                    LogMessage($"Connecting to {url}...");

                    _ws?.Dispose();
                    _ws = new ClientWebSocket();

                    var uriString = string.IsNullOrEmpty(Pin)
                        ? url
                        : $"{url}{(url.Contains('?') ? '&' : '?')}pin={Pin}";
                    var uri = new Uri(uriString);
                    await _ws.ConnectAsync(uri, token);

                    SetState(ConnectionState.Connected);
                    LogMessage("Connected.");
                    delay = ReconnectDelayMs; // Reset backoff

                    await ReceiveLoopAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogMessage($"Connection error: {ex.Message}");
                    Error?.Invoke(ex.Message);

                    if (_intentionalDisconnect || token.IsCancellationRequested)
                        break;

                    SetState(ConnectionState.Reconnecting);
                    LogMessage($"Reconnecting in {delay / 1000.0:F1}s...");

                    try
                    {
                        await Task.Delay(delay, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // Exponential backoff
                    delay = Math.Min(delay * 2, MaxReconnectDelayMs);
                }
            }

            SetState(ConnectionState.Disconnected);
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[4096];

            while (_ws?.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (result.CloseStatus == (WebSocketCloseStatus)4001)
                        {
                            LogMessage("Authentication failed: wrong PIN.");
                            Error?.Invoke("Authentication failed: wrong PIN.");
                            _intentionalDisconnect = true;
                        }
                        else
                        {
                            LogMessage("Server closed connection.");
                        }
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                }
                while (!result.EndOfMessage);

                if (sb.Length > 0)
                {
                    string message = sb.ToString();
                    LogMessage($"Received: {message}");
                    MessageReceived?.Invoke(message);
                }
            }
        }

        public async Task DisconnectAsync()
        {
            _intentionalDisconnect = true;
            _cts?.Cancel();

            if (_ws?.State == WebSocketState.Open)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "User disconnect", timeout.Token);
                }
                catch { /* Best effort */ }
            }

            SetState(ConnectionState.Disconnected);
            LogMessage("Disconnected.");
        }

        private void SetState(ConnectionState state)
        {
            State = state;
            StateChanged?.Invoke(state);
        }

        private void LogMessage(string msg)
        {
            Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _intentionalDisconnect = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _ws?.Dispose();
        }
    }
}
