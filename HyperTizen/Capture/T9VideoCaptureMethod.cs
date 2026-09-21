using System;
using System.Runtime.InteropServices;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Tizen 9 video capture adapter.
    ///
    /// Samsung has shipped several ABI-compatible names and soname variants for
    /// this API.  The library and symbols are therefore resolved at runtime; a
    /// missing or incomplete implementation must never be allowed to crash the
    /// service during startup.
    /// </summary>
    public sealed class T9VideoCaptureMethod : ICaptureMethod
    {
        private const int MaxCaptureWidth = 3840;
        private const int MaxCaptureHeight = 2160;
        private const int MaxPlaneBytes = 64 * 1024 * 1024;

        private readonly object _captureLock = new object();
        private IntPtr _libraryHandle;
        private string _libraryPath;
        private bool _isInitialized;
        private string _workingEntryPoint;
        private bool _requiresGlobalLock;

        private CaptureDelegate _capture;
        private LockDelegate _lockGlobal;
        private LockDelegate _unlockGlobal;

        private IntPtr _yBuffer;
        private IntPtr _uvBuffer;
        private int _yCapacity;
        private int _uvCapacity;
        private byte[] _managedYData;
        private byte[] _managedUVData;

        public string Name => "T9 Video Capture (libvideo-capture)";
        public CaptureMethodType Type => CaptureMethodType.T9VideoCapture;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CaptureDelegate(ref InputParams input, ref OutputParams output);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int LockDelegate();

        [StructLayout(LayoutKind.Sequential)]
        public struct InputParams
        {
            public int field0;
            public int field1;
            public int cropX;
            public int cropY;
            public int field4;
            public int yBufferSize;
            public int uvBufferSize;
            public IntPtr pYBuffer;
            public IntPtr pUVBuffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OutputParams
        {
            public int width;
            public int height;
            public int field2;
            public int field3;
            public int ySize;
            public int uvSize;
            public IntPtr pYData;
            public IntPtr pUVData;
        }

        public bool IsAvailable()
        {
            try
            {
                bool available = TryLoadNativeApi();
                Helper.Log.Write(available ? Helper.eLogType.Info : Helper.eLogType.Warning,
                    available
                        ? $"[T9VideoCaptureMethod] API available via {_libraryPath}, entry point {_workingEntryPoint}"
                        : "[T9VideoCaptureMethod] No supported Tizen 9 video capture API found");
                return available;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Warning,
                    $"[T9VideoCaptureMethod] Availability probe failed: {ex.Message}");
                Cleanup();
                return false;
            }
        }

        public bool Test()
        {
            Helper.Log.Write(Helper.eLogType.Info,
                "[T9VideoCaptureMethod] Testing 1920x1080 native capture");

            try
            {
                if (!TryLoadNativeApi() || !EnsureBuffers(1920, 1080))
                {
                    return false;
                }

                CaptureResult result = CaptureInternal(1920, 1080);
                if (result.Success)
                {
                    _isInitialized = true;
                    Helper.Log.Write(Helper.eLogType.Info,
                        $"[T9VideoCaptureMethod] Test passed: {result.Width}x{result.Height}, " +
                        $"stride {result.StrideY}/{result.StrideUV}, entry point {_workingEntryPoint}");
                    return true;
                }

                Helper.Log.Write(Helper.eLogType.Warning,
                    $"[T9VideoCaptureMethod] Test failed ({result.NativeErrorCode}): {result.ErrorMessage}");
                return false;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"[T9VideoCaptureMethod] Test exception: {ex.Message}");
                return false;
            }
        }

        public CaptureResult Capture(int width, int height)
        {
            if (!_isInitialized || _capture == null)
            {
                return CaptureResult.CreateFailure("Tizen 9 video capture is not initialized", 0, Name);
            }

            try
            {
                return CaptureInternal(width, height);
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"[T9VideoCaptureMethod] Capture exception: {ex.Message}");
                return CaptureResult.CreateFailure($"Native capture exception: {ex.Message}", -99, Name);
            }
        }

        private CaptureResult CaptureInternal(int width, int height)
        {
            lock (_captureLock)
            {
                if (!EnsureBuffers(width, height))
                {
                    return CaptureResult.CreateFailure("Invalid or oversized capture dimensions", 0, Name);
                }

                InputParams input = new InputParams
                {
                    field0 = 0,
                    field1 = 0,
                    cropX = 0xffff,
                    cropY = 0xffff,
                    field4 = 1,
                    yBufferSize = _yCapacity,
                    uvBufferSize = _uvCapacity,
                    pYBuffer = _yBuffer,
                    pUVBuffer = _uvBuffer
                };
                OutputParams output = new OutputParams();

                int nativeResult = -99;
                bool lockAttempted = false;

                try
                {
                    if (_requiresGlobalLock && _lockGlobal != null)
                    {
                        lockAttempted = true;
                        int lockResult = _lockGlobal();
                        if (lockResult != 0)
                        {
                            return CaptureResult.CreateFailure(
                                $"Native capture lock failed with code {lockResult}", lockResult, Name);
                        }
                    }

                    nativeResult = _capture(ref input, ref output);
                }
                finally
                {
                    if (lockAttempted && _unlockGlobal != null)
                    {
                        try
                        {
                            _unlockGlobal();
                        }
                        catch (Exception ex)
                        {
                            Helper.Log.Write(Helper.eLogType.Warning,
                                $"[T9VideoCaptureMethod] Native unlock failed: {ex.Message}");
                        }
                    }
                }

                if (nativeResult != 0 && nativeResult != 4)
                {
                    return CreateNativeFailure(nativeResult);
                }

                int actualWidth = output.width > 0 ? output.width : width;
                int actualHeight = output.height > 0 ? output.height : height;
                if (actualWidth <= 0 || actualHeight <= 0 ||
                    actualWidth > MaxCaptureWidth || actualHeight > MaxCaptureHeight)
                {
                    return CaptureResult.CreateFailure(
                        $"Native capture returned invalid dimensions {actualWidth}x{actualHeight}",
                        nativeResult, Name);
                }

                int ySize = output.ySize > 0 ? output.ySize : actualWidth * actualHeight;
                int uvSize = output.uvSize > 0 ? output.uvSize : actualWidth * (actualHeight / 2);
                if (!IsValidPlaneSize(ySize, _yCapacity) || !IsValidPlaneSize(uvSize, _uvCapacity))
                {
                    return CaptureResult.CreateFailure(
                        $"Native capture returned invalid plane sizes {ySize}/{uvSize}",
                        nativeResult, Name);
                }

                int strideY = Math.Max(actualWidth, ySize / actualHeight);
                int uvRows = Math.Max(1, actualHeight / 2);
                int strideUV = Math.Max(actualWidth, uvSize / uvRows);
                IntPtr ySource = output.pYData == IntPtr.Zero ? _yBuffer : output.pYData;
                IntPtr uvSource = output.pUVData == IntPtr.Zero ? _uvBuffer : output.pUVData;

                // Only copy pointers owned by the buffers supplied to Samsung's API.
                // An unverified pointer could turn a malformed native response into a
                // process crash, so it is rejected and the selector can fall back.
                if (!OwnsPointer(_yBuffer, _yCapacity, ySource, ySize) ||
                    !OwnsPointer(_uvBuffer, _uvCapacity, uvSource, uvSize))
                {
                    return CaptureResult.CreateFailure(
                        "Native capture returned an unowned buffer pointer", nativeResult, Name);
                }

                if (_managedYData == null || _managedYData.Length != ySize)
                {
                    _managedYData = new byte[ySize];
                }
                if (_managedUVData == null || _managedUVData.Length != uvSize)
                {
                    _managedUVData = new byte[uvSize];
                }
                Marshal.Copy(ySource, _managedYData, 0, ySize);
                Marshal.Copy(uvSource, _managedUVData, 0, uvSize);

                return CaptureResult.CreateSuccess(
                    _managedYData, _managedUVData, actualWidth, actualHeight, strideY, strideUV,
                    "NV12", nativeResult, Name);
            }
        }

        private CaptureResult CreateNativeFailure(int nativeResult)
        {
            if (nativeResult == -4)
            {
                return CaptureResult.CreateFailure(
                    "DRM protected content - capture is intentionally unavailable", nativeResult, Name);
            }

            if (nativeResult == -95)
            {
                return CaptureResult.CreateFailure(
                    "Operation not supported on this Tizen firmware; trying fallback", nativeResult, Name);
            }

            return CaptureResult.CreateFailure(
                $"Tizen 9 video capture failed with native code {nativeResult}", nativeResult, Name);
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
                "/usr/lib/libvideo-capture.so.0.1.0",
                "/usr/lib/libvideo-capture.so.0.1",
                "/usr/lib/libvideo-capture.so"
            };

            string[] captureSymbols =
            {
                "secvideo_api_capture_screen_video_only",
                "secvideo_api_capture_screen",
                "ppi_video_capture_get_video_main_yuv",
                "ppi_video_capture_get_screen_post_yuv"
            };

            foreach (string path in libraryPaths)
            {
                IntPtr handle = NativeLibraryProbe.Open(path);
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                CaptureDelegate capture = null;
                string symbolName = null;
                foreach (string symbol in captureSymbols)
                {
                    IntPtr address = NativeLibraryProbe.Symbol(handle, symbol);
                    if (address == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        capture = Marshal.GetDelegateForFunctionPointer<CaptureDelegate>(address);
                        symbolName = symbol;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning,
                            $"[T9VideoCaptureMethod] Cannot bind {symbol}: {ex.Message}");
                    }
                }

                if (capture == null)
                {
                    NativeLibraryProbe.Close(ref handle);
                    continue;
                }

                IntPtr lockAddress = NativeLibraryProbe.Symbol(handle, "ppi_video_capture_lock_global");
                IntPtr unlockAddress = NativeLibraryProbe.Symbol(handle, "ppi_video_capture_unlock_global");
                bool requiresLock = symbolName.StartsWith("ppi_", StringComparison.Ordinal);
                if (requiresLock && (lockAddress == IntPtr.Zero || unlockAddress == IntPtr.Zero))
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"[T9VideoCaptureMethod] {symbolName} found without lock/unlock ABI");
                    NativeLibraryProbe.Close(ref handle);
                    continue;
                }

                try
                {
                    _capture = capture;
                    _lockGlobal = lockAddress == IntPtr.Zero
                        ? null
                        : Marshal.GetDelegateForFunctionPointer<LockDelegate>(lockAddress);
                    _unlockGlobal = unlockAddress == IntPtr.Zero
                        ? null
                        : Marshal.GetDelegateForFunctionPointer<LockDelegate>(unlockAddress);
                    _libraryHandle = handle;
                    _libraryPath = path;
                    _workingEntryPoint = symbolName;
                    _requiresGlobalLock = requiresLock;
                    return true;
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"[T9VideoCaptureMethod] Cannot bind lock ABI: {ex.Message}");
                    NativeLibraryProbe.Close(ref handle);
                }
            }

            return false;
        }

        private bool EnsureBuffers(int width, int height)
        {
            if (width <= 0 || height <= 0 || width > MaxCaptureWidth || height > MaxCaptureHeight)
            {
                return false;
            }

            long yBytes = (long)width * height;
            long uvBytes = yBytes / 2;
            if (yBytes <= 0 || uvBytes <= 0 || yBytes > MaxPlaneBytes || uvBytes > MaxPlaneBytes)
            {
                return false;
            }

            if (_yBuffer != IntPtr.Zero && _uvBuffer != IntPtr.Zero &&
                _yCapacity >= yBytes && _uvCapacity >= uvBytes)
            {
                return true;
            }

            IntPtr newY = IntPtr.Zero;
            IntPtr newUv = IntPtr.Zero;
            try
            {
                newY = Marshal.AllocHGlobal((int)yBytes);
                newUv = Marshal.AllocHGlobal((int)uvBytes);

                if (_yBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_yBuffer);
                }
                if (_uvBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_uvBuffer);
                }

                _yBuffer = newY;
                _uvBuffer = newUv;
                _yCapacity = (int)yBytes;
                _uvCapacity = (int)uvBytes;
                return true;
            }
            catch
            {
                if (newY != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(newY);
                }
                if (newUv != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(newUv);
                }
                return false;
            }
        }

        private static bool IsValidPlaneSize(int size, int capacity)
        {
            return size > 0 && size <= capacity && size <= MaxPlaneBytes;
        }

        private static bool OwnsPointer(IntPtr buffer, int capacity, IntPtr pointer, int size)
        {
            if (buffer == IntPtr.Zero || pointer == IntPtr.Zero || size <= 0)
            {
                return false;
            }

            long start = buffer.ToInt64();
            long value = pointer.ToInt64();
            long end = value + size;
            return value >= start && end >= value && end <= start + capacity;
        }

        public void Cleanup()
        {
            lock (_captureLock)
            {
                _isInitialized = false;
                CleanupNativeApi();
                if (_yBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_yBuffer);
                    _yBuffer = IntPtr.Zero;
                }
                if (_uvBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_uvBuffer);
                    _uvBuffer = IntPtr.Zero;
                }
                _yCapacity = 0;
                _uvCapacity = 0;
                _managedYData = null;
                _managedUVData = null;
            }
        }

        private void CleanupNativeApi()
        {
            _capture = null;
            _lockGlobal = null;
            _unlockGlobal = null;
            _workingEntryPoint = null;
            _libraryPath = null;
            _requiresGlobalLock = false;
            if (_libraryHandle != IntPtr.Zero)
            {
                NativeLibraryProbe.Close(ref _libraryHandle);
            }
        }
    }
}
