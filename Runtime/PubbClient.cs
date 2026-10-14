using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Pubb.Unity
{
    /// <summary>Call all methods, including Tick in Update, on Unity's main thread.</summary>
    public sealed partial class PubbClient : IDisposable
    {
        private readonly Uri uri;
        private readonly IChannelAuthorizer authorizer;
        private readonly int maxRetries;
        private readonly double timeout;
        private readonly Func<IPubbTransport> factory;
        private readonly Func<double> clock;
        private readonly Random random = new Random();
        private readonly Dictionary<string, PubbChannel> channels = new Dictionary<string, PubbChannel>();
        private readonly Dictionary<string, Queue<PubbChannel>> acknowledgements = new Dictionary<string, Queue<PubbChannel>>();
        private readonly Queue<string> removals = new Queue<string>();
        private IPubbTransport transport;
        private CancellationTokenSource authCancellation;
        private Task<ChannelAuthorization> pendingAuth;
        private PubbChannel authChannel;
        private Task pendingSend;
        private double authStarted, attemptStarted, connectedAt, nextRetry, nextCommand;
        private int retries, generation;
        private bool running, disposed, ticking;
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public string SocketId { get; private set; }
        public event Action<ConnectionState> StateChanged;
        public event Action<string> Error;

        public PubbClient(PubbOptions options, Func<IPubbTransport> transportFactory, Func<double> monotonicClock = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            uri = options.BuildUri(); authorizer = options.Authorizer;
            maxRetries = options.MaxReconnectAttempts; timeout = options.TimeoutSeconds;
            factory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
            var stopwatch = Stopwatch.StartNew();
            clock = monotonicClock ?? (() => stopwatch.Elapsed.TotalSeconds);
        }

        public PubbChannel Subscribe(string name)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(name) || Encoding.UTF8.GetByteCount(name) > 200 || name.Any(char.IsControl) || name.StartsWith("debug-", StringComparison.Ordinal) || name.StartsWith("_", StringComparison.Ordinal))
                throw new ArgumentException("Use a nonblank channel name of at most 200 UTF-8 bytes without a reserved prefix.", nameof(name));
            if (channels.TryGetValue(name, out var existing)) return existing;
            if (channels.Count >= 100) throw new InvalidOperationException("At most 100 channels may be subscribed per connection.");
            var channel = new PubbChannel(name, Report);
            channels.Add(name, channel);
            return channel;
        }

        public void Unsubscribe(string name)
        {
            ThrowIfDisposed();
            if (!channels.TryGetValue(name, out var channel)) return;
            channels.Remove(name);
            if (channel.Status == 1 || channel.Status == 2) removals.Enqueue(name);
            if (authChannel == channel) CancelAuth();
            channel.Reset(); channel.UnbindAll();
        }

        public void Connect()
        {
            ThrowIfDisposed();
            if (running) return;
            running = true; retries = 0;
            StartAttempt();
        }

        public void Disconnect()
        {
            running = false;
            ClearConnection();
            SetState(ConnectionState.Disconnected);
        }

        private void StartAttempt()
        {
            ClearConnection();
            attemptStarted = clock();
            var currentGeneration = generation;
            SetState(ConnectionState.Connecting);
            if (!running || generation != currentGeneration) return;
            try { transport = factory(); transport.Connect(uri); }
            catch (Exception) { LoseConnection("Unable to open the WebSocket connection."); }
        }

        /// <summary>Pumps callbacks on the calling thread. Call once per Update, even while reconnecting.</summary>
        public void Tick()
        {
            ThrowIfDisposed();
            if (ticking) return;
            ticking = true;
            try { Pump(); }
            finally { ticking = false; }
        }

        private void Pump()
        {
            if (!running) return;
            var now = clock();
            if (transport == null)
            {
                if (State == ConnectionState.Reconnecting && now >= nextRetry) StartAttempt();
                return;
            }
            var current = transport;
            for (var i = 0; i < 64 && running && transport == current && current.TryReceive(out var message); i++)
            {
                if (message.Kind != TransportEventKind.Message) { LoseConnection("WebSocket connection interrupted."); return; }
                try { HandleMessage(message.Text); }
                catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidOperationException)
                { LoseConnection("Invalid message received from the server."); return; }
            }
            if (!running || current != transport) return;
            if (State == ConnectionState.Connecting)
            {
                if (now - attemptStarted >= timeout) LoseConnection("Connection handshake timed out.");
                return;
            }
            if (State != ConnectionState.Connected) return;
            if (now - connectedAt >= 30) retries = 0;
            if (channels.Values.Any(c => c.Status == 1 && now - c.SentAt >= timeout))
            { LoseConnection("Subscription acknowledgement timed out."); return; }
            if (pendingSend != null)
            {
                if (!pendingSend.IsCompleted)
                {
                    if (now - nextCommand >= timeout) LoseConnection("Sending a command timed out.");
                    return;
                }
                var failed = pendingSend.IsFaulted || pendingSend.IsCanceled;
                _ = pendingSend.Exception;
                pendingSend = null;
                if (failed) { LoseConnection("Sending a command failed."); return; }
            }
            if (now < nextCommand) return;
            if (removals.Count > 0)
            { Send(new JObject { ["action"] = "unsubscribe", ["channel"] = removals.Dequeue() }); return; }
            if (pendingAuth != null)
            {
                if (!pendingAuth.IsCompleted && now - authStarted < timeout) return;
                var task = pendingAuth;
                var channel = authChannel;
                if (!task.IsCompleted || task.IsCanceled || task.IsFaulted)
                {
                    _ = task.Exception;
                    CancelAuth();
                    Reject(channel, "Channel authorization failed or timed out. Check your session and channel permissions.");
                    return;
                }
                var result = task.Result;
                CancelAuth();
                if (IsCurrent(channel)) SubscribeCommand(channel, result);
                return;
            }
            var desired = channels.Values.FirstOrDefault(c => c.Status == 0);
            if (desired == null) return;
            if (desired.Name.StartsWith("private-", StringComparison.Ordinal) || desired.IsPresence)
            {
                if (authorizer == null) { Reject(desired, "Set an authorizer for private and presence channels."); return; }
                authChannel = desired; authStarted = now;
                authCancellation = new CancellationTokenSource();
                try { pendingAuth = authorizer.AuthorizeAsync(SocketId, desired.Name, authCancellation.Token) ?? throw new InvalidOperationException(); }
                catch (Exception) { CancelAuth(); Reject(desired, "Channel authorization could not start."); }
            }
            else SubscribeCommand(desired, null);
        }

        private void SubscribeCommand(PubbChannel channel, ChannelAuthorization authorization)
        {
            var command = new JObject { ["action"] = "subscribe", ["channel"] = channel.Name };
            if (authorization != null)
            {
                if (string.IsNullOrWhiteSpace(authorization.Auth) || (channel.IsPresence && string.IsNullOrWhiteSpace(authorization.ChannelData)))
                { Reject(channel, "Authorization response is missing auth or channel_data."); return; }
                command["auth"] = authorization.Auth;
                if (authorization.ChannelData != null) command["channel_data"] = authorization.ChannelData;
            }
            else if (channel.IsPresence || channel.Name.StartsWith("private-", StringComparison.Ordinal))
            { Reject(channel, "Authorization returned no signature."); return; }
            if (Encoding.UTF8.GetByteCount(command.ToString(Formatting.None)) > 16 * 1024)
            { Reject(channel, "Subscription command exceeds 16 KiB."); return; }
            if (!acknowledgements.TryGetValue(channel.Name, out var queue)) acknowledgements[channel.Name] = queue = new Queue<PubbChannel>();
            queue.Enqueue(channel);
            channel.Status = 1; channel.SentAt = clock();
            Send(command);
        }

        private void Send(JObject command)
        {
            // One command every 100 ms, with no catch-up burst after a slow frame.
            nextCommand = clock() + 0.1;
            try { pendingSend = transport.SendAsync(command.ToString(Formatting.None)); }
            catch (Exception) { LoseConnection("Sending a command failed."); }
        }

        private static JToken Decode(JToken data) => data?.Type == JTokenType.String ? JToken.Parse((string)data) : data;

        private void HandleMessage(string json)
        {
            var packet = JObject.Parse(json);
            var eventName = (string)packet["event"];
            if (eventName == "pubb:connection_established")
            {
                if (State != ConnectionState.Connecting) return;
                SocketId = (string)Decode(packet["data"])?["socket_id"];
                if (string.IsNullOrWhiteSpace(SocketId)) throw new JsonException("Missing socket ID.");
                connectedAt = clock(); SetState(ConnectionState.Connected); return;
            }
            if (eventName == "error") { Report("Server error: " + (string)packet["data"]); return; }
            var name = (string)packet["channel"];
            if (name == null) return;
            if (eventName == "pubb:subscription_succeeded" || eventName == "pusher_internal:subscription_succeeded" || eventName == "subscription_error")
            {
                if (!acknowledgements.TryGetValue(name, out var queue) || queue.Count == 0) return;
                var target = queue.Dequeue();
                if (queue.Count == 0) acknowledgements.Remove(name);
                if (!IsCurrent(target)) return;
                if (eventName == "subscription_error") Reject(target, (string)packet["data"] ?? "Subscription rejected.");
                else target.Subscribed(Decode(packet["data"]));
                return;
            }
            if (!channels.TryGetValue(name, out var channel) || !channel.IsSubscribed) return;
            if (eventName == "pubb:member_added" || eventName == "pubb:member_removed")
            { if (channel.IsPresence) channel.MemberEvent(eventName, Decode(packet["data"])); return; }
            if (eventName != null) return;
            var data = Decode(packet["data"]);
            var applicationEvent = (string)data?["name"];
            if (!string.IsNullOrWhiteSpace(applicationEvent)) channel.Emit(applicationEvent, data["data"]);
        }

        private bool IsCurrent(PubbChannel channel) => channels.TryGetValue(channel.Name, out var current) && current == channel;
        private void Reject(PubbChannel channel, string error)
        {
            if (!IsCurrent(channel)) return;
            channel.Status = 3;
            channel.Emit("pubb:subscription_error", new JValue(error));
            Report(error);
        }
        private void LoseConnection(string error)
        {
            ClearConnection();
            var currentGeneration = generation;
            Report(error);
            if (!running || generation != currentGeneration) return;
            if (retries >= maxRetries) { running = false; SetState(ConnectionState.Failed); return; }
            nextRetry = clock() + Math.Min(30, 2 * Math.Pow(2, retries++)) * (1 + random.NextDouble() * 0.25);
            SetState(ConnectionState.Reconnecting);
        }
        private void CancelAuth()
        {
            var task = pendingAuth;
            pendingAuth = null; authChannel = null;
            authCancellation?.Cancel(); authCancellation?.Dispose(); authCancellation = null;
            if (task != null) Observe(task);
        }
        private static async void Observe(Task task) { try { await task.ConfigureAwait(false); } catch (Exception) { } }
        private void ClearConnection()
        {
            generation++;
            CancelAuth();
            transport?.Dispose(); transport = null;
            if (pendingSend != null) Observe(pendingSend);
            pendingSend = null; SocketId = null;
            acknowledgements.Clear(); removals.Clear(); nextCommand = 0;
            foreach (var channel in channels.Values) channel.Reset();
        }
        private void SetState(ConnectionState state)
        {
            if (State == state) return;
            State = state;
            if (StateChanged == null) return;
            foreach (Action<ConnectionState> callback in StateChanged.GetInvocationList())
            {
                if (State != state) break;
                try { callback(state); } catch (Exception) { Report("A connection state handler threw an exception."); }
            }
        }
        private void Report(string error)
        {
            if (Error == null) return;
            foreach (Action<string> callback in Error.GetInvocationList())
            { try { callback(error); } catch (Exception) { } }
        }
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(PubbClient)); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Disconnect();
            foreach (var channel in channels.Values) channel.UnbindAll();
            channels.Clear(); StateChanged = null; Error = null;
        }
    }
}
