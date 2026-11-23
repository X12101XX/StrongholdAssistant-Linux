namespace ASPAssistant.Core.Services;

/// <summary>One automatic attempt per opening screen. Missing OCR never rearms it.</summary>
public sealed class InformationScreenState
{
    private readonly object _sync = new();
    private int _gameplayFrames;
    private bool _consumed;

    public bool IsConsumed { get { lock (_sync) return _consumed; } }

    public bool Observe(bool opening, bool gameplay)
    {
        lock (_sync)
        {
            if (gameplay)
            {
                if (++_gameplayFrames >= 2) _consumed = false;
                return false;
            }
            _gameplayFrames = 0;
            if (!opening)
            {
                return false;
            }
            // 标题和两条盟约标题已在同一帧确认；滚动后不一定还有第二帧完整页。
            if (_consumed) return false;
            _consumed = true;
            return true;
        }
    }

    public void MarkRecorded()
    {
        lock (_sync)
        {
            _consumed = true;
            _gameplayFrames = 0;
        }
    }

    public void Suspend()
    {
        lock (_sync) _gameplayFrames = 0;
    }
}
