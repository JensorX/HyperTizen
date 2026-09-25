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

        // 16-point sampling grid (normalized coordinates 0.0-1.0)
        // 4 points per edge for better color representation
        // With synchronized batching, we achieve 40 FPS even with 16 points
        private CapturePoint[] _capturedPoints = new CapturePoint[] {
            // Top edge (4 points) - left to right
            new CapturePoint(0.20, 0.05),
            new CapturePoint(0.40, 0.05),
            new CapturePoint(0.60, 0.05),
            new CapturePoint(0.80, 0.05),
            // Right edge (4 points) - top to bottom
            new CapturePoint(0.95, 0.20),
            new CapturePoint(0.95, 0.40),
            new CapturePoint(0.95, 0.60),
            new CapturePoint(0.95, 0.80),
            // Bottom edge (4 points) - right to left
            new CapturePoint(0.80, 0.95),
            new CapturePoint(0.60, 0.95),
            new CapturePoint(0.40, 0.95),
            new CapturePoint(0.20, 0.95),
            // Left edge (4 points) - bottom to top
            new CapturePoint(0.05, 0.80),
            new CapturePoint(0.05, 0.60),
            new CapturePoint(0.05, 0.40),
            new CapturePoint(0.05, 0.20)
        };
        private Color[] _lastGoodColors = new Color[16];
        private bool[] _hasLastGoodColors = new bool[16];
        private bool _hasLoggedSampleRange = false;
        private DateTime _lastSamplingErrorLog = DateTime.MinValue;

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

                if (success)
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

                if (_condition.Width <= 0 || _condition.Height <= 0 || _condition.ScreenCapturePoints <= 0)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"PixelSampling: {variant}/{libPath} returned invalid condition " +
                        $"({_condition.Width}x{_condition.Height}, points={_condition.ScreenCapturePoints})");
                    return false;
                }

                // Validate that a real position can be configured before selecting this API variant.
                int positionResult = positionFunc(0, _condition.Width / 2, _condition.Height / 2);
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} position entry point exists (result: {positionResult})");
                if (positionResult < 0)
                {
                    return false;
                }

                if (_condition.SleepMS > 0)
                {
                    Thread.Sleep(_condition.SleepMS);
                }

                // Validate that the configured measurement slot can return a sample.
                Color dummyColor;
                int pixelResult = pixelFunc(0, out dummyColor);
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} pixel entry point exists (result: {pixelResult})");
                if (pixelResult < 0)
                {
                    return false;
                }

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

            for (int i = 0; i < _capturedPoints.Length; i++)
            {
                // Convert normalized coordinates to pixel coordinates
                int x = (int)(_capturedPoints[i].X * (double)_condition.Width) - _condition.PixelDensityX / 2;
                int y = (int)(_capturedPoints[i].Y * (double)_condition.Height) - _condition.PixelDensityY / 2;

                // Clamp coordinates to valid screen bounds
                x = (x >= _condition.Width - _condition.PixelDensityX) ?
                    _condition.Width - (_condition.PixelDensityX + 1) : x;
                y = (y >= _condition.Height - _condition.PixelDensityY) ?
                    (_condition.Height - _condition.PixelDensityY + 1) : y;

                // Ensure coordinates are not negative
                x = Math.Max(0, x);
                y = Math.Max(0, y);

                _pixelCoordinates[i].X = x;
                _pixelCoordinates[i].Y = y;
            }

            Helper.Log.Write(Helper.eLogType.Info,
                $"PixelSampling: Pre-calculated {_pixelCoordinates.Length} pixel coordinates");
        }

        /// <summary>
        /// Sample the edge points in batches no larger than the native measurement capacity.
        /// A batch must be read before its native slots are reused for the next batch.
        /// </summary>
        private Color[] GetColors()
        {
            Color[] colorData = new Color[_capturedPoints.Length];

            int batchCapacity = _condition.ScreenCapturePoints;
            if (batchCapacity <= 0 || _pixelCoordinates == null ||
                _pixelCoordinates.Length != _capturedPoints.Length)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"PixelSampling: Invalid sampling configuration (capacity={batchCapacity})");
                return null;
            }

            int[] positionResults = new int[Math.Min(batchCapacity, _capturedPoints.Length)];
            int usableSampleCount = 0;

            for (int batchStart = 0; batchStart < _capturedPoints.Length; batchStart += batchCapacity)
            {
                int batchCount = Math.Min(batchCapacity, _capturedPoints.Length - batchStart);

                // Configure this batch, then wait for the native measurement to settle.
                for (int slot = 0; slot < batchCount; slot++)
                {
                    int pointIndex = batchStart + slot;
                    PixelCoordinate coordinate = _pixelCoordinates[pointIndex];
                    positionResults[slot] = CallMeasurePosition(slot, coordinate.X, coordinate.Y);

                    if (positionResults[slot] < 0)
                    {
                        LogSamplingError(
                            $"PixelSampling: MeasurePosition failed for point {pointIndex} at " +
                            $"({coordinate.X}, {coordinate.Y}) with error {positionResults[slot]}");
                    }
                }

                if (_condition.SleepMS > 0)
                {
                    Thread.Sleep(_condition.SleepMS);
                }

                // Read the batch before its slots are overwritten by the next batch.
                for (int slot = 0; slot < batchCount; slot++)
                {
                    int pointIndex = batchStart + slot;
                    Color color = default(Color);
                    bool sampleValid = positionResults[slot] >= 0;

                    if (sampleValid)
                    {
                        int result = CallMeasurePixel(slot, out color);
                        sampleValid = result >= 0;
                        if (!sampleValid)
                        {
                            LogSamplingError(
                                $"PixelSampling: MeasurePixel failed for point {pointIndex} with error {result}");
                        }
                    }

                    if (sampleValid)
                    {
                        // Validate color data (10-bit values should be 0-1023)
                        bool invalidColorData = color.R > 1023 || color.G > 1023 || color.B > 1023 ||
                                                color.R < 0 || color.G < 0 || color.B < 0;

                        if (invalidColorData)
                        {
                            LogSamplingError(
                                $"PixelSampling: Invalid color data at point {pointIndex}: R={color.R}, G={color.G}, B={color.B}");
                            // Treat out-of-range native values as a bad sample; clamping can turn
                            // a corrupted value into a conspicuous white flash.
                            sampleValid = false;
                        }
                    }

                    if (sampleValid)
                    {
                        _lastGoodColors[pointIndex] = color;
                        _hasLastGoodColors[pointIndex] = true;
                    }
                    else if (_hasLastGoodColors[pointIndex])
                    {
                        // Keep the previous valid sample instead of injecting a black flash.
                        color = _lastGoodColors[pointIndex];
                    }
                    else
                    {
                        continue;
                    }

                    colorData[pointIndex] = color;
                    usableSampleCount++;
                }
            }

            if (usableSampleCount == 0)
            {
                Helper.Log.Write(Helper.eLogType.Warning,
                    "PixelSampling: No valid samples available; dropping this frame");
                return null;
            }

            // On the first frame, fill any never-valid point from its nearest valid neighbor.
            // This avoids introducing black wedges when only part of a native batch fails.
            for (int pointIndex = 0; pointIndex < colorData.Length; pointIndex++)
            {
                if (_hasLastGoodColors[pointIndex])
                {
                    continue;
                }

                for (int distance = 1; distance < colorData.Length; distance++)
                {
                    int before = (pointIndex - distance + colorData.Length) % colorData.Length;
                    int after = (pointIndex + distance) % colorData.Length;
                    int neighbor = _hasLastGoodColors[before] ? before :
                        (_hasLastGoodColors[after] ? after : -1);

                    if (neighbor >= 0)
                    {
                        colorData[pointIndex] = _lastGoodColors[neighbor];
                        break;
                    }
                }
            }

            if (!_hasLoggedSampleRange)
            {
                int minR = 1023, minG = 1023, minB = 1023;
                int maxR = 0, maxG = 0, maxB = 0;
                for (int i = 0; i < colorData.Length; i++)
                {
                    minR = Math.Min(minR, colorData[i].R);
                    minG = Math.Min(minG, colorData[i].G);
                    minB = Math.Min(minB, colorData[i].B);
                    maxR = Math.Max(maxR, colorData[i].R);
                    maxG = Math.Max(maxG, colorData[i].G);
                    maxB = Math.Max(maxB, colorData[i].B);
                }

                Helper.Log.Write(Helper.eLogType.Info,
                    $"PixelSampling: First RGB sample range R={minR}..{maxR}, G={minG}..{maxG}, B={minB}..{maxB}");
                Helper.Log.Write(Helper.eLogType.Info,
                    "PixelSampling: First edge sample ranges: " +
                    FormatEdgeSampleRange(colorData, "Top", 0) + "; " +
                    FormatEdgeSampleRange(colorData, "Right", 4) + "; " +
                    FormatEdgeSampleRange(colorData, "Bottom", 8) + "; " +
                    FormatEdgeSampleRange(colorData, "Left", 12));
                _hasLoggedSampleRange = true;
            }

            return colorData;
        }

        private string FormatEdgeSampleRange(Color[] colors, string edgeName, int firstIndex)
        {
            int minR = 1023, minG = 1023, minB = 1023;
            int maxR = 0, maxG = 0, maxB = 0;

            for (int i = firstIndex; i < firstIndex + 4; i++)
            {
                minR = Math.Min(minR, colors[i].R);
                minG = Math.Min(minG, colors[i].G);
                minB = Math.Min(minB, colors[i].B);
                maxR = Math.Max(maxR, colors[i].R);
                maxG = Math.Max(maxG, colors[i].G);
                maxB = Math.Max(maxB, colors[i].B);
            }

            return $"{edgeName} R={minR}..{maxR} G={minG}..{maxG} B={minB}..{maxB}";
        }

        private void LogSamplingError(string message)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastSamplingErrorLog).TotalSeconds < 5)
            {
                return;
            }

            _lastSamplingErrorLog = now;
            Helper.Log.Write(Helper.eLogType.Warning, message);
        }

        /// <summary>
        /// Convert the 16 perimeter samples to a 64x48 synthetic image and limited-range BT.709 NV12.
        /// Each edge color is extended inward to avoid averaging sampled colors with a black center.
        /// </summary>
        private (byte[] yData, byte[] uvData) ConvertColorsToNV12(Color[] colors)
        {
            const int width = 64;
            const int height = 48;

            if (colors == null || colors.Length < _capturedPoints.Length)
            {
                throw new ArgumentException("Expected one color per configured capture point", nameof(colors));
            }

            // Allocate NV12 buffers
            byte[] yData = new byte[width * height];
            byte[] uvData = new byte[width * height / 2]; // UV plane is half the size
            byte[] rgbImage = new byte[width * height * 3]; // RGB888

            // Map each image pixel to the nearest sampled edge and interpolate along that edge.
            // This keeps LED regions colored even when HyperHDR's sampling area extends inward.
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int distanceTop = y;
                    int distanceRight = width - 1 - x;
                    int distanceBottom = height - 1 - y;
                    int distanceLeft = x;
                    Color edgeColor;

                    if (distanceTop <= distanceRight && distanceTop <= distanceBottom && distanceTop <= distanceLeft)
                    {
                        edgeColor = InterpolateEdgeColor(colors, 0, x / (float)(width - 1));
                    }
                    else if (distanceRight <= distanceBottom && distanceRight <= distanceLeft)
                    {
                        edgeColor = InterpolateEdgeColor(colors, 4, y / (float)(height - 1));
                    }
                    else if (distanceBottom <= distanceLeft)
                    {
                        edgeColor = InterpolateEdgeColor(colors, 8, 1.0f - x / (float)(width - 1));
                    }
                    else
                    {
                        edgeColor = InterpolateEdgeColor(colors, 12, 1.0f - y / (float)(height - 1));
                    }

                    int rgbIndex = (y * width + x) * 3;
                    rgbImage[rgbIndex] = ScaleTo8Bit(edgeColor.R);
                    rgbImage[rgbIndex + 1] = ScaleTo8Bit(edgeColor.G);
                    rgbImage[rgbIndex + 2] = ScaleTo8Bit(edgeColor.B);
                }
            }

            // Convert RGB to limited-range BT.709 NV12 (video-range Y=16..235, UV=16..240).
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int rgbIdx = (y * width + x) * 3;
                    byte r = rgbImage[rgbIdx + 0];
                    byte g = rgbImage[rgbIdx + 1];
                    byte b = rgbImage[rgbIdx + 2];

                    // BT.709 limited-range luma.
                    int yVal = (int)Math.Round(16.0 + 0.182586 * r + 0.614231 * g + 0.062007 * b);
                    yData[y * width + x] = (byte)Math.Max(16, Math.Min(235, yVal));
                }
            }

            // NV12 chroma is interleaved and averaged over each 2x2 RGB block.
            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    double r = 0;
                    double g = 0;
                    double b = 0;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int rgbIndex = ((y + dy) * width + x + dx) * 3;
                            r += rgbImage[rgbIndex];
                            g += rgbImage[rgbIndex + 1];
                            b += rgbImage[rgbIndex + 2];
                        }
                    }

                    r /= 4.0;
                    g /= 4.0;
                    b /= 4.0;

                    int uVal = (int)Math.Round(128.0 - 0.100644 * r - 0.338572 * g + 0.439216 * b);
                    int vVal = (int)Math.Round(128.0 + 0.439216 * r - 0.398942 * g - 0.040274 * b);
                    int uvIndex = (y / 2) * width + x;
                    uvData[uvIndex] = (byte)Math.Max(16, Math.Min(240, uVal));
                    uvData[uvIndex + 1] = (byte)Math.Max(16, Math.Min(240, vVal));
                }
            }

            return (yData, uvData);
        }

        private Color InterpolateEdgeColor(Color[] colors, int firstIndex, float position)
        {
            float segmentPosition = Math.Max(0, Math.Min(1, position)) * 3.0f;
            int segment = Math.Min(2, (int)segmentPosition);
            float blend = segmentPosition - segment;
            Color first = colors[firstIndex + segment];
            Color second = colors[firstIndex + segment + 1];

            return new Color
            {
                R = (int)Math.Round(first.R + (second.R - first.R) * blend),
                G = (int)Math.Round(first.G + (second.G - first.G) * blend),
                B = (int)Math.Round(first.B + (second.B - first.B) * blend)
            };
        }

        /// <summary>
        /// Convert 10-bit color value (0-1023) to 8-bit (0-255) using proper scaling
        /// Uses scaling rather than clamping to preserve color accuracy
        /// </summary>
        private byte ScaleTo8Bit(int value)
        {
            // Scale 10-bit (0-1023) to 8-bit (0-255)
            return (byte)Math.Max(0, Math.Min(255, value * 255 / 1023));
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
                    if (!GetCondition())
                    {
                        return CaptureResult.CreateFailure("PixelSampling: Failed to get condition");
                    }

                    // Pre-calculate all pixel coordinates once
                    PreCalculateCoordinates();

                    _isInitialized = true;
                }

                // Sample pixels from screen
                Color[] colors = GetColors();

                if (colors == null || colors.Length == 0)
                {
                    return CaptureResult.CreateFailure("PixelSampling: No colors captured");
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
            _lastGoodColors = new Color[_capturedPoints.Length];
            _hasLastGoodColors = new bool[_capturedPoints.Length];
            _hasLoggedSampleRange = false;
            _lastSamplingErrorLog = DateTime.MinValue;
            Helper.Log.Write(Helper.eLogType.Debug, "PixelSampling: Cleaned up");
        }
    }
}
