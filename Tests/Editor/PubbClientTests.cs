using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pubb.Unity;

public sealed class PubbClientTests
{
    private sealed class Socket : IPubbTransport
    {
        public readonly Queue<TransportEvent> Incoming = new Queue<TransportEvent>();
        public readonly List<string> Sent = new List<string>();
        public bool Disposed;
        public void Connect(Uri uri) { }
        public bool TryReceive(out TransportEvent message) {
            if (Incoming.Count > 0) { message = Incoming.Dequeue(); return true; }
            message = default; return false;
        }
        public Task SendAsync(string text) { Sent.Add(text); return Task.CompletedTask; }
        public void Dispose() { Disposed = true; }
        public void Receive(string json) { Incoming.Enqueue(new TransportEvent(TransportEventKind.Message, json)); }
    }
    [Test]
    public void DeliversAfterAcknowledgementOnTickThread()
    {
        var socket = new Socket();
        using (var client = new PubbClient(new PubbOptions { AppKey = "test" }, () => socket))
        {
            var channel = client.Subscribe("chat");
            string received = null;
            channel.Bind("chat.message", data => received = (string)data["text"]);
            client.Connect(); client.Tick();
            Assert.That(socket.Sent, Is.Empty);
            socket.Receive("{\"event\":\"pubb:connection_established\",\"data\":\"{\\\"socket_id\\\":\\\"test\\\"}\"}");
            client.Tick(); Assert.That(socket.Sent.Count, Is.EqualTo(1));
            socket.Receive("{\"event\":\"pubb:subscription_succeeded\",\"channel\":\"chat\",\"data\":\"{}\"}");
            socket.Receive(new JObject { ["channel"] = "chat", ["data"] = new JObject { ["name"] = "chat.message", ["data"] = new JObject { ["text"] = "Hello ♟" } }.ToString() }.ToString());
            Assert.That(received, Is.Null);
            client.Tick(); Assert.That(channel.IsSubscribed, Is.True); Assert.That(received, Is.EqualTo("Hello ♟"));
        }
        Assert.That(socket.Disposed, Is.True);
    }
    [Test]
    public void DisconnectCancelsRetryAndKeepsDesiredChannel()
    {
        var sockets = 0; double now = 0; var socket = new Socket();
        using (var client = new PubbClient(new PubbOptions { AppKey = "test" }, () => { sockets++; return socket; }, () => now))
        {
            var channel = client.Subscribe("chat"); client.Connect(); client.Disconnect(); now = 100; client.Tick();
            Assert.That(sockets, Is.EqualTo(1)); Assert.That(client.Subscribe("chat"), Is.SameAs(channel));
            Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));
        }
    }
    [Test]
    public void RejectsPublicInternetCleartextAndReservedChannels()
    {
        Assert.Throws<ArgumentException>(() => new PubbClient(new PubbOptions { AppKey = "test", SocketUrl = "ws://example.com/socket" }));
        using (var client = new PubbClient(new PubbOptions { AppKey = "test" }, () => new Socket()))
            Assert.Throws<ArgumentException>(() => client.Subscribe("debug-room"));
    }
}
