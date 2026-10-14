using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pubb.Unity;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Attach to an empty GameObject. The sample backend contract is documented in README.md.</summary>
public sealed class ChatAndLobby : MonoBehaviour
{
    [SerializeField] private string appKey = "YOUR_PUBLIC_APP_KEY";
    [SerializeField] private string backendUrl = "http://localhost:3210";
    [SerializeField] private string channelName = "presence-lobby-demo";
    // Demo only: set through ConfigureSession at runtime in your application. Never ship a saved session token.
    private string sessionToken = "";
    private string message = "";
    private string status = "Enter your demo player token and connect.";
    private string pendingCommandId;
    private string pendingText;
    private JObject state;
    private Vector2 scroll;
    private PubbClient client;
    private PubbChannel channel;
    private bool sending, refreshing, refreshAgain, sessionStarted;
    private int sessionGeneration;
    private float nextRefresh;

    public void ConfigureSession(string token)
    {
        EndSession(); sessionToken = token; sessionStarted = true;
        client = new PubbClient(new PubbOptions {
            AppKey = appKey,
            Authorizer = new HttpChannelAuthorizer(backendUrl.TrimEnd('/') + "/auth",
                () => new Dictionary<string, string> { ["Authorization"] = "Bearer " + sessionToken })
        });
        client.Error += error => status = error;
        client.StateChanged += connection => status = connection.ToString();
        channel = client.Subscribe(channelName);
        channel.Bind("pubb:subscription_succeeded", _ => RefreshState());
        // Events invalidate the saved view; the HTTP version prevents stale reads from replacing new state.
        channel.Bind("chat.message", _ => RefreshState());
        channel.Bind("lobby.updated", _ => RefreshState());
        channel.Bind("score.updated", _ => RefreshState());
        client.Connect();
    }

    private void Update()
    {
        client?.Tick();
        if (sessionStarted && Time.unscaledTime >= nextRefresh)
        {
            nextRefresh = Time.unscaledTime + 10;
            RefreshState();
        }
    }
    private void RefreshState()
    {
        if (!isActiveAndEnabled) return;
        if (refreshing) { refreshAgain = true; return; }
        StartCoroutine(ReadState(sessionGeneration));
    }
    private IEnumerator ReadState(int generation)
    {
        refreshing = true;
        do
        {
            refreshAgain = false;
            using (var request = UnityWebRequest.Get(backendUrl.TrimEnd('/') + "/rooms/demo/state"))
            {
                request.SetRequestHeader("Authorization", "Bearer " + sessionToken);
                request.timeout = 15; request.redirectLimit = 0;
                yield return request.SendWebRequest();
                if (generation != sessionGeneration) yield break;
                if (request.result == UnityWebRequest.Result.Success)
                {
                    var latest = JObject.Parse(request.downloadHandler.text);
                    if (state == null || (long)latest["version"] >= (long)state["version"]) state = latest;
                }
                else status = "State refresh failed. Retrying periodically.";
            }
        } while (refreshAgain);
        refreshing = false;
    }
    private IEnumerator Command(string kind, JToken value)
    {
        sending = true;
        var generation = sessionGeneration;
        // Preserve an uncertain chat command's ID and body until the server confirms it.
        var id = kind == "chat" ? pendingCommandId : Guid.NewGuid().ToString("N");
        var payload = new JObject { ["id"] = id, ["kind"] = kind, ["value"] = value };
        using (var request = new UnityWebRequest(backendUrl.TrimEnd('/') + "/rooms/demo/commands", "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + sessionToken);
            request.timeout = 15; request.redirectLimit = 0;
            yield return request.SendWebRequest();
            if (generation != sessionGeneration) yield break;
            if (request.result == UnityWebRequest.Result.Success)
            {
                if (kind == "chat") { pendingCommandId = null; pendingText = null; message = ""; }
                status = "Saved."; RefreshState();
            }
            else status = "Command not confirmed. Retry to use the same chat command ID.";
        }
        sending = false;
    }
    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(20, 20, Mathf.Min(Screen.width - 40, 520), Screen.height - 40), GUI.skin.box);
        scroll = GUILayout.BeginScrollView(scroll);
        GUILayout.Label("pubb · Chat and lobby");
        GUILayout.Label(status);
        if (!sessionStarted)
        {
            GUILayout.Label("Demo session token (kept in memory)");
            sessionToken = GUILayout.PasswordField(sessionToken, '*');
            if (GUILayout.Button("Connect") && !string.IsNullOrWhiteSpace(sessionToken)) ConfigureSession(sessionToken);
        }
        else
        {
            GUILayout.Label("Online: " + (channel?.Members.Count ?? 0));
            if (state?["players"] is JArray players)
                foreach (var player in players) GUILayout.Label(player["name"] + " · " + ((bool)player["ready"] ? "Ready" : "Waiting") + " · Score " + player["score"]);
            if (state?["messages"] is JArray messages)
                foreach (var item in messages) GUILayout.Label(item["name"] + ": " + item["text"]);
            GUI.enabled = !sending;
            message = GUILayout.TextField(message, 500);
            if (GUILayout.Button(pendingCommandId == null ? "Send message" : "Retry pending message") && (pendingCommandId != null || !string.IsNullOrWhiteSpace(message)))
            {
                if (pendingCommandId == null) { pendingCommandId = Guid.NewGuid().ToString("N"); pendingText = message; }
                StartCoroutine(Command("chat", new JValue(pendingText)));
            }
            if (GUILayout.Button("Ready")) StartCoroutine(Command("ready", new JValue(true)));
            if (GUILayout.Button("Not ready")) StartCoroutine(Command("ready", new JValue(false)));
            GUI.enabled = true;
            if (GUILayout.Button("Disconnect")) EndSession();
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }
    private void OnApplicationPause(bool paused)
    {
        if (paused) client?.Disconnect();
        else if (sessionStarted) { client?.Connect(); RefreshState(); }
    }
    private void OnDisable() => EndSession();
    private void EndSession()
    {
        sessionGeneration++; StopAllCoroutines();
        client?.Dispose(); client = null; channel = null;
        sessionStarted = sending = refreshing = refreshAgain = false;
        state = null; pendingCommandId = pendingText = null;
    }
}
