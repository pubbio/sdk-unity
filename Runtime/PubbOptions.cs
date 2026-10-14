using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pubb.Unity
{
    public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting, Failed }

    public sealed class PubbOptions
    {
        public string AppKey { get; set; }
        public string SocketUrl { get; set; } = "wss://ws.pubb.io/socket";
        public IChannelAuthorizer Authorizer { get; set; }
        public int MaxReconnectAttempts { get; set; } = 5;
        public double TimeoutSeconds { get; set; } = 15;

        internal Uri BuildUri()
        {
            if (string.IsNullOrWhiteSpace(AppKey)) throw new ArgumentException("A public app key is required.");
            if (MaxReconnectAttempts < 0 || MaxReconnectAttempts > 20) throw new ArgumentOutOfRangeException(nameof(MaxReconnectAttempts));
            if (double.IsNaN(TimeoutSeconds) || double.IsInfinity(TimeoutSeconds) || TimeoutSeconds < 1 || TimeoutSeconds > 120)
                throw new ArgumentOutOfRangeException(nameof(TimeoutSeconds));
            var uri = ValidateEndpoint(SocketUrl, true);
            if (!string.IsNullOrEmpty(uri.Query)) throw new ArgumentException("SocketUrl must not include query parameters; set AppKey separately.");
            return new UriBuilder(uri) { Query = "appKey=" + Uri.EscapeDataString(AppKey) + "&protocol=2" }.Uri;
        }

        internal static Uri ValidateEndpoint(string value, bool socket)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Use an absolute endpoint without embedded credentials or a fragment.");
            var secure = socket ? "wss" : "https";
            var local = socket ? "ws" : "http";
            if (uri.Scheme != secure && !(uri.Scheme == local && uri.IsLoopback))
                throw new ArgumentException("Use TLS. Unencrypted endpoints are allowed only on loopback for development.");
            return uri;
        }
    }

    public sealed class ChannelAuthorization
    {
        public string Auth { get; set; }
        public string ChannelData { get; set; }
    }

    public interface IChannelAuthorizer
    {
        Task<ChannelAuthorization> AuthorizeAsync(string socketId, string channelName, CancellationToken cancellationToken);
    }

    public enum TransportEventKind { Message, Closed, Error }

    public readonly struct TransportEvent
    {
        public TransportEventKind Kind { get; }
        public string Text { get; }
        public TransportEvent(TransportEventKind kind, string text = null) { Kind = kind; Text = text; }
    }

    /// <summary>A fresh transport is created for each connection attempt.</summary>
    public interface IPubbTransport : IDisposable
    {
        void Connect(Uri uri);
        bool TryReceive(out TransportEvent message);
        Task SendAsync(string message);
    }
}
