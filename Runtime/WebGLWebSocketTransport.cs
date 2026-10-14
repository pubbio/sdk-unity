#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Pubb.Unity
{
    internal sealed class WebGLWebSocketTransport : IPubbTransport
    {
        [DllImport("__Internal")] private static extern int Pubb_Connect(string url);
        [DllImport("__Internal")] private static extern int Pubb_Send(int id, string message);
        [DllImport("__Internal")] private static extern IntPtr Pubb_Poll(int id, out int length);
        [DllImport("__Internal")] private static extern void Pubb_Free(IntPtr pointer);
        [DllImport("__Internal")] private static extern void Pubb_Close(int id);
        private int id;
        public void Connect(Uri uri) { id = Pubb_Connect(uri.AbsoluteUri); }
        public bool TryReceive(out TransportEvent message)
        {
            var pointer = Pubb_Poll(id, out var length);
            message = default;
            if (pointer == IntPtr.Zero) return false;
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(pointer, bytes, 0, length);
                var packet = JObject.Parse(Encoding.UTF8.GetString(bytes));
                message = new TransportEvent((TransportEventKind)(int)packet["kind"], (string)packet["text"]);
                return true;
            }
            finally { Pubb_Free(pointer); }
        }
        public Task SendAsync(string message)
        {
            if (Pubb_Send(id, message) != 0) throw new InvalidOperationException("WebSocket is unavailable or its send buffer is full.");
            return Task.CompletedTask;
        }
        public void Dispose() { if (id != 0) Pubb_Close(id); id = 0; }
    }
}
#endif
