using System;
using System.Runtime.InteropServices;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Tizen 9 display-capture fallback.
    /// The API is optional and differs between TV firmware builds, therefore all
    /// symbols are resolved through NativeLibraryProbe before they are called.
    /// </summary>
    public sealed class T9DisplayCaptureMethod : ICaptureMethod
    {
        private const int MaxCaptureWidth = 3840;
        private const int MaxCaptureHeight = 2160;
        private const int MaxBufferBytes = 64 * 1024 * 1024;

        private readonly object _captureLock = new object();
        private IntPtr _libraryHandle;
        private string _libraryPath;
        private CaptureDelegate _capture;
        private bool _isInitialized;
        private IntPtr _buffer;
        private int _bufferCapacity;
        private byte[] _managedYData;
        private byte[] _managedUVData;

        public string Name => "T9 Display Capture (libdisplay-capture-api)";
        public CaptureMethodType Type => CaptureMethodType.T9DisplayCapture;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CaptureDelegate(
            ref RequestData request, IntPtr yBuffer, IntPtr uvBuffer, int bufferSize);

        [StructLayout(LayoutKind.Sequential)]
        public struct RequestData
        {
            public int width;
            public int height;
            public int format;
            public int mode;
            public int reserved1;
            public int reserved2;
        }

        public bool IsAvailable()
        {
            try
            {
                bool available = TryLoadNativeApi();
                Helper.Log.Write(available ? Helper.eLogType.Info : Helper.eLogType.Warning,
                    available
                        ? $"[T9DisplayCaptureMethod] API available via {_libraryPath}"
                        : "[T9DisplayCaptureMethod] No display capture API found");
                return available;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Warning,
                    $"[T9DisplayCaptureMethod] Availability probe failed: {ex.Message}");
                Cleanup();
                return false;
            }
        }

        public bool Test()
        {
            Helper.Log.Write(Helper.eLogType.Info,
                "[T9DisplayCaptureMethod] Testing 1920x1080 native capture");
            try
            {
                if (!TryLoadNativeApi() || !EnsureBuffer(1920, 1080))
                {
                    return false;
                }

                CaptureResult result = CaptureInternal(1920, 1080);
                if (result.Success)
                {
                    _isInitialized = true;
                    Helper.Log.Write(Helper.eLogType.Info,
                        $"[T9DisplayCaptureMethod] Test passed: {result.Width}x{result.Height}");
                    return true;
                }

                Helper.Log.Write(Helper.eLogType.Warning,
                    $"[T9DisplayCaptureMethod] Test failed ({result.NativeErrorCode}): {result.ErrorMessage}");
                return false;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"[T9DisplayCaptureMethod] Test exception: {ex.Message}");
                return false;
            }
        }

        public CaptureResult Capture(int width, int height)
        {
            if (!_isInitialized || _capture == null)
            {
                return CaptureResult.CreateFailure("Tizen 9 display capture is not initialized", 0, Name);
            }

            try
            {
                return CaptureInternal(width, height);
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"[T9DisplayCaptureMethod] Capture exception: {ex.Message}");
                return CaptureResult.CreateFailure($"Native capture exception: {ex.Message}", -99, Name);
            }
        }

        private CaptureResult CaptureInternal(int width, int height)
        {
            lock (_captureLock)
            {
                if (!EnsureBuffer(width, height))
                {
                    return CaptureResult.CreateFailure("Invalid or oversized display capture dimensions", 0, Name);
                }

                int ySize = checked(width * height);
                int uvSize = checked(ySize / 2);
                RequestData request = new RequestData
                {
                    width = width,
                    height = height,
                    format = 0,
                    mode = 0,
                    reserved1 = 0,
                    reserved2 = 0
                };

                int nativeResult = _capture(
                    ref request,
                    _buffer,
                    IntPtr.Add(_buffer, ySize),
                    _bufferCapacity);

                if (nativeResult != 0 && nativeResult != 4)
                {
                    if (nativeResult == -4)
                    {
                        return CaptureResult.CreateFailure(
                            "DRM protected content - capture is intentionally unavailable",
                            nativeResult, Name);
                    }
                    if (nativeResult == -95)
                    {
                        return CaptureResult.CreateFailure(
                            "Operation not supported on this Tizen firmware; trying fallback",
                            nativeResult, Name);
                    }
                    return CaptureResult.CreateFailure(
                        $"Tizen 9 display capture failed with native code {nativeResult}",
                        nativeResult, Name);
                }

                if (_managedYData == null || _managedYData.Length != ySize)
                {
                    _managedYData = new byte[ySize];
                }
                if (_managedUVData == null || _managedUVData.Length != uvSize)
                {
                    _managedUVData = new byte[uvSize];
                }
                Marshal.Copy(_buffer, _managedYData, 0, ySize);
                Marshal.Copy(IntPtr.Add(_buffer, ySize), _managedUVData, 0, uvSize);
                return CaptureResult.CreateSuccess(
                    _managedYData, _managedUVData, width, height, width, width,
                    "NV12", nativeResult, Name);
            }
        }

        private bool TryLoadNativeApi()
        {
            if (_libraryHandle != IntPtr.Zero && _capture != null)
            {
                return true;
            }

            CleanupNativeApi();
            string[] libraryPaths =
            {
                "/usr/lib/libdisplay-capture-api.so.0.0",
                "/usr/lib/libdisplay-capture-api.so.0",
                "/usr/lib/libdisplay-capture-api.so"
            };

            foreach (string path in libraryPaths)
            {
                IntPtr handle = NativeLibraryProbe.Open(path);
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                IntPtr address = NativeLibraryProbe.Symbol(handle, "dc_request_capture_sync");
                if (address == IntPtr.Zero)
                {
                    NativeLibraryProbe.Close(ref handle);
                    continue;
                }

                try
                {
                    _capture = Marshal.GetDelegateForFunctionPointer<CaptureDelegate>(address);
                    _libraryHandle = handle;
                    _libraryPath = path;
                    return true;
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"[T9DisplayCaptureMethod] Cannot bind capture API: {ex.Message}");
                    NativeLibraryProbe.Close(ref handle);
                }
            }

            return false;
        }

        private bool EnsureBuffer(int width, int height)
        {
            if (width <= 0 || height <= 0 || width > MaxCaptureWidth || height > MaxCaptureHeight)
            {
                return false;
            }

            long required = (long)width * height * 3 / 2;
            if (required <= 0 || required > MaxBufferBytes)
            {
                return false;
            }
            if (_buffer != IntPtr.Zero && _bufferCapacity >= required)
            {
                return true;
            }

            IntPtr newBuffer = IntPtr.Zero;
            try
            {
                newBuffer = Marshal.AllocHGlobal((int)required);
                if (_buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_buffer);
                }
                _buffer = newBuffer;
                _bufferCapacity = (int)required;
                return true;
            }
            catch
            {
                if (newBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(newBuffer);
                }
                return false;
            }
        }

        public void Cleanup()
        {
            lock (_captureLock)
            {
                _isInitialized = false;
                CleanupNativeApi();
                if (_buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_buffer);
                    _buffer = IntPtr.Zero;
                }
                _bufferCapacity = 0;
                _managedYData = null;
                _managedUVData = null;
            }
        }

        private void CleanupNativeApi()
        {
            _capture = null;
            _libraryPath = null;
            if (_libraryHandle != IntPtr.Zero)
            {
                NativeLibraryProbe.Close(ref _libraryHandle);
            }
        }
    }
}
