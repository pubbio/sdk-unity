using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Pubb.Unity
{
    public sealed class PubbChannel
    {
        private readonly Dictionary<string, List<Action<JToken>>> handlers = new Dictionary<string, List<Action<JToken>>>();
        private readonly Dictionary<string, JToken> members = new Dictionary<string, JToken>();
        private readonly Action<string> reportError;
        internal int Status; // 0 desired, 1 awaiting acknowledgement, 2 subscribed, 3 rejected
        internal double SentAt;
        public string Name { get; }
        public bool IsSubscribed => Status == 2;
        public bool IsPresence => Name.StartsWith("presence-", StringComparison.Ordinal);
        public IReadOnlyDictionary<string, JToken> Members => new Dictionary<string, JToken>(members);

        internal PubbChannel(string name, Action<string> reportError) { Name = name; this.reportError = reportError; }

        public PubbChannel Bind(string eventName, Action<JToken> callback)
        {
            if (string.IsNullOrWhiteSpace(eventName)) throw new ArgumentException("An event name is required.");
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (!handlers.TryGetValue(eventName, out var list)) handlers[eventName] = list = new List<Action<JToken>>();
            if (!list.Contains(callback)) list.Add(callback);
            return this;
        }

        public void Unbind(string eventName, Action<JToken> callback = null)
        {
            if (callback == null) handlers.Remove(eventName);
            else if (handlers.TryGetValue(eventName, out var list)) list.Remove(callback);
        }
        public void UnbindAll() => handlers.Clear();
        internal void Reset() { Status = 0; members.Clear(); }

        internal void Emit(string name, JToken data)
        {
            if (!handlers.TryGetValue(name, out var list)) return;
            var status = Status;
            foreach (var callback in list.ToArray())
            {
                if (Status != status) break;
                if (!handlers.TryGetValue(name, out var current) || !current.Contains(callback)) continue;
                try { callback(data); }
                catch (Exception) { reportError("An event handler threw an exception: " + name); }
            }
        }

        internal void Subscribed(JToken data)
        {
            if (IsPresence && data?["presence"] is JObject presence)
            {
                members.Clear();
                if (presence["ids"] is JArray ids && presence["hash"] is JObject hash)
                    foreach (var id in ids) members[(string)id] = hash[(string)id]?.DeepClone() ?? JValue.CreateNull();
            }
            Status = 2;
            Emit("pubb:subscription_succeeded", data);
        }

        internal void MemberEvent(string name, JToken data)
        {
            var id = (string)data?["user_id"];
            if (string.IsNullOrEmpty(id)) return;
            if (name == "pubb:member_added") members[id] = data["user_info"]?.DeepClone() ?? JValue.CreateNull();
            else members.Remove(id);
            Emit(name, data);
        }
    }
}
