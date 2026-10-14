using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Pubb.Unity;

static class Program
{
    static int passed;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
    static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    static async Task Main()
    {
        Test("waits for handshake and acknowledgement, decodes Unicode application payload", () => {
            using var h = new Harness(); var c = h.Client.Subscribe("chat"); string received = null;
            c.Bind("chat.message", data => received = (string)data["text"]);
            h.Client.Connect(); h.Tick(); Check(h.Socket.Sent.Count == 0, "Subscribed before handshake");
            h.Handshake(); Check(h.Socket.Sent.Count == 1 && !c.IsSubscribed, "Acknowledgement not awaited");
            h.Ack("chat"); h.Socket.Message(new JObject { ["channel"] = "chat", ["data"] = new JObject { ["name"] = "chat.message", ["data"] = new JObject { ["text"] = "Merhaba ♟️" } }.ToString() }); h.Tick();
            Check(received == "Merhaba ♟️" && c.IsSubscribed, "Payload not decoded");
        });
        Test("presence snapshot, add, remove and reset", () => {
            using var h = new Harness(new ImmediateAuth()); var c = h.Client.Subscribe("presence-room"); h.Client.Connect(); h.Handshake(); h.Tick();
            h.Ack(c.Name, new JObject { ["presence"] = new JObject { ["ids"] = new JArray("a"), ["hash"] = new JObject { ["a"] = new JObject { ["name"] = "Ada" } } } });
            Check(c.Members.Count == 1, "Missing presence snapshot");
            h.Socket.Control("pubb:member_added", c.Name, new JObject { ["user_id"] = "b", ["user_info"] = new JObject { ["name"] = "B" } }); h.Tick();
            Check(c.Members.Count == 2, "Member not added");
            h.Socket.Control("pubb:member_removed", c.Name, new JObject { ["user_id"] = "a" }); h.Tick();
            Check(!c.Members.ContainsKey("a"), "Member not removed"); h.Client.Disconnect(); Check(c.Members.Count == 0, "Stale presence survived disconnect");
        });
        Test("reconnect reauthorizes with fresh socket ID and restores subscriptions", () => {
            var auth = new ImmediateAuth(); using var h = new Harness(auth); var c = h.Client.Subscribe("private-room"); h.Client.Connect(); h.Handshake("one"); h.Tick(); h.Ack(c.Name);
            var old = h.Socket; old.Close(); h.Tick(); Check(h.Client.State == ConnectionState.Reconnecting && !c.IsSubscribed && h.Client.SocketId == null, "Disconnect state");
            h.Time += 40; h.Tick(); h.Handshake("two"); h.Tick(); h.Ack(c.Name);
            Check(auth.SocketIds.SequenceEqual(new[] { "one", "two" }) && c.IsSubscribed && old.Disposed, "Reauthorization failed");
        });
        Test("intentional disconnect cancels retries and pending auth", () => {
            var auth = new DelayedAuth(); using var h = new Harness(auth); h.Client.Subscribe("private-room"); h.Client.Connect(); h.Handshake();
            h.Client.Disconnect(); Check(auth.Token.IsCancellationRequested, "Auth not cancelled");
            auth.Complete(); h.Time += 100; h.Tick(); Check(h.Sockets.Count == 1 && h.Socket.Sent.Count == 0, "Late auth or retry survived disconnect");
        });
        Test("unsubscribe cancels auth and ignores stale acknowledgements", () => {
            var auth = new DelayedAuth(); using var h = new Harness(auth); h.Client.Subscribe("private-room"); h.Client.Connect(); h.Handshake();
            h.Client.Unsubscribe("private-room"); auth.Complete(); h.Tick(); Check(h.Socket.Sent.Count == 0, "Removed channel subscribed");
            var old = h.Client.Subscribe("chat"); h.Tick(); h.Client.Unsubscribe("chat"); var next = h.Client.Subscribe("chat"); h.Tick(); h.Tick();
            h.Ack("chat"); Check(!next.IsSubscribed, "Stale ack accepted"); h.Ack("chat"); Check(next.IsSubscribed, "Fresh ack rejected");
        });
        Test("legacy acknowledgement and callback isolation", () => {
            using var h = new Harness(); var c = h.Client.Subscribe("chat"); int invoked = 0;
            c.Bind("pubb:subscription_succeeded", _ => throw new Exception()).Bind("pubb:subscription_succeeded", _ => invoked++);
            h.Client.Connect(); h.Handshake(); h.Socket.Control("pusher_internal:subscription_succeeded", "chat", "{}"); h.Tick(); Check(invoked == 1 && c.IsSubscribed, "Legacy/callback failure");
        });
        Test("subscription rejection does not endlessly retry", () => {
            using var h = new Harness(); var c = h.Client.Subscribe("chat"); string error = null;
            c.Bind("pubb:subscription_error", e => error = (string)e); h.Client.Connect(); h.Handshake(); h.Socket.Control("subscription_error", "chat", "Channel unavailable"); h.Tick();
            for (int i = 0; i < 20; i++) h.Tick(); Check(error == "Channel unavailable" && !c.IsSubscribed && h.Socket.Sent.Count == 1, "Rejected channel retried");
        });
        Test("missing authorization and null responses fail closed", () => {
            using var h = new Harness(); h.Client.Subscribe("private-room"); h.Client.Connect(); h.Handshake(); h.Tick(); Check(h.Socket.Sent.Count == 0, "Private channel sent without auth");
            using var n = new Harness(new ImmediateAuth { Result = null }); n.Client.Subscribe("presence-room"); n.Client.Connect(); n.Handshake(); n.Tick(); Check(n.Socket.Sent.Count == 0, "Null auth accepted");
        });
        Test("timeouts for handshake, subscription, and authorization", () => {
            using var h = new Harness(); h.Client.Connect(); h.Time += 20; h.Tick(); Check(h.Client.State == ConnectionState.Reconnecting, "Handshake timeout missing");
            using var s = new Harness(); s.Client.Subscribe("chat"); s.Client.Connect(); s.Handshake(); s.Time += 20; s.Tick(); Check(s.Client.State == ConnectionState.Reconnecting, "Ack timeout missing");
            var auth = new DelayedAuth(); using var a = new Harness(auth); a.Client.Subscribe("private-room"); a.Client.Connect(); a.Handshake(); a.Time += 20; a.Tick(); Check(auth.Token.IsCancellationRequested && a.Socket.Sent.Count == 0, "Auth timeout missing");
        });
        Test("retries remain bounded across unstable handshakes", () => {
            using var h = new Harness(); h.Client.Connect();
            for (int i = 0; i < 6; i++) { h.Handshake(); h.Socket.Close(); h.Tick(); h.Time += 40; h.Tick(); }
            Check(h.Client.State == ConnectionState.Failed && h.Sockets.Count == 6, "Retry cap failed");
            h.Client.Connect(); Check(h.Client.State == ConnectionState.Connecting && h.Sockets.Count == 7, "Manual retry unavailable");
        });
        Test("subscription pacing prevents reconnect bursts", () => {
            using var h = new Harness(); for (int i = 0; i < 100; i++) h.Client.Subscribe("c" + i);
            Throws<InvalidOperationException>(() => h.Client.Subscribe("overflow")); h.Client.Connect(); h.Handshake();
            for (int i = 0; i < 100; i++) h.Client.Tick(); Check(h.Socket.Sent.Count == 1, "Burst at same time");
            for (int i = 0; i < 9; i++) h.Tick(); Check(h.Socket.Sent.Count <= 10, "Command rate exceeded");
        });
        Test("dispose during callback prevents sends and future callbacks", () => {
            using var h = new Harness(); h.Client.StateChanged += s => { if (s == ConnectionState.Connected) h.Client.Dispose(); };
            h.Client.Subscribe("chat"); h.Client.Connect(); h.Handshake(); Check(h.Socket.Sent.Count == 0 && h.Socket.Disposed, "Sent after disposal");
            Throws<ObjectDisposedException>(() => h.Client.Connect());
        });
        Test("validation rejects insecure endpoints and invalid channels", () => {
            Throws<ArgumentException>(() => new PubbClient(new PubbOptions { AppKey = "key", SocketUrl = "ws://example.com/socket" }, () => new FakeSocket()));
            Throws<ArgumentException>(() => new PubbClient(new PubbOptions { AppKey = "key", SocketUrl = "wss://example.com/socket?appKey=x" }, () => new FakeSocket()));
            using var h = new Harness(); Throws<ArgumentException>(() => h.Client.Subscribe(new string('ü', 101))); Throws<ArgumentException>(() => h.Client.Subscribe("debug-room"));
            h.Client.Connect(); Check(h.Socket.Uri.Query.Contains("protocol=2") && h.Socket.Uri.Query.Contains("appKey=test"), "Protocol URL");
        });
        Test("malformed protocol and failed sends recover", () => {
            using var h = new Harness(); h.Client.Connect(); h.Socket.Queue.Enqueue(new TransportEvent(TransportEventKind.Message, "not json")); h.Tick(); Check(h.Client.State == ConnectionState.Reconnecting, "Malformed packet ignored");
            using var s = new Harness(); s.Client.Subscribe("chat"); s.Client.Connect(); s.Socket.FailSend = true; s.Handshake(); s.Tick(); Check(s.Client.State == ConnectionState.Reconnecting, "Send failure ignored");
        });
        Test("disconnect inside an event stops remaining callbacks", () => {
            using var h = new Harness(); var c = h.Client.Subscribe("chat"); int invoked = 0;
            c.Bind("pubb:subscription_succeeded", _ => h.Client.Disconnect());
            c.Bind("pubb:subscription_succeeded", _ => invoked++);
            h.Client.Connect(); h.Handshake(); h.Ack("chat");
            Check(invoked == 0 && h.Client.State == ConnectionState.Disconnected, "Stale callback delivered after disconnect");
        });
        await NativeIntegration();
        Console.WriteLine($"{passed} tests passed.");
    }
    static async Task NativeIntegration()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build(); app.UseWebSockets();
        var commandSeen = new TaskCompletionSource<string>();
        app.Map("/socket", async context => {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            async Task Send(string text, bool end = true) => await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, end, CancellationToken.None);
            await Send("{\"event\":\"pubb:connection_established\",\"data\":\"{\\\"socket_id\\\":\\\"live\\\"}\"}");
            var bytes = new byte[4096]; var result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), CancellationToken.None);
            commandSeen.SetResult(Encoding.UTF8.GetString(bytes, 0, result.Count));
            await Send("{\"event\":\"pubb:subscription_succeeded\",\"channel\":\"chat\",\"data\":\"{}\"}");
            await Send("{\"channel\":\"chat\",", false);
            await Send("\"data\":\"{\\\"name\\\":\\\"chat.message\\\",\\\"data\\\":{\\\"text\\\":\\\"gerçek ♟\\\"}}\"}");
            await Task.Delay(500);
        });
        await app.StartAsync();
        try {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
            using var client = new PubbClient(new PubbOptions { AppKey = "test", SocketUrl = address.Replace("http:", "ws:") + "/socket" });
            string received = null; client.Subscribe("chat").Bind("chat.message", data => received = (string)data["text"]); client.Connect();
            for (int i = 0; i < 200 && received == null; i++) { client.Tick(); await Task.Delay(10); }
            Check(received == "gerçek ♟", "Native fragmented UTF-8 message failed"); Check((string)JObject.Parse(await commandSeen.Task)["action"] == "subscribe", "Wrong subscription command");
            passed++; Console.WriteLine("PASS real loopback WebSocket handshake, subscription and fragmented Unicode delivery");
        } finally { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
sealed class FakeSocket : IPubbTransport
{
    public readonly Queue<TransportEvent> Queue = new Queue<TransportEvent>();
    public readonly List<string> Sent = new List<string>(); public bool Disposed, FailSend; public Uri Uri;
    public void Connect(Uri uri) => Uri = uri;
    public bool TryReceive(out TransportEvent message) { if (Queue.Count > 0) { message = Queue.Dequeue(); return true; } message = default; return false; }
    public Task SendAsync(string message) { Sent.Add(message); return FailSend ? Task.FromException(new Exception()) : Task.CompletedTask; }
    public void Dispose() => Disposed = true;
    public void Message(JObject data) => Queue.Enqueue(new TransportEvent(TransportEventKind.Message, data.ToString()));
    public void Control(string name, string channel, JToken data) => Message(new JObject { ["event"] = name, ["channel"] = channel, ["data"] = data });
    public void Close() => Queue.Enqueue(new TransportEvent(TransportEventKind.Closed));
}
sealed class Harness : IDisposable
{
    public double Time; public readonly List<FakeSocket> Sockets = new List<FakeSocket>(); public FakeSocket Socket => Sockets.Last(); public readonly PubbClient Client;
    public Harness(IChannelAuthorizer auth = null) { Client = new PubbClient(new PubbOptions { AppKey = "test", Authorizer = auth }, () => { var s = new FakeSocket(); Sockets.Add(s); return s; }, () => Time); }
    public void Tick() { Time += .101; Client.Tick(); }
    public void Handshake(string id = "test") { Socket.Control("pubb:connection_established", null, new JObject { ["socket_id"] = id }.ToString()); Tick(); }
    public void Ack(string name, JToken data = null) { Socket.Control("pubb:subscription_succeeded", name, data ?? new JValue("{}")); Tick(); }
    public void Dispose() => Client.Dispose();
}
sealed class ImmediateAuth : IChannelAuthorizer
{
    public readonly List<string> SocketIds = new List<string>(); public ChannelAuthorization Result = new ChannelAuthorization { Auth = "key:signature", ChannelData = "{\"user_id\":\"a\"}" };
    public Task<ChannelAuthorization> AuthorizeAsync(string id, string channel, CancellationToken token) { SocketIds.Add(id); return Task.FromResult(Result); }
}
sealed class DelayedAuth : IChannelAuthorizer
{
    readonly TaskCompletionSource<ChannelAuthorization> completion = new TaskCompletionSource<ChannelAuthorization>(); public CancellationToken Token;
    public Task<ChannelAuthorization> AuthorizeAsync(string id, string channel, CancellationToken token) { Token = token; return completion.Task; }
    public void Complete() => completion.SetResult(new ChannelAuthorization { Auth = "key:sig" });
}
