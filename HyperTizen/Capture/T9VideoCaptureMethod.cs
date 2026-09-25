using System;
using System.Runtime.InteropServices;
using Tizen.System;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Tizen 9 video capture probe using the reference-backed IVideoCapture
    /// singleton/vtable path from libvideo-capture.so.0.1.0.
    /// </summary>
    public class T9VideoCaptureMethod : ICaptureMethod
    {
        private bool _isInitialized = false;
        private string _workingEntryPoint = null; // Which API variant works
        private const int RTLD_LAZY = 1;

        public string Name => "T9 Video Capture (libvideo-capture.so.0.1.0)";
        public CaptureMethodType Type => CaptureMethodType.T9VideoCapture;

        #region P/Invoke Declarations - Dynamic Library Loading

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlopen(string filename, int flags);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int dlclose(IntPtr handle);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlerror();

        #endregion

        #region Native IVideoCapture API

        private const string VideoCaptureLibrary = "/usr/lib/libvideo-capture.so.0.1.0";
        private const int MainYuvVtableOffset = 0x0c;
        private const int CaptureLockVtableOffset = 0x34;
        private const int CaptureUnlockVtableOffset = 0x38;
        private const int MinimumNativeBufferSize = 0x7e900;
        private const byte BufferSentinel = 0xa5;
        private const int NativeBufferFillChunkSize = 64 * 1024;
        private static readonly byte[] NativeBufferFillChunk = CreateNativeBufferFillChunk();

        [DllImport(VideoCaptureLibrary, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "_ZN13IVideoCapture11getInstanceEv")]
        private static extern IntPtr IVideoCapture_getInstance();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CaptureMethodDelegate(
            IntPtr instance,
            ref InputParams inputParams,
            ref OutputParams outputParams);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CaptureLockDelegate(IntPtr instance, int captureType, int flags);

        #endregion

        #region Native Structs

        /// <summary>
        /// Input parameters for IVideoCapture::getVideoMainYUV.
        /// The nine 32-bit fields match the Tizen capture reference structure.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct InputParams
        {
            public int field0;
            public int field1;
            public int field2;
            public int field3;
            public int cropX;           // Offset 0x10; 0xffff requests full screen
            public int cropY;           // Offset 0x14; 0xffff requests full screen
            public int field6;          // Offset 0x18; set to 1
            public int field7;
            public int field8;
        }

        /// <summary>
        /// Output parameters for IVideoCapture. The native structure is 80 bytes;
        /// output buffers begin at offsets 0x30 and 0x34.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct OutputParams
        {
            public int width;           // Captured width
            public int height;          // Captured height
            public int field2;
            public int field3;
            public int field4;
            public int field5;
            public int field6;
            public int field7;
            public int field8;
            public int field9;
            public int ySize;           // Offset 0x28; buffer capacity
            public int uvSize;          // Offset 0x2c; buffer capacity
            public IntPtr pYData;       // Pointer to Y data
            public IntPtr pUVData;      // Pointer to UV data
            public int field14;
            public int field15;
            public int field16;
            public int field17;
            public int field18;
            public int field19;
        }

        #endregion

        #region ICaptureMethod Implementation

        public bool IsAvailable()
        {
            Helper.Log.Write(Helper.eLogType.Info, "[T9VideoCaptureMethod] Checking availability...");

            // Test library loading using dlopen
            string[] libraryPaths = new string[]
            {
                "/usr/lib/libvideo-capture.so.0.1.0",
                "/usr/lib/libvideo-capture.so.0.1",
                "/usr/lib/libvideo-capture.so"
            };

            foreach (var libPath in libraryPaths)
            {
                IntPtr handle = dlopen(libPath, RTLD_LAZY);
                if (handle != IntPtr.Zero)
                {
                    Helper.Log.Write(Helper.eLogType.Info, $"[T9VideoCaptureMethod] Library loaded: {libPath}");

                    // Only use the C++ singleton/vtable ABI reconstructed from
                    // Samsung's reference implementation. The secvideo_api_* and
                    // ppi_video_capture_* wrappers have different signatures.
                    string[] entryPoints = new string[]
                    {
                        "_ZN13IVideoCapture11getInstanceEv"
                    };

                    bool foundEntryPoint = false;
                    foreach (var entryPoint in entryPoints)
                    {
                        IntPtr symbol = dlsym(handle, entryPoint);
                        if (symbol != IntPtr.Zero)
                        {
                            Helper.Log.Write(Helper.eLogType.Info, $"[T9VideoCaptureMethod] ✓ Found entry point: {entryPoint}");
                            foundEntryPoint = true;
                        }
                    }

                    dlclose(handle);

                    if (foundEntryPoint)
                    {
                        Helper.Log.Write(Helper.eLogType.Info, "[T9VideoCaptureMethod] Available!");
                        return true;
                    }
                }
                else
                {
                    // Get detailed error information
                    IntPtr errorPtr = dlerror();
                    string dlError = errorPtr != IntPtr.Zero
                        ? Marshal.PtrToStringAnsi(errorPtr)
                        : null;

                    // Check if file exists
                    bool fileExists = System.IO.File.Exists(libPath);

                    // Build detailed error message
                    string errorMsg = $"[T9VideoCaptureMethod] dlopen() failed for {libPath}";
                    if (!fileExists)
                    {
                        errorMsg += " - File does not exist";
                    }
                    else if (dlError != null)
                    {
                        errorMsg += $" - {dlError}";
                    }
                    else
                    {
                        errorMsg += " - dlopen returned NULL but dlerror() also returned NULL (possible permission issue or invalid ELF format)";
                    }

                    Helper.Log.Write(Helper.eLogType.Warning, errorMsg);
                }
            }

            Helper.Log.Write(Helper.eLogType.Warning, "[T9VideoCaptureMethod] Not available - no library or entry points found");
            return false;
        }

        public bool Test()
        {
            _isInitialized = false;
            _workingEntryPoint = null;
            Helper.Log.Write(Helper.eLogType.Info, "[T9VideoCaptureMethod] Running capture test...");

            try
            {
                bool success = TestEntryPoint("IVideoCapture::getVideoMainYUV", MainYuvVtableOffset);

                if (success)
                {
                    _isInitialized = true;
                    Helper.Log.Write(Helper.eLogType.Info, $"[T9VideoCaptureMethod] ✓ Test PASSED using: {_workingEntryPoint}");
                    return true;
                }
                else
                {
                    Helper.Log.Write(Helper.eLogType.Error,
                        "[T9VideoCaptureMethod] ✗ Test FAILED - reference-matched getVideoMainYUV call failed validation");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"[T9VideoCaptureMethod] Test exception: {ex.Message}");
                return false;
            }
        }

        private bool TestEntryPoint(string entryPointName, int vtableOffset)
        {
            Helper.Log.Write(Helper.eLogType.Info, $"[T9VideoCaptureMethod] Testing reference-backed method: {entryPointName}");

            try
            {
                int testWidth = 1920;
                int testHeight = 1080;
                int bufferCapacity = GetNativeBufferCapacity(testWidth, testHeight);

                IntPtr yBuffer = IntPtr.Zero;
                IntPtr uvBuffer = IntPtr.Zero;

                try
                {
                    yBuffer = Marshal.AllocHGlobal(bufferCapacity);
                    uvBuffer = Marshal.AllocHGlobal(bufferCapacity);
                    FillNativeBuffer(yBuffer, bufferCapacity);
                    FillNativeBuffer(uvBuffer, bufferCapacity);

                    InputParams input = CreateInputParams();
                    OutputParams output = CreateOutputParams(yBuffer, uvBuffer, bufferCapacity);
                    int result = CallVideoCapture(entryPointName, vtableOffset, ref input, ref output);

                    Helper.Log.Write(Helper.eLogType.Info, $"[T9VideoCaptureMethod] {entryPointName} returned: {result}");

                    if (result == 0 || result == 4)
                    {
                        if (TryGetFrameByteCounts(output, bufferCapacity, out int yByteCount, out int uvByteCount) &&
                            output.pYData == yBuffer && output.pUVData == uvBuffer)
                        {
                            byte[] yData = new byte[yByteCount];
                            byte[] uvData = new byte[uvByteCount];
                            Marshal.Copy(yBuffer, yData, 0, yByteCount);
                            Marshal.Copy(uvBuffer, uvData, 0, uvByteCount);

                            if (BufferChangedFromSentinel(yData, BufferSentinel) &&
                                BufferChangedFromSentinel(uvData, BufferSentinel))
                            {
                                Helper.Log.Write(Helper.eLogType.Info,
                                    $"[T9VideoCaptureMethod] ✓ SUCCESS: {output.width}x{output.height}, " +
                                    $"Y bytes: {yByteCount}, UV bytes: {uvByteCount}");
                                _workingEntryPoint = entryPointName;
                                return true;
                            }
                        }
                    }

                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"[T9VideoCaptureMethod] {entryPointName} failed validation: result={result}, " +
                        $"size={output.width}x{output.height}, buffers={output.ySize}/{output.uvSize}, " +
                        $"pointersMatch={output.pYData == yBuffer && output.pUVData == uvBuffer}");
                    return false;
                }
                finally
                {
                    if (yBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(yBuffer);
                    }
                    if (uvBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(uvBuffer);
                    }
                }
            }
            catch (DllNotFoundException)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"[T9VideoCaptureMethod] {entryPointName} - Library not found");
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"[T9VideoCaptureMethod] {entryPointName} - Entry point not found");
                return false;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"[T9VideoCaptureMethod] {entryPointName} exception: {ex.Message}");
                return false;
            }
        }

        private static InputParams CreateInputParams()
        {
            return new InputParams
            {
                cropX = 0xffff,
                cropY = 0xffff,
                field6 = 1
            };
        }

        private static OutputParams CreateOutputParams(IntPtr yBuffer, IntPtr uvBuffer, int bufferCapacity)
        {
            return new OutputParams
            {
                ySize = bufferCapacity,
                uvSize = bufferCapacity,
                pYData = yBuffer,
                pUVData = uvBuffer
            };
        }

        private static int GetNativeBufferCapacity(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException("width", "Capture dimensions must be positive");
            }

            long requestedSize = (long)width * height;
            if (requestedSize > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException("width", "Capture buffer is too large");
            }

            return Math.Max((int)requestedSize, MinimumNativeBufferSize);
        }

        private static int CallVideoCapture(
            string entryPointName,
            int vtableOffset,
            ref InputParams input,
            ref OutputParams output)
        {
            IntPtr instance = IVideoCapture_getInstance();
            if (instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("IVideoCapture::getInstance returned null");
            }

            IntPtr vtable = Marshal.ReadIntPtr(instance);
            if (vtable == IntPtr.Zero)
            {
                throw new InvalidOperationException("IVideoCapture instance has no vtable");
            }

            IntPtr captureAddress = Marshal.ReadIntPtr(vtable, vtableOffset);
            IntPtr lockAddress = Marshal.ReadIntPtr(vtable, CaptureLockVtableOffset);
            IntPtr unlockAddress = Marshal.ReadIntPtr(vtable, CaptureUnlockVtableOffset);
            if (captureAddress == IntPtr.Zero || lockAddress == IntPtr.Zero || unlockAddress == IntPtr.Zero)
            {
                throw new InvalidOperationException("IVideoCapture vtable is missing a required method");
            }

            CaptureMethodDelegate capture =
                Marshal.GetDelegateForFunctionPointer<CaptureMethodDelegate>(captureAddress);
            CaptureLockDelegate captureLock =
                Marshal.GetDelegateForFunctionPointer<CaptureLockDelegate>(lockAddress);
            CaptureLockDelegate captureUnlock =
                Marshal.GetDelegateForFunctionPointer<CaptureLockDelegate>(unlockAddress);

            Helper.Log.Write(Helper.eLogType.Debug,
                $"[T9VideoCaptureMethod] Calling {entryPointName} through vtable offset 0x{vtableOffset:x}");

            captureLock(instance, 1, 0);
            try
            {
                return capture(instance, ref input, ref output);
            }
            finally
            {
                captureUnlock(instance, 1, 0);
            }
        }

        private static bool TryGetFrameByteCounts(
            OutputParams output,
            int bufferCapacity,
            out int yByteCount,
            out int uvByteCount)
        {
            yByteCount = 0;
            uvByteCount = 0;
            if (output.width <= 0 || output.height <= 0 ||
                output.ySize <= 0 || output.ySize > bufferCapacity ||
                output.uvSize <= 0 || output.uvSize > bufferCapacity)
            {
                return false;
            }

            long ySize = (long)output.width * output.height;
            long uvSize = ySize / 2;
            if (ySize <= 0 || ySize > bufferCapacity || ySize > output.ySize ||
                uvSize <= 0 || uvSize > bufferCapacity || uvSize > output.uvSize)
            {
                return false;
            }

            yByteCount = (int)ySize;
            uvByteCount = (int)uvSize;
            return true;
        }

        private static void FillNativeBuffer(IntPtr buffer, int size)
        {
            int offset = 0;
            while (offset < size)
            {
                int copySize = Math.Min(NativeBufferFillChunk.Length, size - offset);
                Marshal.Copy(NativeBufferFillChunk, 0, IntPtr.Add(buffer, offset), copySize);
                offset += copySize;
            }
        }

        private static byte[] CreateNativeBufferFillChunk()
        {
            byte[] data = new byte[NativeBufferFillChunkSize];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = BufferSentinel;
            }
            return data;
        }

        private static bool BufferChangedFromSentinel(byte[] buffer, byte sentinel)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != sentinel)
                {
                    return true;
                }
            }
            return false;
        }

        public CaptureResult Capture(int width, int height)
        {
            if (!_isInitialized || string.IsNullOrEmpty(_workingEntryPoint))
            {
                return CaptureResult.CreateFailure("Not initialized - call Test() first");
            }

            try
            {
                int bufferCapacity = GetNativeBufferCapacity(width, height);

                IntPtr yBuffer = IntPtr.Zero;
                IntPtr uvBuffer = IntPtr.Zero;

                try
                {
                    yBuffer = Marshal.AllocHGlobal(bufferCapacity);
                    uvBuffer = Marshal.AllocHGlobal(bufferCapacity);
                    FillNativeBuffer(yBuffer, bufferCapacity);
                    FillNativeBuffer(uvBuffer, bufferCapacity);

                    InputParams input = CreateInputParams();
                    OutputParams output = CreateOutputParams(yBuffer, uvBuffer, bufferCapacity);
                    int result = CallVideoCapture(_workingEntryPoint, MainYuvVtableOffset, ref input, ref output);

                    if ((result == 0 || result == 4) &&
                        TryGetFrameByteCounts(output, bufferCapacity, out int yByteCount, out int uvByteCount) &&
                        output.pYData == yBuffer && output.pUVData == uvBuffer)
                    {
                        byte[] yData = new byte[yByteCount];
                        byte[] uvData = new byte[uvByteCount];

                        Marshal.Copy(yBuffer, yData, 0, yByteCount);
                        Marshal.Copy(uvBuffer, uvData, 0, uvByteCount);

                        if (!BufferChangedFromSentinel(yData, BufferSentinel) ||
                            !BufferChangedFromSentinel(uvData, BufferSentinel))
                        {
                            return CaptureResult.CreateFailure(
                                "Capture returned success but one or both output planes were unchanged");
                        }

                        return CaptureResult.CreateSuccess(yData, uvData, output.width, output.height);
                    }
                    else
                    {
                        Helper.Log.Write(Helper.eLogType.Warning,
                            $"[T9VideoCaptureMethod] Capture validation failed: result={result}, " +
                            $"size={output.width}x{output.height}, buffers={output.ySize}/{output.uvSize}, " +
                            $"pointersMatch={output.pYData == yBuffer && output.pUVData == uvBuffer}");
                        return CaptureResult.CreateFailure($"Capture failed validation (native result {result})");
                    }
                }
                finally
                {
                    if (yBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(yBuffer);
                    }
                    if (uvBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(uvBuffer);
                    }
                }
            }
            catch (Exception ex)
            {
                return CaptureResult.CreateFailure($"Exception: {ex.Message}");
            }
        }

        public void Cleanup()
        {
            Helper.Log.Write(Helper.eLogType.Info, "[T9VideoCaptureMethod] Cleanup");
            _isInitialized = false;
            _workingEntryPoint = null;
        }

        #endregion
    }
}
