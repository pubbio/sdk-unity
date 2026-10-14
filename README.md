# pubb Unity SDK

`io.pubb.unity` 0.1.0 is a Unity Package Manager client package for live chat,
lobbies, presence, ready states, score updates and turn-based games such as cards,
okey, chess and strategy. pubb carries events; your backend validates commands,
stores history and owns the game state.

## Install

Target: Unity 2022.3 LTS or newer with .NET Standard 2.1 API compatibility.
Dependencies: Unity's Newtonsoft JSON 3.2.1, UnityWebRequest and IMGUI (sample).

- Git installation: **Window → Package Manager → + → Add package from git URL**,
  then paste `https://github.com/pubbio/sdk-unity.git#v0.1.0`. Git must be installed
  and available to Unity. The version tag pins this package release.
- Local source: **Window → Package Manager → + → Add package from disk**, then
  select this directory's `package.json`.
- Packaged release: download `io.pubb.unity-0.1.0.tgz` from the
  [Unity SDK guide](https://pubb.io/docs/sdk/unity), then choose **Add package from tarball**.
- A consumer using an assembly definition must reference `Pubb.Unity` and
  `Unity.Newtonsoft.Json` when using `JToken`.

Source: [pubbio/sdk-unity](https://github.com/pubbio/sdk-unity).
No Asset Store or OpenUPM installation is required.
The tarball includes source, documentation and an importable sample. Build it with
`python3 tools~/pack.py`; output goes to `dist~/`. Use `--output /path/to/downloads`
to choose another destination. The same command works in this standalone repository
and the monorepo copy. GitHub Actions runs the core, bridge and backend tests and
checks that repeated package builds have the same checksum.

## First event

Attach this component to a GameObject and enter the public app key:

```csharp
using Pubb.Unity;
using UnityEngine;

public sealed class PubbQuickstart : MonoBehaviour
{
    [SerializeField] private string appKey = "YOUR_PUBLIC_APP_KEY";
    private PubbClient pubb;

    private void OnEnable()
    {
        pubb = new PubbClient(new PubbOptions { AppKey = appKey });
        pubb.Error += error => Debug.LogWarning(error);
        pubb.StateChanged += state => Debug.Log("pubb: " + state);
        pubb.Subscribe("notifications")
            .Bind("pubb:subscription_succeeded", _ => Debug.Log("Ready to receive"))
            .Bind("message.sent", data => Debug.Log((string)data["message"]));
        pubb.Connect();
    }
    private void Update() => pubb?.Tick();
    private void OnApplicationPause(bool paused)
    {
        if (paused) pubb?.Disconnect();
        else pubb?.Connect();
    }
    private void OnDisable() { pubb?.Dispose(); pubb = null; }
}
```

After `Ready to receive`, publish `message.sent` with `{"message":"Hello Unity!"}`
to `notifications` in your application's Live console, or publish from a trusted
backend using a pubb server SDK / HTTP API. Keep the app secret out of Unity
source, Inspector fields, PlayerPrefs and player binaries. Public channels are
readable by anyone with the public app key; use restricted channels for real chat.

## Private and presence channels

```csharp
using System.Collections.Generic;
using Pubb.Unity;

var client = new PubbClient(new PubbOptions {
    AppKey = "YOUR_PUBLIC_APP_KEY",
    Authorizer = new HttpChannelAuthorizer("https://game.example.com/auth",
        () => new Dictionary<string, string> {
            ["Authorization"] = "Bearer " + currentSessionToken
        })
});
var lobby = client.Subscribe("presence-lobby-42");
lobby.Bind("pubb:subscription_succeeded", _ => {
    // Refetch the saved lobby/game state here, on initial join AND reconnect.
    foreach (var member in lobby.Members)
        UnityEngine.Debug.Log(member.Key);
});
lobby.Bind("pubb:member_added", member => UnityEngine.Debug.Log(member["user_id"]));
lobby.Bind("pubb:member_removed", member => UnityEngine.Debug.Log(member["user_id"]));
client.Connect();
// Retain client, call Tick in Update, and Dispose on owner teardown.
```

The headers callback is evaluated for every authorization attempt, so refreshed
session tokens are used. The endpoint receives JSON `{socket_id, channel_name}`.
Authenticate the session, check that exact channel's membership, and return
`{auth}` or `{auth, channel_data}` from your server SDK. Derive presence identity
from the session. Preserve the signed `channel_data` string without reserializing.
A failed subscription emits `pubb:subscription_error`; after fixing credentials,
unsubscribe and subscribe again, or intentionally disconnect and reconnect.
The authorizer does not follow redirects. HTTPS is required except loopback HTTP.

## Chat and lobby sample

Import **Chat and lobby** in this package's Samples tab, then follow its README.
The sample includes a Unity component and a small Node.js 22+ backend with two
configured player sessions, scoped authorization, chat, readiness, idempotent
commands, versioned state and a bounded publication retry queue. The backend is
optional teaching material; use your application's existing backend in production.

## API

| API | Behavior |
| --- | --- |
| `new PubbClient(options)` | Chooses native ClientWebSocket or the WebGL browser bridge. |
| `Connect()` | Starts one connection. Calling it again while running is a no-op. |
| `Tick()` | Pumps messages and reconnects; call once per Unity `Update`. |
| `Subscribe(name)` | Returns the same channel for repeated subscriptions. Up to 100 desired channels. |
| `Unsubscribe(name)` | Removes the channel, its listeners and pending authorization. |
| `Disconnect()` | Cancels connection/retries/auth; keeps desired channels and handlers for a later Connect. |
| `Dispose()` | Disconnects permanently and clears handlers. |
| `State`, `StateChanged` | Disconnected, Connecting, Connected, Reconnecting, Failed. Connected means handshake complete, not channel subscribed. |
| `SocketId` | Current server socket ID; cleared after disconnection. |
| `Error` | Transport, protocol, authorization and handler errors; response bodies/tokens are not logged. |
| `channel.Bind(name, Action<JToken>)` | Registers once per callback. Chainable. |
| `channel.Unbind(name, callback)` | Removes one callback; omit callback to remove that event's listeners. |
| `channel.UnbindAll()` | Removes all listeners on this channel. |
| `channel.IsSubscribed` | True after the server acknowledgement. |
| `channel.Members` | Presence member map copy keyed by user ID. Cleared on connection loss. Treat payload tokens as read-only. |

Use `pubb:subscription_succeeded`, `pubb:subscription_error`, `pubb:member_added`
and `pubb:member_removed` for lifecycle events. Application event names such as
`chat.message`, `turn.changed`, `lobby.updated` and `score.updated` are yours to define.
Each callback gets a Newtonsoft `JToken`; inspect fields directly to avoid
reflection-based DTO deserialization surprises with IL2CPP stripping.

All SDK access belongs on Unity's main thread. Native socket I/O runs asynchronously;
`Tick` delivers callbacks on its calling thread. Do not call it from a worker.
Use one owning component per session and dispose before replacing it or switching users.

Options: `AppKey` (required), `SocketUrl` (default `wss://ws.pubb.io/socket`, no query),
`Authorizer`, `TimeoutSeconds` (default 15; 1–120), `MaxReconnectAttempts` (default 5;
0 disables automatic retries). Options are captured at construction. Headers remain
provided dynamically by the authorizer. Custom `IChannelAuthorizer` and
`IPubbTransport` implementations can be injected for application integration/testing.

## Lifecycle and recovery

- The client requests `protocol=2`, waits for the connection handshake, then
  subscribes. Both the native and legacy subscription acknowledgements are accepted.
- Subscriptions/unsubscriptions are paced at most 10 per second without catch-up
  bursts. Acknowledgements and authorization have bounded timeouts.
- Unexpected disconnects retry with 2, 4, 8, 16, 30 second base delays plus 0–25%
  jitter. The retry budget resets after 30 seconds of healthy connection, preventing
  endless rapid handshake/disconnect loops. Call Connect to retry after Failed.
- Each reconnect obtains new private/presence signatures using the new socket ID.
  Intentional Disconnect/Dispose cancels retries and pending authorization.
- Pause: Disconnect. Resume: Connect. Refresh saved state after every subscription
  acknowledgement. Backgrounded apps and suspended browser tabs are not an always-on
  delivery surface. Use a separate mobile push service for closed-app notifications.
- Native and browser transports handle WebSocket Ping/Pong; there is no JSON ping.
  Receive queues are capped at 256 packets and packets at approximately 1 MiB.
  Overflow closes the connection and triggers recovery instead of growing memory.
- pubb provides no missed-event replay or delivery acknowledgement. Store state and
  history in your backend. Use versions/IDs to reject duplicates or stale updates.
  Periodic HTTP refresh can recover an event missed without a detected disconnect.

## Game state and publishing

Players submit commands to your authenticated backend: send a message, mark ready,
play a card, place a tile, make a move. Validate membership, turn order, allowed moves
and rate limits; save the resulting state before publishing. Clients never submit
trusted scores or outcomes. Use an application transactional outbox for durable
publication retries. HTTP 202 means accepted for processing, not delivered.

Use a shared private/presence room channel for public board state and separate
`private-player-{id}-match-{id}` channels for hands, hidden tiles and other secrets.
Never put every player's private hand in a shared snapshot. Presence means online
membership, not readiness, seat ownership, matchmaking or durable lobby state.

There is deliberately no client `Publish` method: the protocol accepts subscription
commands only, and HTTP publishing requires the server secret. JSON event data is
limited to 10 KiB. Refresh large state through your backend instead of publishing it.
This SDK is event messaging, not transform replication, physics synchronization,
rollback, competitive FPS netcode or an authoritative game server.

## Platforms and verification

Implementation targets Editor/desktop, Android/iOS via ClientWebSocket and WebGL
via a bundled `.jslib` bridge. WebGL HTTP calls need CORS for the exact game origin
and Authorization/Content-Type headers. Serve WebGL and remote endpoints over HTTPS;
a remote browser or phone cannot reach your computer through its own localhost.
Consoles are not evaluated. Device internet permission and TLS trust must be configured
in the consuming Unity project. Reconnection behavior on mobile suspension must be
verified on devices.

The development environment has no Unity Editor installed. Core behavior is tested
with a .NET 10 harness and a real loopback WebSocket; the WebGL bridge is tested with
a JavaScript harness, and the backend with HTTP integration tests. Those tests do not
establish Unity Editor compilation, IL2CPP builds or Android/iOS/WebGL device support.
Run the included EditMode tests and the platform checklist before a production release.

```sh
# From the source checkout (requires .NET 10 SDK and Node.js 22+):
dotnet run --project Tests~/Pubb.Tests.csproj
node --test Tests~/*.test.mjs
python3 tools~/pack.py
```

The harness uses the Newtonsoft assembly bundled with the .NET SDK, while Unity
resolves its supported Newtonsoft package. It does not require a test NuGet restore.
Full protocol: https://pubb.io/docs/websocket-protocol
