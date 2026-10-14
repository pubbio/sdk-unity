using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace Pubb.Unity
{
    /// <summary>Posts socket_id/channel_name to YOUR authenticated backend, never directly to pubb's secret-bearing API.</summary>
    public sealed class HttpChannelAuthorizer : IChannelAuthorizer
    {
        private readonly string endpoint;
        private readonly Func<IDictionary<string, string>> headers;
        public HttpChannelAuthorizer(string endpoint, Func<IDictionary<string, string>> headers = null)
        {
            this.endpoint = PubbOptions.ValidateEndpoint(endpoint, false).AbsoluteUri;
            this.headers = headers;
        }

        public async Task<ChannelAuthorization> AuthorizeAsync(string socketId, string channelName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = new JObject { ["socket_id"] = socketId, ["channel_name"] = channelName };
            using (var request = new UnityWebRequest(endpoint, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Formatting.None)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = 15;
                request.redirectLimit = 0;
                if (headers != null)
                    foreach (var header in headers()) request.SetRequestHeader(header.Key, header.Value);
                request.SetRequestHeader("Content-Type", "application/json");
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested) { request.Abort(); cancellationToken.ThrowIfCancellationRequested(); }
                    await Task.Yield();
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
                    throw new InvalidOperationException("Authorization failed (HTTP " + request.responseCode + ").");
                if (request.downloadHandler.data.Length > 16 * 1024) throw new InvalidOperationException("Authorization response exceeds 16 KiB.");
                var data = JObject.Parse(request.downloadHandler.text);
                return new ChannelAuthorization { Auth = (string)data["auth"], ChannelData = (string)data["channel_data"] };
            }
        }
    }
}
