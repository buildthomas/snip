namespace Snip;

/// Esc closes right away when nothing is drawn; with annotations it needs a second press within the window.
public sealed class EscapeGuard
{
    public const long WindowMs = 1500;
    private long? _armedAt;

    public bool ShouldClose(bool hasAnnotations, long nowMs)
    {
        if (!hasAnnotations) { _armedAt = null; return true; }
        if (_armedAt is long t && nowMs - t <= WindowMs) { _armedAt = null; return true; }
        _armedAt = nowMs;
        return false;
    }
}
