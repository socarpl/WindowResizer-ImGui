using System.Diagnostics;
using System.Numerics;

namespace WindowResizer;

public sealed class DoubleClickTracker<T> where T : notnull
{
    private T? _lastItem;
    private Vector2 _lastPosition;
    private long _lastTick;
    private bool _hasPrevious;

    public bool Register(T item, Vector2 position, long tick, uint intervalMilliseconds)
    {
        bool doubleClick = _hasPrevious && EqualityComparer<T>.Default.Equals(_lastItem!, item)
            && tick >= _lastTick
            && (tick - _lastTick) * 1000.0 / Stopwatch.Frequency <= intervalMilliseconds
            && Vector2.DistanceSquared(position, _lastPosition) <= 144;

        _hasPrevious = !doubleClick;
        _lastItem = item;
        _lastPosition = position;
        _lastTick = tick;
        return doubleClick;
    }
}
