namespace HyperTizen.Capture
{
    /// <summary>
    /// Tizen 9 display capture probe. Native invocation is intentionally disabled
    /// until the request structure, metadata, and complete native ABI are verified.
    /// </summary>
    public class T9DisplayCaptureMethod : ICaptureMethod
    {
        private const string DisabledReason =
            "T9 Display capture is disabled: native request ABI and required metadata are unverified";

        public string Name => "T9 Display Capture (libdisplay-capture-api.so)";
        public CaptureMethodType Type => CaptureMethodType.T9DisplayCapture;

        public bool IsAvailable()
        {
            Helper.Log.Write(Helper.eLogType.Warning, $"[T9DisplayCaptureMethod] {DisabledReason}");
            return false;
        }

        public bool Test()
        {
            Helper.Log.Write(Helper.eLogType.Warning, $"[T9DisplayCaptureMethod] {DisabledReason}");
            return false;
        }

        public CaptureResult Capture(int width, int height)
        {
            return CaptureResult.CreateFailure(DisabledReason);
        }

        public void Cleanup()
        {
        }
    }
}
