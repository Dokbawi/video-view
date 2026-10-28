namespace VideoView.Platform.Video;

/// <summary>Fits changing capture dimensions into the encoder's fixed canvas. Single consumer.</summary>
public sealed class FrameCanvas(int width, int height)
{
    private VideoFrame? _source;
    private byte[]? _pixels;
    private int _mappedWidth, _mappedHeight, _fitWidth, _fitHeight, _left, _top;
    private (int Low, int High, int Weight)[] _x = [], _y = [];

    public byte[] Fit(VideoFrame frame)
    {
        if (width <= 0 || height <= 0 || frame.Width <= 0 || frame.Height <= 0 ||
            frame.Pixels.Length != checked(frame.Width * frame.Height * 4))
            throw new ArgumentException("Invalid BGRA frame dimensions.");
        if (frame.Width == width && frame.Height == height) return frame.Pixels;
        if (ReferenceEquals(frame, _source)) return _pixels!;
        _pixels ??= new byte[checked(width * height * 4)];
        if (_mappedWidth != frame.Width || _mappedHeight != frame.Height)
        {
            var scale = Math.Min((double)width / frame.Width, (double)height / frame.Height);
            _fitWidth = Math.Max(1, Math.Min(width, (int)Math.Round(frame.Width * scale)));
            _fitHeight = Math.Max(1, Math.Min(height, (int)Math.Round(frame.Height * scale)));
            _left = (width - _fitWidth) / 2; _top = (height - _fitHeight) / 2;
            _x = Map(frame.Width, _fitWidth); _y = Map(frame.Height, _fitHeight);
            Array.Clear(_pixels);
            for (var i = 3; i < _pixels.Length; i += 4) _pixels[i] = 255;
            (_mappedWidth, _mappedHeight) = (frame.Width, frame.Height);
        }
        var input = frame.Pixels;
        // Fixed-point bilinear interpolation; reusable maps and output avoid per-frame allocations.
        for (var y = 0; y < _fitHeight; y++)
        {
            var (y0, y1, wy) = _y[y];
            var row0 = y0 * frame.Width * 4; var row1 = y1 * frame.Width * 4;
            var destination = ((_top + y) * width + _left) * 4;
            for (var x = 0; x < _fitWidth; x++)
            {
                var (x0, x1, wx) = _x[x];
                for (var channel = 0; channel < 3; channel++)
                {
                    var a = input[row0 + x0 * 4 + channel] * (256 - wx) + input[row0 + x1 * 4 + channel] * wx;
                    var b = input[row1 + x0 * 4 + channel] * (256 - wx) + input[row1 + x1 * 4 + channel] * wx;
                    _pixels[destination + channel] = (byte)((a * (256 - wy) + b * wy + 32768) >> 16);
                }
                destination += 4;
            }
        }
        _source = frame;
        return _pixels;
    }

    private static (int, int, int)[] Map(int source, int destination) => Enumerable.Range(0, destination)
        .Select(index =>
        {
            var position = Math.Clamp((index + 0.5) * source / destination - 0.5, 0, source - 1d);
            var low = (int)position;
            return (low, Math.Min(low + 1, source - 1), (int)((position - low) * 256));
        }).ToArray();
}
