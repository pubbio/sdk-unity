# Chat and lobby

1. Install the SDK, select it in Package Manager and import **Chat and lobby**.
2. Create an empty GameObject and attach `ChatAndLobby`.
3. Set its public app key. The default channel is `presence-lobby-demo`.
4. Start the sample backend below. Enter a player's demo token in the running UI.
5. Run a second Editor/player instance with the other token. Both should see chat,
   presence and ready states. The score display stays at zero until your trusted
   game logic implements scoring; there is no client-controlled score endpoint.

## Backend (Node.js 22+)

No npm install is needed. This backend is a learning sample, not a requirement to
use Node in a game. It uses pubb's HTTP authorization/publish APIs directly.

Set these variables in your shell or secret manager, replacing placeholders:

```sh
export PUBB_APP_ID='YOUR_APP_ID'
export PUBB_APP_SECRET='YOUR_SERVER_ONLY_SECRET'
export DEMO_PLAYERS_JSON='[{"id":"alice","name":"Alice","token":"REPLACE_WITH_RANDOM_TOKEN_AT_LEAST_24_CHARS"},{"id":"bob","name":"Bob","token":"REPLACE_WITH_A_DIFFERENT_RANDOM_TOKEN_24_CHARS"}]'
node backend/server.mjs
```

Generate independent tokens, for example with `openssl rand -hex 32`. Give each
demo player only their own token; never distribute the pubb secret. Tokens entered
in the sample UI are kept in memory, not serialized in scenes or PlayerPrefs.
Production should obtain expiring session tokens through your real sign-in flow
and call `ConfigureSession(token)` at runtime.

The server binds `127.0.0.1:3210` for local development. Native Editor/desktop uses
`http://localhost:3210`. For phones and hosted WebGL, deploy behind HTTPS and set
Backend Url to that reachable address; do not point remote devices at localhost.
For WebGL set `WEBGL_ORIGIN` to the exact origin hosting the game, e.g.
`https://game.example.com`. The backend rejects other browser origins and handles
Authorization/Content-Type preflight requests. Add the endpoint host to your
Content Security Policy if you use one.

Endpoints:

- `POST /auth`: authenticated JSON `{socket_id, channel_name}`; only the configured
  room channel is authorized, with identity derived from the session.
- `GET /rooms/demo/state`: authenticated versioned lobby and the latest 50 messages.
- `POST /rooms/demo/commands`: authenticated `{id, kind, value}`; `kind` is `chat`
  (nonblank text up to 500 characters) or `ready` (boolean). IDs are scoped per player;
  repeats are deduplicated, and changed payloads under the same ID return 409.

The Unity component reads state after subscription acknowledgements and live events,
and every ten seconds as a recovery path. It applies only newer/equal versions.
An uncertain chat command retains its ID and body for the Retry button. Switching
sessions or closing the sample clears local pending commands.

The backend has one room, a 2 KiB request limit, a per-player request rate limit,
10,000 retained command IDs and a maximum 1,000 pending publications. It saves state
before queuing publication, retries with the same publication key and returns saved
state even during a pubb outage. These are in-memory teaching implementations:
process restart loses rooms, history, command deduplication and queued publications.
Use a database, transactional outbox and expiring authenticated sessions for production.
Do not run multiple instances without shared state coordination.

## Verify before release

- Two players receive the same saved chat/ready update; member counts change correctly.
- An invalid token and an unauthorized channel are rejected.
- Disconnect/reconnect, pause/resume and scene unload do not duplicate callbacks.
- Reconnect rebuilds presence and refreshes saved state; retrying a command does not duplicate it.
- Test the actual Android/iOS/WebGL build, IL2CPP stripping, CORS and TLS setup.
- Test poor connectivity and verify that history/game state is recovered after missing events.
