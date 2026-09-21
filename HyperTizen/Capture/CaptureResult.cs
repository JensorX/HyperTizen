using System;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Result of a screen capture operation
    /// Contains NV12 image data (Y and UV planes) or error information
    /// </summary>
    public class CaptureResult
    {
        /// <summary>
        /// True if capture succeeded, false if it failed
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Y plane data (luminance) in NV12 format
        /// Size = StrideY * Height bytes
        /// </summary>
        public byte[] YData { get; set; }

        /// <summary>
        /// UV plane data (chrominance) in NV12 format
        /// Interleaved U and V values
        /// Size = StrideUV * (Height / 2) bytes
        /// </summary>
        public byte[] UVData { get; set; }

        /// <summary>
        /// Actual width of captured frame
        /// May differ from requested width
        /// </summary>
        public int Width { get; set; }

        /// <summary>
        /// Actual height of captured frame
        /// May differ from requested height
        /// </summary>
        public int Height { get; set; }

        /// <summary>
        /// Error message if capture failed (Success = false)
        /// Null or empty if capture succeeded
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Bytes between adjacent rows in the Y plane.
        /// </summary>
        public int StrideY { get; set; }

        /// <summary>
        /// Bytes between adjacent rows in the interleaved UV plane.
        /// </summary>
        public int StrideUV { get; set; }

        /// <summary>
        /// Native pixel format. Current transport format is NV12.
        /// </summary>
        public string PixelFormat { get; set; }

        /// <summary>
        /// Native API return code. Zero for managed/fallback failures.
        /// </summary>
        public int NativeErrorCode { get; set; }

        /// <summary>
        /// Name of the method that produced the result.
        /// </summary>
        public string CaptureMethod { get; set; }

        /// <summary>
        /// Create a successful capture result
        /// </summary>
        public static CaptureResult CreateSuccess(byte[] yData, byte[] uvData, int width, int height,
            int strideY = 0, int strideUV = 0, string pixelFormat = "NV12",
            int nativeErrorCode = 0, string captureMethod = null)
        {
            return new CaptureResult
            {
                Success = true,
                YData = yData,
                UVData = uvData,
                Width = width,
                Height = height,
                StrideY = strideY > 0 ? strideY : width,
                StrideUV = strideUV > 0 ? strideUV : width,
                PixelFormat = pixelFormat,
                NativeErrorCode = nativeErrorCode,
                CaptureMethod = captureMethod,
                ErrorMessage = null
            };
        }

        /// <summary>
        /// Create a failed capture result
        /// </summary>
        public static CaptureResult CreateFailure(string errorMessage, int nativeErrorCode = 0,
            string captureMethod = null)
        {
            return new CaptureResult
            {
                Success = false,
                YData = null,
                UVData = null,
                Width = 0,
                Height = 0,
                NativeErrorCode = nativeErrorCode,
                CaptureMethod = captureMethod,
                ErrorMessage = errorMessage
            };
        }
    }
}
