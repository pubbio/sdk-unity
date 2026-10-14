#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pubb.Unity
{
    public sealed class NativeWebSocketTransport : IPubbTransport
    {
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly ConcurrentQueue<TransportEvent> messages = new ConcurrentQueue<TransportEvent>();
        private bool disposed;
        public void Connect(Uri uri)
        {
            // ClientWebSocket responds to protocol Ping frames while ReceiveAsync is active.
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            _ = ReceiveLoop(uri, cancellation.Token);
        }
        private async Task ReceiveLoop(Uri uri, CancellationToken token)
        {
            try
            {
                await socket.ConnectAsync(uri, token).ConfigureAwait(false);
                var buffer = new byte[8192];
                using (var message = new MemoryStream())
                {
                    while (!token.IsCancellationRequested)
                    {
                        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 1024 * 1024)
                            throw new InvalidDataException("Unexpected or oversized WebSocket message.");
                        message.Write(buffer, 0, result.Count);
                        if (!result.EndOfMessage) continue;
                        if (messages.Count >= 256) throw new InvalidDataException("Receive queue exceeded its capacity.");
                        messages.Enqueue(new TransportEvent(TransportEventKind.Message, new UTF8Encoding(false, true).GetString(message.ToArray())));
                        message.SetLength(0);
                    }
                }
            }
            catch (Exception) when (!token.IsCancellationRequested)
            { messages.Enqueue(new TransportEvent(TransportEventKind.Error)); }
            catch (Exception) { /* Cancellation/abort races must not fault the detached receive task. */ }
            finally { messages.Enqueue(new TransportEvent(TransportEventKind.Closed)); }
        }
        public bool TryReceive(out TransportEvent message) => messages.TryDequeue(out message);
        public Task SendAsync(string message)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation.Token);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cancellation.Cancel(); socket.Abort(); socket.Dispose(); cancellation.Dispose();
        }
    }
}
#endif
