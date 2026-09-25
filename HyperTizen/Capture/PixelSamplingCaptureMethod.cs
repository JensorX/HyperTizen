using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Tizen.System;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Pixel sampling capture method using libvideoenhance.so
    /// Samples individual pixels from screen edges for ambient lighting
    /// Adapted from original HyperTizen Capturer.cs to use NV12/FlatBuffers format
    /// </summary>
    public class PixelSamplingCaptureMethod : ICaptureMethod
    {
        private bool _isInitialized = false;
        private Condition _condition;

        // Track which API variant and library path works
        private string _workingVariant = null; // T6, T7, T9A, T9B, T9C, T9 (ppi_ve_*)
        private string _workingLibPath = null; // SO, SO0 (only .so and .so.0 exist on Tizen 9)

        // Pre-calculated pixel coordinates (calculated once during initialization)
        private struct PixelCoordinate
        {
            public int X;
            public int Y;
        }
        private PixelCoordinate[] _pixelCoordinates = null;

        private const int MaximumStaleSampleMs = 250;
        private const int EdgeAnchorCount = 4;
        private const int AbruptChangeThreshold = 256;
        private const int PendingSampleMatchThreshold = 128;
        private const int MaximumPendingSamples = 3;
        private const int SamplingErrorLogIntervalMs = 5000;

        // Output order is top, right, bottom, left. One anchor per edge is used
        // because the S90C reports only two hardware measurement slots.
        private readonly CapturePoint[] _capturedPoints = new CapturePoint[] {
            new CapturePoint(0.50, 0.05), // Top center
            new CapturePoint(0.95, 0.50), // Right center
            new CapturePoint(0.50, 0.95), // Bottom center
            new CapturePoint(0.05, 0.50)  // Left center
        };

        // Sample opposing edges together when there are two slots: right/left,
        // then top/bottom. This preserves the most important spatial contrast.
        private static readonly int[] SamplingOrder = new int[] { 1, 3, 0, 2 };

        private Color[] _lastFilteredColors = new Color[EdgeAnchorCount];
        private bool[] _hasFilteredColors = new bool[EdgeAnchorCount];
        private Color[] _pendingColors = new Color[EdgeAnchorCount];
        private bool[] _hasPendingColors = new bool[EdgeAnchorCount];
        private int[] _pendingSampleCounts = new int[EdgeAnchorCount];
        private long[] _lastSuccessfulSampleTimestamps = new long[EdgeAnchorCount];
        private int[] _anchorErrorCounts = new int[EdgeAnchorCount];

        private long _lastSamplingErrorLogTimestamp;
        private long _lastSampleSummaryTimestamp;
        private long _lastUnavailableSampleSummaryTimestamp;
        private int _positionErrorsSinceLog;
        private int _pixelErrorsSinceLog;
        private int _invalidSamplesSinceLog;
        private int _abruptCandidatesSinceSummary;

        public string Name => "Pixel Sampling";
        public CaptureMethodType Type => CaptureMethodType.PixelSampling;

        #region P/Invoke Declarations

        // ===== Tizen 6 API (cs_ve_* prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T6_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T6_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T6_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T6_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T6_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T6_SO0(int index, out Color color);

        // ===== Tizen 7 API (ve_* prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T7_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T7_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T7_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T7_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T7_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T7_SO0(int index, out Color color);

        // ===== Tizen 9+ API variant A (tizen_ve_* prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9A_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9A_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9A_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9A_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9A_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9A_SO0(int index, out Color color);

        // ===== Tizen 9+ API variant B (samsung_ve_* prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9B_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9B_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9B_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9B_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9B_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9B_SO0(int index, out Color color);

        // ===== Tizen 9+ API variant C (no prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9C_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "set_rgb_measure_position")]
        private static extern int MeasurePosition_T9C_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9C_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9C_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "set_rgb_measure_position")]
        private static extern int MeasurePosition_T9C_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9C_SO0(int index, out Color color);

        // ===== Tizen 9 API (ppi_ve_* prefix) - CONFIRMED via analysis =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9_SO0(int index, out Color color);

        #endregion

        #region Native Structs

        /// <summary>
        /// Color struct for 10-bit RGB values (0-1023)
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Color
        {
            public int R;
            public int G;
            public int B;
        }

        /// <summary>
        /// Condition struct containing screen parameters and sampling configuration
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Condition
        {
            public int ScreenCapturePoints;  // Max number of points that can be sampled simultaneously
            public int PixelDensityX;         // Pixel density in X direction
            public int PixelDensityY;         // Pixel density in Y direction
            public int SleepMS;               // Milliseconds to sleep between position set and pixel read
            public int Width;                 // Screen width in pixels
            public int Height;                // Screen height in pixels
        }

        /// <summary>
        /// Capture point with normalized coordinates (0.0-1.0)
        /// </summary>
        public struct CapturePoint
        {
            public CapturePoint(double x, double y)
            {
                this.X = x;
                this.Y = y;
            }

            public double X;
            public double Y;
        }

        #endregion

        /// <summary>
        /// Check if pixel sampling library is available
        /// </summary>
        public bool IsAvailable()
        {
            Helper.Log.Write(Helper.eLogType.Debug, "PixelSampling: Checking availability...");

            // Check if library file exists
            if (!System.IO.File.Exists("/usr/lib/libvideoenhance.so"))
            {
                Helper.Log.Write(Helper.eLogType.Debug, "PixelSampling: libvideoenhance.so not found");
                return false;
            }

            Helper.Log.Write(Helper.eLogType.Debug, "PixelSampling: Library found, available");
            return true;
        }

        /// <summary>
        /// Test pixel sampling by attempting to get screen condition
        /// </summary>
        public bool Test()
        {
            if (!IsAvailable())
                return false;

            try
            {
                Helper.Log.Write(Helper.eLogType.Info, "PixelSampling: Testing capture...");

                bool success = GetCondition();

                if (success && IsConditionValid())
                {
                    // Pre-calculate coordinates during test
                    PreCalculateCoordinates();

                    Helper.Log.Write(Helper.eLogType.Info,
                        $"PixelSampling Test: SUCCESS - Screen: {_condition.Width}x{_condition.Height}, " +
                        $"Points: {_condition.ScreenCapturePoints}, Sleep: {_condition.SleepMS}ms");
                    _isInitialized = true;
                    return true;
                }
                else
                {
                    Helper.Log.Write(Helper.eLogType.Warning, "PixelSampling Test: GetCondition failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"PixelSampling Test exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get screen condition parameters from VideoEnhance library
        /// Tests ALL combinations of API variants and library paths systematically
        /// </summary>
        private bool GetCondition()
        {
            Helper.Log.Write(Helper.eLogType.Info,
                "PixelSampling: Testing ALL combinations of entry points and library paths...");

            // Test Tizen 6 (cs_ve_*) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T6_SO(out _condition),
                (idx, x, y) => MeasurePosition_T6_SO(idx, x, y),
                MeasurePixel_T6_SO,
                "T6", "SO", "cs_ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T6_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T6_SO0(idx, x, y),
                MeasurePixel_T6_SO0,
                "T6", "SO0", "cs_ve_*", ".so.0")) return true;

            // Test Tizen 7 (ve_*) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T7_SO(out _condition),
                (idx, x, y) => MeasurePosition_T7_SO(idx, x, y),
                MeasurePixel_T7_SO,
                "T7", "SO", "ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T7_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T7_SO0(idx, x, y),
                MeasurePixel_T7_SO0,
                "T7", "SO0", "ve_*", ".so.0")) return true;

            // Test Tizen 9+ variant A (tizen_ve_*) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T9A_SO(out _condition),
                (idx, x, y) => MeasurePosition_T9A_SO(idx, x, y),
                MeasurePixel_T9A_SO,
                "T9A", "SO", "tizen_ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T9A_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T9A_SO0(idx, x, y),
                MeasurePixel_T9A_SO0,
                "T9A", "SO0", "tizen_ve_*", ".so.0")) return true;

            // Test Tizen 9+ variant B (samsung_ve_*) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T9B_SO(out _condition),
                (idx, x, y) => MeasurePosition_T9B_SO(idx, x, y),
                MeasurePixel_T9B_SO,
                "T9B", "SO", "samsung_ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T9B_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T9B_SO0(idx, x, y),
                MeasurePixel_T9B_SO0,
                "T9B", "SO0", "samsung_ve_*", ".so.0")) return true;

            // Test Tizen 9+ variant C (no prefix) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T9C_SO(out _condition),
                (idx, x, y) => MeasurePosition_T9C_SO(idx, x, y),
                MeasurePixel_T9C_SO,
                "T9C", "SO", "no prefix", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T9C_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T9C_SO0(idx, x, y),
                MeasurePixel_T9C_SO0,
                "T9C", "SO0", "no prefix", ".so.0")) return true;

            // Test Tizen 9 actual API (ppi_ve_*) - CONFIRMED via library analysis
            if (TryVariant(
                () => MeasureCondition_T9_SO(out _condition),
                (idx, x, y) => MeasurePosition_T9_SO(idx, x, y),
                MeasurePixel_T9_SO,
                "T9", "SO", "ppi_ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T9_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T9_SO0(idx, x, y),
                MeasurePixel_T9_SO0,
                "T9", "SO0", "ppi_ve_*", ".so.0")) return true;

            // All combinations failed
            Helper.Log.Write(Helper.eLogType.Error,
                "PixelSampling: ALL 12 combinations failed (6 entry point variants × 2 library paths)");
            Helper.Log.Write(Helper.eLogType.Error,
                "PixelSampling: libvideoenhance.so does not support RGB pixel sampling on this Tizen version");
            return false;
        }

        /// <summary>
        /// Try a specific API variant + library path combination
        /// Tests ALL 3 entry points to ensure complete API surface exists
        /// </summary>
        private delegate int MeasurePixelDelegate(int index, out Color color);

        private bool TryVariant(
            Func<int> conditionFunc,
            Func<int, int, int, int> positionFunc,
            MeasurePixelDelegate pixelFunc,
            string variant,
            string libPath,
            string entryPrefix,
            string libSuffix)
        {
            try
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: Testing {variant} ({entryPrefix}) with libvideoenhance{libSuffix}");

                // Test 1: MeasureCondition
                int conditionResult = conditionFunc();
                if (conditionResult < 0)
                {
                    Helper.Log.Write(Helper.eLogType.Debug,
                        $"PixelSampling: {variant}/{libPath} condition returned error {conditionResult}");
                    return false;
                }

                // Test 2: MeasurePosition (validate entry point exists with dummy coordinates)
                int positionResult = positionFunc(0, 0, 0);
                // Position may fail if called before proper setup, but entry point should exist
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} position entry point exists (result: {positionResult})");

                // Test 3: MeasurePixel (validate entry point exists)
                Color dummyColor;
                int pixelResult = pixelFunc(0, out dummyColor);
                // Pixel may fail if no position set yet, but entry point should exist
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} pixel entry point exists (result: {pixelResult})");

                // Success - all three entry points exist and condition succeeded
                _workingVariant = variant;
                _workingLibPath = libPath;
                Helper.Log.Write(Helper.eLogType.Info,
                    $"PixelSampling: ✓ SUCCESS - All 3 entry points validated for {variant} ({entryPrefix}) with libvideoenhance{libSuffix}");
                Helper.Log.Write(Helper.eLogType.Debug, $"PixelSampling: Condition result: {conditionResult}");
                LogConditionDetails();
                return true;
            }
            catch (EntryPointNotFoundException ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} entry point not found: {ex.Message}");
                return false;
            }
            catch (DllNotFoundException ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} library file not found: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} exception: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Helper method to log condition details
        /// </summary>
        private void LogConditionDetails()
        {
            Helper.Log.Write(Helper.eLogType.Info,
                $"PixelSampling: Condition - Width: {_condition.Width}, Height: {_condition.Height}, " +
                $"Points: {_condition.ScreenCapturePoints}, PixelDensity: {_condition.PixelDensityX}x{_condition.PixelDensityY}, " +
                $"Sleep: {_condition.SleepMS}ms");
        }

        private bool IsConditionValid()
        {
            bool isValid = _condition.Width > 1 &&
                           _condition.Height > 1 &&
                           _condition.ScreenCapturePoints > 0 &&
                           _condition.PixelDensityX >= 0 &&
                           _condition.PixelDensityY >= 0 &&
                           _condition.PixelDensityX <= _condition.Width &&
                           _condition.PixelDensityY <= _condition.Height &&
                           _condition.SleepMS >= 0;

            if (!isValid)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"PixelSampling: Invalid condition - Screen={_condition.Width}x{_condition.Height}, " +
                    $"Slots={_condition.ScreenCapturePoints}, PixelDensity={_condition.PixelDensityX}x{_condition.PixelDensityY}, " +
                    $"Sleep={_condition.SleepMS}ms");
            }

            return isValid;
        }

        /// <summary>
        /// Call correct MeasurePosition variant based on working combination
        /// </summary>
        private int CallMeasurePosition(int index, int x, int y)
        {
            string key = $"{_workingVariant}_{_workingLibPath}";
            switch (key)
            {
                case "T6_SO": return MeasurePosition_T6_SO(index, x, y);
                case "T6_SO0": return MeasurePosition_T6_SO0(index, x, y);
                case "T7_SO": return MeasurePosition_T7_SO(index, x, y);
                case "T7_SO0": return MeasurePosition_T7_SO0(index, x, y);
                case "T9A_SO": return MeasurePosition_T9A_SO(index, x, y);
                case "T9A_SO0": return MeasurePosition_T9A_SO0(index, x, y);
                case "T9B_SO": return MeasurePosition_T9B_SO(index, x, y);
                case "T9B_SO0": return MeasurePosition_T9B_SO0(index, x, y);
                case "T9C_SO": return MeasurePosition_T9C_SO(index, x, y);
                case "T9C_SO0": return MeasurePosition_T9C_SO0(index, x, y);
                case "T9_SO": return MeasurePosition_T9_SO(index, x, y);
                case "T9_SO0": return MeasurePosition_T9_SO0(index, x, y);
                default:
                    throw new InvalidOperationException($"Unknown variant: {key}");
            }
        }

        /// <summary>
        /// Call correct MeasurePixel variant based on working combination
        /// </summary>
        private int CallMeasurePixel(int index, out Color color)
        {
            string key = $"{_workingVariant}_{_workingLibPath}";
            switch (key)
            {
                case "T6_SO": return MeasurePixel_T6_SO(index, out color);
                case "T6_SO0": return MeasurePixel_T6_SO0(index, out color);
                case "T7_SO": return MeasurePixel_T7_SO(index, out color);
                case "T7_SO0": return MeasurePixel_T7_SO0(index, out color);
                case "T9A_SO": return MeasurePixel_T9A_SO(index, out color);
                case "T9A_SO0": return MeasurePixel_T9A_SO0(index, out color);
                case "T9B_SO": return MeasurePixel_T9B_SO(index, out color);
                case "T9B_SO0": return MeasurePixel_T9B_SO0(index, out color);
                case "T9C_SO": return MeasurePixel_T9C_SO(index, out color);
                case "T9C_SO0": return MeasurePixel_T9C_SO0(index, out color);
                case "T9_SO": return MeasurePixel_T9_SO(index, out color);
                case "T9_SO0": return MeasurePixel_T9_SO0(index, out color);
                default:
                    throw new InvalidOperationException($"Unknown variant: {key}");
            }
        }

        /// <summary>
        /// Pre-calculate pixel coordinates from normalized positions
        /// Called once during initialization to avoid repeated calculations
        /// </summary>
        private void PreCalculateCoordinates()
        {
            _pixelCoordinates = new PixelCoordinate[_capturedPoints.Length];

            if (!IsConditionValid())
            {
                throw new InvalidOperationException("PixelSampling: Cannot calculate coordinates from an invalid condition");
            }

            int densityX = Math.Max(1, _condition.PixelDensityX);
            int densityY = Math.Max(1, _condition.PixelDensityY);

            for (int i = 0; i < _capturedPoints.Length; i++)
            {
                // Treat the configured pixel density as the size of the sample area
                // and place that area around the normalized anchor coordinate.
                int x = (int)Math.Round(_capturedPoints[i].X * (_condition.Width - 1)) - densityX / 2;
                int y = (int)Math.Round(_capturedPoints[i].Y * (_condition.Height - 1)) - densityY / 2;
                x = Math.Max(0, Math.Min(_condition.Width - densityX, x));
                y = Math.Max(0, Math.Min(_condition.Height - densityY, y));

                _pixelCoordinates[i].X = x;
                _pixelCoordinates[i].Y = y;
            }

            Helper.Log.Write(Helper.eLogType.Info,
                $"PixelSampling: Pre-calculated {_pixelCoordinates.Length} edge anchors " +
                $"(top={_pixelCoordinates[0].X},{_pixelCoordinates[0].Y}; " +
                $"right={_pixelCoordinates[1].X},{_pixelCoordinates[1].Y}; " +
                $"bottom={_pixelCoordinates[2].X},{_pixelCoordinates[2].Y}; " +
                $"left={_pixelCoordinates[3].X},{_pixelCoordinates[3].Y})");
        }

        /// <summary>
        /// Sample pixel colors from predefined screen positions
        /// Samples each configured hardware-slot batch before reusing its slots.
        /// </summary>
        private Color[] GetColors(out bool hasUsableSamples, out string unavailableReason)
        {
            long captureStarted = Stopwatch.GetTimestamp();
            hasUsableSamples = false;
            unavailableReason = null;
            Color[] colorData = new Color[_capturedPoints.Length];
            Color[] rawSamples = new Color[_capturedPoints.Length];
            bool[] estimatedSamples = new bool[_capturedPoints.Length];
            string[] sampleDetails = new string[_capturedPoints.Length];
            for (int pointIndex = 0; pointIndex < sampleDetails.Length; pointIndex++)
            {
                sampleDetails[pointIndex] = "not attempted this capture";
            }

            if (!IsConditionValid() || _pixelCoordinates == null || _pixelCoordinates.Length != _capturedPoints.Length)
            {
                unavailableReason = "invalid capture condition or missing coordinates";
                return null;
            }

            int slotCount = Math.Min(_condition.ScreenCapturePoints, _capturedPoints.Length);
            bool[] freshSamples = new bool[_capturedPoints.Length];
            int[] pointIndexes = new int[slotCount];
            int[] positionResults = new int[slotCount];
            bool[] positionSet = new bool[slotCount];
            int batchStart = 0;

            // Each slot is reused only after its current pixel has been read.
            // On the S90C this makes two 20 ms batches (about 25 FPS maximum).
            while (batchStart < SamplingOrder.Length)
            {
                int currentBatchSize = Math.Min(slotCount, SamplingOrder.Length - batchStart);
                Array.Clear(positionSet, 0, currentBatchSize);
                bool hasPositionToRead = false;

                for (int slot = 0; slot < currentBatchSize; slot++)
                {
                    int pointIndex = SamplingOrder[batchStart + slot];
                    pointIndexes[slot] = pointIndex;

                    PixelCoordinate coordinate = _pixelCoordinates[pointIndex];
                    int result = CallMeasurePosition(slot, coordinate.X, coordinate.Y);
                    positionResults[slot] = result;
                    if (result >= 0)
                    {
                        positionSet[slot] = true;
                        hasPositionToRead = true;
                        sampleDetails[pointIndex] = $"position={result}; pixel=not read";
                    }
                    else
                    {
                        _positionErrorsSinceLog++;
                        _anchorErrorCounts[pointIndex]++;
                        sampleDetails[pointIndex] = $"position={result}";
                        DiscardPendingSample(pointIndex);
                    }
                }

                if (hasPositionToRead && _condition.SleepMS > 0)
                {
                    Thread.Sleep(_condition.SleepMS);
                }

                for (int slot = 0; slot < currentBatchSize; slot++)
                {
                    if (!positionSet[slot])
                    {
                        int skippedPointIndex = pointIndexes[slot];
                        Helper.Log.Write(Helper.eLogType.Debug,
                            $"PixelSampling: Skipping pixel read for slot={slot}, point={skippedPointIndex}, " +
                            $"positionResult={positionResults[slot]}");
                        continue;
                    }

                    int pointIndex = pointIndexes[slot];
                    Color sample;
                    sampleDetails[pointIndex] =
                        $"position={positionResults[slot]}; pixel=read entered (slot={slot})";
                    int result;
                    try
                    {
                        result = CallMeasurePixel(slot, out sample);
                    }
                    catch (Exception ex)
                    {
                        _pixelErrorsSinceLog++;
                        _anchorErrorCounts[pointIndex]++;
                        sampleDetails[pointIndex] =
                            $"position={positionResults[slot]}; pixel=exception({ex.GetType().Name}: {ex.Message})";
                        DiscardPendingSample(pointIndex);
                        continue;
                    }

                    sampleDetails[pointIndex] =
                        $"position={positionResults[slot]}; pixel={result}; " +
                        $"RGB10=({sample.R},{sample.G},{sample.B}); slot={slot}";

                    if (result < 0)
                    {
                        _pixelErrorsSinceLog++;
                        _anchorErrorCounts[pointIndex]++;
                        sampleDetails[pointIndex] = $"position={positionResults[slot]}; pixel={result}";
                        DiscardPendingSample(pointIndex);
                        continue;
                    }

                    if (sample.R < 0 || sample.R > 1023 ||
                        sample.G < 0 || sample.G > 1023 ||
                        sample.B < 0 || sample.B > 1023)
                    {
                        _invalidSamplesSinceLog++;
                        _anchorErrorCounts[pointIndex]++;
                        sampleDetails[pointIndex] =
                            $"position={positionResults[slot]}; pixel={result}; invalidRGB10=({sample.R},{sample.G},{sample.B})";
                        DiscardPendingSample(pointIndex);
                        continue;
                    }

                    long sampleTimestamp = Stopwatch.GetTimestamp();
                    _lastSuccessfulSampleTimestamps[pointIndex] = sampleTimestamp;
                    rawSamples[pointIndex] = sample;
                    colorData[pointIndex] = FilterSample(pointIndex, sample);
                    freshSamples[pointIndex] = true;
                    sampleDetails[pointIndex] =
                        $"position={positionResults[slot]}; pixel={result}; RGB10=({sample.R},{sample.G},{sample.B})";
                }

                batchStart += currentBatchSize;
            }

            long now = Stopwatch.GetTimestamp();
            bool[] reliableSamples = new bool[colorData.Length];
            int reliableSampleCount = 0;
            for (int pointIndex = 0; pointIndex < colorData.Length; pointIndex++)
            {
                if (freshSamples[pointIndex])
                {
                    reliableSamples[pointIndex] = true;
                    reliableSampleCount++;
                    continue;
                }

                if (_hasFilteredColors[pointIndex] &&
                    IsSampleRecent(now, _lastSuccessfulSampleTimestamps[pointIndex]))
                {
                    // Keep a brief, last-known-good value through transient API errors.
                    colorData[pointIndex] = _lastFilteredColors[pointIndex];
                    reliableSamples[pointIndex] = true;
                    reliableSampleCount++;
                }
            }

            // If one or more anchors are missing on startup or after a long native
            // error, estimate only those edges from the nearest reliable perimeter
            // anchor. Never turn one failed slot into a black whole-frame drop.
            if (reliableSampleCount >= 2)
            {
                for (int pointIndex = 0; pointIndex < colorData.Length; pointIndex++)
                {
                    if (reliableSamples[pointIndex])
                    {
                        continue;
                    }

                    int nearestReliable = FindNearestReliableAnchor(pointIndex, reliableSamples);
                    colorData[pointIndex] = colorData[nearestReliable];
                    estimatedSamples[pointIndex] = true;
                }

                hasUsableSamples = true;
            }
            else
            {
                unavailableReason = BuildUnavailableReason(reliableSamples, sampleDetails, now);
            }

            LogSamplingErrorSummary(now);
            LogSampleSummary(
                now,
                captureStarted,
                rawSamples,
                colorData,
                freshSamples,
                reliableSamples,
                estimatedSamples,
                sampleDetails,
                hasUsableSamples);
            return colorData;
        }

        private static int FindNearestReliableAnchor(int pointIndex, bool[] reliableSamples)
        {
            for (int distance = 1; distance < reliableSamples.Length; distance++)
            {
                int clockwise = (pointIndex + distance) % reliableSamples.Length;
                if (reliableSamples[clockwise])
                {
                    return clockwise;
                }

                int counterClockwise = (pointIndex - distance + reliableSamples.Length) % reliableSamples.Length;
                if (reliableSamples[counterClockwise])
                {
                    return counterClockwise;
                }
            }

            throw new InvalidOperationException("PixelSampling: No reliable anchor available for fallback");
        }

        private string BuildUnavailableReason(bool[] reliableSamples, string[] sampleDetails, long now)
        {
            string[] edgeNames = new string[] { "top", "right", "bottom", "left" };
            System.Text.StringBuilder reason = new System.Text.StringBuilder("fewer than two reliable edge anchors; unavailable=");
            bool first = true;

            for (int pointIndex = 0; pointIndex < reliableSamples.Length; pointIndex++)
            {
                if (reliableSamples[pointIndex])
                {
                    continue;
                }

                if (!first)
                {
                    reason.Append(",");
                }

                reason.Append(edgeNames[pointIndex]);
                reason.Append('(');
                reason.Append("no recent successful sample; current=");
                reason.Append(sampleDetails[pointIndex] ?? "unknown");
                reason.Append("; lastSuccessAge=");
                long lastSuccessTimestamp = _lastSuccessfulSampleTimestamps[pointIndex];
                if (lastSuccessTimestamp == 0)
                {
                    reason.Append("never");
                }
                else
                {
                    double ageMilliseconds = (now - lastSuccessTimestamp) * 1000.0 / Stopwatch.Frequency;
                    reason.Append(ageMilliseconds.ToString("F0"));
                    reason.Append("ms");
                }

                reason.Append(", errorsSinceLastSummary=");
                reason.Append(_anchorErrorCounts[pointIndex]);
                reason.Append(")");
                first = false;
            }

            reason.Append($"; slots={_condition.ScreenCapturePoints}");
            return reason.ToString();
        }

        private Color FilterSample(int pointIndex, Color sample)
        {
            if (!_hasFilteredColors[pointIndex])
            {
                _lastFilteredColors[pointIndex] = sample;
                _hasFilteredColors[pointIndex] = true;
                _hasPendingColors[pointIndex] = false;
                return sample;
            }

            Color stable = _lastFilteredColors[pointIndex];
            if (_hasPendingColors[pointIndex])
            {
                Color pending = _pendingColors[pointIndex];
                if (MaxChannelDifference(sample, pending) <= PendingSampleMatchThreshold)
                {
                    Color confirmed = AverageColors(sample, pending);
                    _lastFilteredColors[pointIndex] = confirmed;
                    _hasPendingColors[pointIndex] = false;
                    _pendingSampleCounts[pointIndex] = 0;
                    return confirmed;
                }

                if (MaxChannelDifference(sample, stable) <= PendingSampleMatchThreshold)
                {
                    // The abrupt value was not repeated; treat it as a one-frame spike.
                    _hasPendingColors[pointIndex] = false;
                    _pendingSampleCounts[pointIndex] = 0;
                    return stable;
                }

                _pendingColors[pointIndex] = sample;
                _pendingSampleCounts[pointIndex]++;
                if (_pendingSampleCounts[pointIndex] >= MaximumPendingSamples)
                {
                    // Real moving content may not repeat an exact color; accept a
                    // sustained sequence of abrupt measurements after brief confirmation.
                    _lastFilteredColors[pointIndex] = sample;
                    _hasPendingColors[pointIndex] = false;
                    _pendingSampleCounts[pointIndex] = 0;
                    return sample;
                }

                return stable;
            }

            if (MaxChannelDifference(sample, stable) >= AbruptChangeThreshold)
            {
                _pendingColors[pointIndex] = sample;
                _pendingSampleCounts[pointIndex] = 1;
                _hasPendingColors[pointIndex] = true;
                _abruptCandidatesSinceSummary++;
                return stable;
            }

            // Light smoothing reduces small sample noise without washing out colors.
            Color smoothed = BlendColors(stable, sample, 3, 4);
            _lastFilteredColors[pointIndex] = smoothed;
            return smoothed;
        }

        private void DiscardPendingSample(int pointIndex)
        {
            _hasPendingColors[pointIndex] = false;
            _pendingSampleCounts[pointIndex] = 0;
        }

        private static int MaxChannelDifference(Color first, Color second)
        {
            return Math.Max(Math.Abs(first.R - second.R),
                Math.Max(Math.Abs(first.G - second.G), Math.Abs(first.B - second.B)));
        }

        private static Color AverageColors(Color first, Color second)
        {
            return new Color
            {
                R = (first.R + second.R + 1) / 2,
                G = (first.G + second.G + 1) / 2,
                B = (first.B + second.B + 1) / 2
            };
        }

        private static Color BlendColors(Color previous, Color current, int currentWeight, int denominator)
        {
            int previousWeight = denominator - currentWeight;
            return new Color
            {
                R = (previous.R * previousWeight + current.R * currentWeight + denominator / 2) / denominator,
                G = (previous.G * previousWeight + current.G * currentWeight + denominator / 2) / denominator,
                B = (previous.B * previousWeight + current.B * currentWeight + denominator / 2) / denominator
            };
        }

        private static bool IsSampleRecent(long now, long sampleTimestamp)
        {
            if (sampleTimestamp <= 0 || now < sampleTimestamp)
            {
                return false;
            }

            long maximumAgeTicks = Stopwatch.Frequency * MaximumStaleSampleMs / 1000;
            return now - sampleTimestamp <= maximumAgeTicks;
        }

        private void LogSamplingErrorSummary(long now)
        {
            if (_positionErrorsSinceLog == 0 && _pixelErrorsSinceLog == 0 && _invalidSamplesSinceLog == 0)
            {
                return;
            }

            long intervalTicks = Stopwatch.Frequency * SamplingErrorLogIntervalMs / 1000;
            if (_lastSamplingErrorLogTimestamp != 0 && now - _lastSamplingErrorLogTimestamp < intervalTicks)
            {
                return;
            }

            Helper.Log.Write(Helper.eLogType.Warning,
                $"PixelSampling: Sample errors in interval - position={_positionErrorsSinceLog}, " +
                $"pixel={_pixelErrorsSinceLog}, invalidColor={_invalidSamplesSinceLog}; " +
                $"anchorErrors T/R/B/L={_anchorErrorCounts[0]}/{_anchorErrorCounts[1]}/" +
                $"{_anchorErrorCounts[2]}/{_anchorErrorCounts[3]}");

            _positionErrorsSinceLog = 0;
            _pixelErrorsSinceLog = 0;
            _invalidSamplesSinceLog = 0;
            Array.Clear(_anchorErrorCounts, 0, _anchorErrorCounts.Length);
            _lastSamplingErrorLogTimestamp = now;
        }

        private void LogSampleSummary(
            long now,
            long captureStarted,
            Color[] rawSamples,
            Color[] outputColors,
            bool[] freshSamples,
            bool[] reliableSamples,
            bool[] estimatedSamples,
            string[] sampleDetails,
            bool hasUsableSamples)
        {
            long intervalTicks = Stopwatch.Frequency * SamplingErrorLogIntervalMs / 1000;
            long lastSummaryTimestamp = hasUsableSamples
                ? _lastSampleSummaryTimestamp
                : _lastUnavailableSampleSummaryTimestamp;
            if (lastSummaryTimestamp != 0 && now - lastSummaryTimestamp < intervalTicks)
            {
                return;
            }

            double samplingMilliseconds = (now - captureStarted) * 1000.0 / Stopwatch.Frequency;
            Helper.Log.Write(Helper.eLogType.Debug,
                $"PixelSampling: Read diagnostics v1; API={_workingVariant}/{_workingLibPath}, " +
                $"usable={hasUsableSamples}; RGB10 raw T={FormatColor(rawSamples[0], freshSamples[0])} " +
                $"R={FormatColor(rawSamples[1], freshSamples[1])} " +
                $"B={FormatColor(rawSamples[2], freshSamples[2])} " +
                $"L={FormatColor(rawSamples[3], freshSamples[3])}; " +
                $"filtered T={FormatColor(outputColors[0], reliableSamples[0], estimatedSamples[0])} " +
                $"R={FormatColor(outputColors[1], reliableSamples[1], estimatedSamples[1])} " +
                $"B={FormatColor(outputColors[2], reliableSamples[2], estimatedSamples[2])} " +
                $"L={FormatColor(outputColors[3], reliableSamples[3], estimatedSamples[3])}; " +
                $"sampleStatus T={sampleDetails[0]} R={sampleDetails[1]} " +
                $"B={sampleDetails[2]} L={sampleDetails[3]}; " +
                $"slots={_condition.ScreenCapturePoints}, sampling={samplingMilliseconds:F1}ms, " +
                $"estimated={CountEstimatedSamples(estimatedSamples)}, " +
                $"abruptCandidates={_abruptCandidatesSinceSummary}");

            if (hasUsableSamples)
            {
                _lastSampleSummaryTimestamp = now;
            }
            else
            {
                _lastUnavailableSampleSummaryTimestamp = now;
            }
            _abruptCandidatesSinceSummary = 0;
        }

        private static int CountEstimatedSamples(bool[] estimatedSamples)
        {
            int count = 0;
            for (int i = 0; i < estimatedSamples.Length; i++)
            {
                if (estimatedSamples[i])
                {
                    count++;
                }
            }

            return count;
        }

        private static string FormatColor(Color color, bool isAvailable, bool isEstimated = false)
        {
            if (!isAvailable)
            {
                return "unavailable";
            }

            return isEstimated ? $"({color.R},{color.G},{color.B})*" : $"({color.R},{color.G},{color.B})";
        }

        /// <summary>
        /// Convert sampled pixel colors to NV12 format using BT.2020 color space
        /// Convert four sampled edge anchors to a 64x48 NV12 image.
        /// The existing BT.2020 coefficients are retained pending hardware validation.
        /// </summary>
        private (byte[] yData, byte[] uvData) ConvertColorsToNV12(Color[] colors)
        {
            const int width = 64;
            const int height = 48;

            if (colors == null || colors.Length != _capturedPoints.Length)
            {
                throw new ArgumentException("PixelSampling: Expected one sampled color for each of the four edges", nameof(colors));
            }

            // The legacy edge rasterizer expects four samples per side. Replicate
            // each anchor along its edge rather than interpolating unrelated sides.
            Color[] edgeColors = colors;
            colors = new Color[16];
            for (int edge = 0; edge < edgeColors.Length; edge++)
            {
                for (int point = 0; point < 4; point++)
                {
                    colors[edge * 4 + point] = edgeColors[edge];
                }
            }

            // Allocate NV12 buffers
            byte[] yData = new byte[width * height];
            byte[] uvData = new byte[width * height / 2]; // UV plane is half the size

            // Create virtual RGB image (same logic as original ToImage method)
            byte[] rgbImage = new byte[width * height * 3]; // RGB888

            // Initialize with black
            for (int i = 0; i < rgbImage.Length; i++)
            {
                rgbImage[i] = 0;
            }

            // Four edge anchors expanded to the legacy color-map layout:
            // colors[0-3]   = Top, colors[4-7] = Right,
            // colors[8-11]  = Bottom, colors[12-15] = Left.

            // Top edge (colors 0-3) with linear interpolation
            for (int x = 0; x < 64; x++)
            {
                // Determine which segment this x falls into (4 segments)
                float segmentPos = (x / 63.0f) * 3.0f; // 0.0 to 3.0
                int segment = Math.Min(2, (int)segmentPos); // 0, 1, or 2
                float t = segmentPos - segment; // Position within segment (0.0 to 1.0)

                byte r, g, b;
                if (segment == 0) // colors[0] to colors[1]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[0].R) + t * ScaleTo8Bit(colors[1].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[0].G) + t * ScaleTo8Bit(colors[1].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[0].B) + t * ScaleTo8Bit(colors[1].B));
                }
                else if (segment == 1) // colors[1] to colors[2]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[1].R) + t * ScaleTo8Bit(colors[2].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[1].G) + t * ScaleTo8Bit(colors[2].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[1].B) + t * ScaleTo8Bit(colors[2].B));
                }
                else // segment == 2, colors[2] to colors[3]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[2].R) + t * ScaleTo8Bit(colors[3].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[2].G) + t * ScaleTo8Bit(colors[3].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[2].B) + t * ScaleTo8Bit(colors[3].B));
                }

                for (int y = 0; y < 4; y++)
                {
                    int idx = (y * width + x) * 3;
                    rgbImage[idx + 0] = r;
                    rgbImage[idx + 1] = g;
                    rgbImage[idx + 2] = b;
                }
            }

            // Bottom edge (colors 8-11) with linear interpolation (right to left)
            for (int x = 0; x < 64; x++)
            {
                // Determine which segment this x falls into (4 segments, reversed)
                float segmentPos = (x / 63.0f) * 3.0f; // 0.0 to 3.0
                int segment = Math.Min(2, (int)segmentPos); // 0, 1, or 2
                float t = segmentPos - segment; // Position within segment (0.0 to 1.0)

                byte r, g, b;
                if (segment == 0) // colors[11] to colors[10]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[11].R) + t * ScaleTo8Bit(colors[10].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[11].G) + t * ScaleTo8Bit(colors[10].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[11].B) + t * ScaleTo8Bit(colors[10].B));
                }
                else if (segment == 1) // colors[10] to colors[9]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[10].R) + t * ScaleTo8Bit(colors[9].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[10].G) + t * ScaleTo8Bit(colors[9].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[10].B) + t * ScaleTo8Bit(colors[9].B));
                }
                else // segment == 2, colors[9] to colors[8]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[9].R) + t * ScaleTo8Bit(colors[8].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[9].G) + t * ScaleTo8Bit(colors[8].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[9].B) + t * ScaleTo8Bit(colors[8].B));
                }

                for (int y = 44; y < 48; y++)
                {
                    int idx = (y * width + x) * 3;
                    rgbImage[idx + 0] = r;
                    rgbImage[idx + 1] = g;
                    rgbImage[idx + 2] = b;
                }
            }

            // Left edge (colors 12-15) with linear interpolation (bottom to top)
            for (int y = 0; y < 48; y++)
            {
                // Determine which segment this y falls into (4 segments)
                float segmentPos = (y / 47.0f) * 3.0f; // 0.0 to 3.0
                int segment = Math.Min(2, (int)segmentPos); // 0, 1, or 2
                float t = segmentPos - segment; // Position within segment (0.0 to 1.0)

                byte r, g, b;
                if (segment == 0) // colors[15] to colors[14]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[15].R) + t * ScaleTo8Bit(colors[14].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[15].G) + t * ScaleTo8Bit(colors[14].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[15].B) + t * ScaleTo8Bit(colors[14].B));
                }
                else if (segment == 1) // colors[14] to colors[13]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[14].R) + t * ScaleTo8Bit(colors[13].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[14].G) + t * ScaleTo8Bit(colors[13].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[14].B) + t * ScaleTo8Bit(colors[13].B));
                }
                else // segment == 2, colors[13] to colors[12]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[13].R) + t * ScaleTo8Bit(colors[12].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[13].G) + t * ScaleTo8Bit(colors[12].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[13].B) + t * ScaleTo8Bit(colors[12].B));
                }

                for (int x = 0; x < 3; x++)
                {
                    int idx = (y * width + x) * 3;
                    rgbImage[idx + 0] = r;
                    rgbImage[idx + 1] = g;
                    rgbImage[idx + 2] = b;
                }
            }

            // Right edge (colors 4-7) with linear interpolation (top to bottom)
            for (int y = 0; y < 48; y++)
            {
                // Determine which segment this y falls into (4 segments)
                float segmentPos = (y / 47.0f) * 3.0f; // 0.0 to 3.0
                int segment = Math.Min(2, (int)segmentPos); // 0, 1, or 2
                float t = segmentPos - segment; // Position within segment (0.0 to 1.0)

                byte r, g, b;
                if (segment == 0) // colors[4] to colors[5]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[4].R) + t * ScaleTo8Bit(colors[5].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[4].G) + t * ScaleTo8Bit(colors[5].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[4].B) + t * ScaleTo8Bit(colors[5].B));
                }
                else if (segment == 1) // colors[5] to colors[6]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[5].R) + t * ScaleTo8Bit(colors[6].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[5].G) + t * ScaleTo8Bit(colors[6].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[5].B) + t * ScaleTo8Bit(colors[6].B));
                }
                else // segment == 2, colors[6] to colors[7]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[6].R) + t * ScaleTo8Bit(colors[7].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[6].G) + t * ScaleTo8Bit(colors[7].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[6].B) + t * ScaleTo8Bit(colors[7].B));
                }

                for (int x = 61; x < 64; x++)
                {
                    int idx = (y * width + x) * 3;
                    rgbImage[idx + 0] = r;
                    rgbImage[idx + 1] = g;
                    rgbImage[idx + 2] = b;
                }
            }

            // Blend the narrow corner overlaps so adjacent edge colors do not
            // overwrite one another at the four corners.
            BlendRgbCorner(rgbImage, width, 0, 0, 3, 4, colors[0], colors[12]);
            BlendRgbCorner(rgbImage, width, width - 3, 0, 3, 4, colors[0], colors[4]);
            BlendRgbCorner(rgbImage, width, 0, height - 4, 3, 4, colors[8], colors[12]);
            BlendRgbCorner(rgbImage, width, width - 3, height - 4, 3, 4, colors[8], colors[4]);

            // Convert RGB to NV12 using the existing BT.2020 full-range matrix.
            // The output matrix/range is kept unchanged pending TV-side validation.
            // Y plane
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int rgbIdx = (y * width + x) * 3;
                    byte r = rgbImage[rgbIdx + 0];
                    byte g = rgbImage[rgbIdx + 1];
                    byte b = rgbImage[rgbIdx + 2];

                    // BT.2020 Y = 0.2627R + 0.678G + 0.0593B
                    int yVal = (int)Math.Round(0.2627 * r + 0.678 * g + 0.0593 * b);
                    yData[y * width + x] = (byte)Math.Max(0, Math.Min(255, yVal));
                }
            }

            // UV plane (interleaved, subsampled 2x2)
            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    // Average the 2x2 RGB block before chroma subsampling.
                    int rgbIdx = (y * width + x) * 3;
                    int rgbIdxRight = rgbIdx + 3;
                    int rgbIdxBelow = rgbIdx + width * 3;
                    int rgbIdxBelowRight = rgbIdxBelow + 3;
                    byte r = (byte)((rgbImage[rgbIdx] + rgbImage[rgbIdxRight] +
                                     rgbImage[rgbIdxBelow] + rgbImage[rgbIdxBelowRight] + 2) / 4);
                    byte g = (byte)((rgbImage[rgbIdx + 1] + rgbImage[rgbIdxRight + 1] +
                                     rgbImage[rgbIdxBelow + 1] + rgbImage[rgbIdxBelowRight + 1] + 2) / 4);
                    byte b = (byte)((rgbImage[rgbIdx + 2] + rgbImage[rgbIdxRight + 2] +
                                     rgbImage[rgbIdxBelow + 2] + rgbImage[rgbIdxBelowRight + 2] + 2) / 4);

                    // BT.2020 U = -0.1396R - 0.36037G + 0.5B + 128
                    // BT.2020 V = 0.5R - 0.4598G - 0.0402B + 128
                    int uVal = (int)Math.Round(-0.1396 * r - 0.36037 * g + 0.5 * b + 128);
                    int vVal = (int)Math.Round(0.5 * r - 0.4598 * g - 0.0402 * b + 128);

                    int uvIdx = (y / 2) * width + x;
                    uvData[uvIdx + 0] = (byte)Math.Max(0, Math.Min(255, uVal)); // U
                    uvData[uvIdx + 1] = (byte)Math.Max(0, Math.Min(255, vVal)); // V
                }
            }

            return (yData, uvData);
        }

        private static void BlendRgbCorner(
            byte[] rgbImage,
            int imageWidth,
            int startX,
            int startY,
            int cornerWidth,
            int cornerHeight,
            Color firstEdge,
            Color secondEdge)
        {
            byte red = (byte)((ScaleTo8Bit(firstEdge.R) + ScaleTo8Bit(secondEdge.R) + 1) / 2);
            byte green = (byte)((ScaleTo8Bit(firstEdge.G) + ScaleTo8Bit(secondEdge.G) + 1) / 2);
            byte blue = (byte)((ScaleTo8Bit(firstEdge.B) + ScaleTo8Bit(secondEdge.B) + 1) / 2);

            for (int y = startY; y < startY + cornerHeight; y++)
            {
                for (int x = startX; x < startX + cornerWidth; x++)
                {
                    int rgbIndex = (y * imageWidth + x) * 3;
                    rgbImage[rgbIndex] = red;
                    rgbImage[rgbIndex + 1] = green;
                    rgbImage[rgbIndex + 2] = blue;
                }
            }
        }

        /// <summary>
        /// Convert 10-bit color value (0-1023) to 8-bit (0-255) using proper scaling
        /// Uses scaling rather than clamping to preserve color accuracy
        /// </summary>
        private static byte ScaleTo8Bit(int value)
        {
            // Scale 10-bit (0-1023) to 8-bit (0-255) with nearest rounding.
            int clampedValue = Math.Max(0, Math.Min(1023, value));
            return (byte)((clampedValue * 255 + 511) / 1023);
        }

        /// <summary>
        /// Capture screen using pixel sampling
        /// </summary>
        public CaptureResult Capture(int width, int height)
        {
            try
            {
                // Initialize if not already done
                if (!_isInitialized)
                {
                    if (!GetCondition() || !IsConditionValid())
                    {
                        return CaptureResult.CreateFailure("PixelSampling: Failed to get condition");
                    }

                    // Pre-calculate all pixel coordinates once
                    PreCalculateCoordinates();

                    _isInitialized = true;
                }

                // Sample pixels from screen
                bool hasUsableSamples;
                string unavailableReason;
                Color[] colors = GetColors(out hasUsableSamples, out unavailableReason);

                if (colors == null || colors.Length != _capturedPoints.Length || !hasUsableSamples)
                {
                    return CaptureResult.CreateFailure($"PixelSampling: Capture samples unavailable ({unavailableReason ?? "unknown reason"})");
                }

                // Convert to NV12 format
                var (yData, uvData) = ConvertColorsToNV12(colors);

                // Return success with 64x48 sampled image
                return CaptureResult.CreateSuccess(yData, uvData, 64, 48);
            }
            catch (Exception ex)
            {
                return CaptureResult.CreateFailure($"PixelSampling exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Clean up resources
        /// </summary>
        public void Cleanup()
        {
            _isInitialized = false;
            _pixelCoordinates = null;
            Array.Clear(_lastFilteredColors, 0, _lastFilteredColors.Length);
            Array.Clear(_hasFilteredColors, 0, _hasFilteredColors.Length);
            Array.Clear(_pendingColors, 0, _pendingColors.Length);
            Array.Clear(_hasPendingColors, 0, _hasPendingColors.Length);
            Array.Clear(_pendingSampleCounts, 0, _pendingSampleCounts.Length);
            Array.Clear(_lastSuccessfulSampleTimestamps, 0, _lastSuccessfulSampleTimestamps.Length);
            Array.Clear(_anchorErrorCounts, 0, _anchorErrorCounts.Length);
            _lastSamplingErrorLogTimestamp = 0;
            _lastSampleSummaryTimestamp = 0;
            _lastUnavailableSampleSummaryTimestamp = 0;
            _positionErrorsSinceLog = 0;
            _pixelErrorsSinceLog = 0;
            _invalidSamplesSinceLog = 0;
            _abruptCandidatesSinceSummary = 0;
            Helper.Log.Write(Helper.eLogType.Debug, "PixelSampling: Cleaned up");
        }
    }
}
