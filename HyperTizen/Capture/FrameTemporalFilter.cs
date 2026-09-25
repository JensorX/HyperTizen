using System;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Rejects isolated one-frame noise and smooths small temporal changes in NV12 planes.
    /// Large changes use a faster blend so scene cuts do not remain visibly stale.
    /// </summary>
    public sealed class FrameTemporalFilter
    {
        private const int FastChangeThreshold = 48;
        private const float StableAlpha = 0.22f;
        private const float FastAlpha = 0.55f;

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

            FilterPlane(frame.YData, _previousRawY, _filteredY);
            FilterPlane(frame.UVData, _previousRawUv, _filteredUv);
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
            _previousRawY = (byte[])frame.YData.Clone();
            _filteredY = (byte[])frame.YData.Clone();
            _previousRawUv = (byte[])frame.UVData.Clone();
            _filteredUv = (byte[])frame.UVData.Clone();
            _width = frame.Width;
            _height = frame.Height;
        }

        private static void FilterPlane(byte[] current, byte[] previousRaw, byte[] filtered)
        {
            for (int i = 0; i < current.Length; i++)
            {
                byte rawValue = current[i];
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
