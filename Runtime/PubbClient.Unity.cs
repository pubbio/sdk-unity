namespace Pubb.Unity
{
    public sealed partial class PubbClient
    {
        public PubbClient(PubbOptions options) : this(options, CreateTransport) { }
        private static IPubbTransport CreateTransport()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new WebGLWebSocketTransport();
#else
            return new NativeWebSocketTransport();
#endif
        }
    }
}
