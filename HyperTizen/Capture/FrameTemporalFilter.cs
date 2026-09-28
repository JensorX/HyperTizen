using System;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Rejects isolated one-frame noise, lifts deep shadows, and smooths temporal changes in NV12.
    /// Large changes use a faster blend so scene cuts do not remain visibly stale.
    /// </summary>
    public sealed class FrameTemporalFilter
    {
        private const int FastChangeThreshold = 48;
        private const float StableAlpha = 0.14f;
        private const float FastAlpha = 0.28f;
        private const float ShadowLumaFloor = 0.50f;
        private const float LumaKneeInput = 0.50f;
        private const float LumaKneeOutput = 0.70f;

        private byte[] _previousRawY;
        private byte[] _filteredY;
        private byte[] _previousRawUv;
        private byte[] _filteredUv;
        private int _width;
        private int _height;

        public void Apply(CaptureResult frame)
        {
            if (frame == null || !frame.Success || frame.YData == null || frame.UVData == null ||
                frame.Width <= 0 || frame.Height <= 0)
            {
                return;
            }

            if (_previousRawY == null || _width != frame.Width || _height != frame.Height ||
                _previousRawY.Length != frame.YData.Length || _previousRawUv.Length != frame.UVData.Length)
            {
                Reset(frame);
                return;
            }

            FilterPlane(frame.YData, _previousRawY, _filteredY, true);
            FilterPlane(frame.UVData, _previousRawUv, _filteredUv, false);
        }

        public void Reset()
        {
            _previousRawY = null;
            _filteredY = null;
            _previousRawUv = null;
            _filteredUv = null;
            _width = 0;
            _height = 0;
        }

        private void Reset(CaptureResult frame)
        {
            _previousRawY = new byte[frame.YData.Length];
            _filteredY = new byte[frame.YData.Length];
            _previousRawUv = (byte[])frame.UVData.Clone();
            _filteredUv = (byte[])frame.UVData.Clone();

            for (int i = 0; i < frame.YData.Length; i++)
            {
                byte liftedValue = LiftShadowLuma(frame.YData[i]);
                frame.YData[i] = liftedValue;
                _previousRawY[i] = liftedValue;
                _filteredY[i] = liftedValue;
            }

            _width = frame.Width;
            _height = frame.Height;
        }

        private static void FilterPlane(byte[] current, byte[] previousRaw, byte[] filtered, bool isLuma)
        {
            for (int i = 0; i < current.Length; i++)
            {
                byte rawValue = isLuma ? LiftShadowLuma(current[i]) : current[i];
                byte filteredValue = filtered[i];
                byte stableValue = Median(rawValue, previousRaw[i], filteredValue);
                int difference = stableValue - filteredValue;
                float alpha = Math.Abs(difference) >= FastChangeThreshold ? FastAlpha : StableAlpha;
                float adjustment = difference * alpha;
                int roundedAdjustment = adjustment >= 0
                    ? (int)(adjustment + 0.5f)
                    : (int)(adjustment - 0.5f);
                int output = Math.Max(0, Math.Min(255, filteredValue + roundedAdjustment));

                previousRaw[i] = rawValue;
                filtered[i] = (byte)output;
                current[i] = (byte)output;
            }
        }

        private static byte LiftShadowLuma(byte value)
        {
            // NV12 Y is treated as limited range (16..235). Map input luma 0..50%
            // into output luma 50..70%, analogous to compressing CMYK K=50..100
            // into K=30..50. The upper half transitions continuously to white.
            float luma = Math.Max(0, Math.Min(1, (value - 16) / 219.0f));
            float liftedLuma;

            if (luma <= LumaKneeInput)
            {
                float progress = luma / LumaKneeInput;
                liftedLuma = ShadowLumaFloor +
                    (LumaKneeOutput - ShadowLumaFloor) * progress;
            }
            else
            {
                float progress = (luma - LumaKneeInput) / (1.0f - LumaKneeInput);
                liftedLuma = LumaKneeOutput + (1.0f - LumaKneeOutput) * progress;
            }

            int output = (int)Math.Round(16 + liftedLuma * 219.0f);
            return (byte)Math.Max(16, Math.Min(235, output));
        }

        private static byte Median(byte first, byte second, byte third)
        {
            if (first > second)
            {
                byte swap = first;
                first = second;
                second = swap;
            }

            if (second > third)
            {
                byte swap = second;
                second = third;
                third = swap;
            }

            return first > second ? first : second;
        }
    }
}
