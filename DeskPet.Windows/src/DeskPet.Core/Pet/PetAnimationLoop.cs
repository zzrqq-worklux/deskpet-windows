namespace DeskPet.Core.Pet;

public sealed class PetAnimationLoop
{
    public double Fps { get; set; }
    public bool IsLoop { get; set; }
    public int TotalFrames { get; set; }
    public int CachedCount { get; set; }
    public int LoopPoint { get; set; }
    public int? CurrentIndex { get; private set; }
    public bool OneShotFinished { get; private set; }

    private double _frameProgress;
    private int _lastIndex = -1;

    public void Reset()
    {
        _frameProgress = 0;
        _lastIndex = -1;
        CurrentIndex = null;
        OneShotFinished = false;
    }

    public bool Advance(double tick)
    {
        if (CachedCount <= 0)
        {
            CurrentIndex = null;
            return false;
        }

        int idx;
        if (IsLoop)
        {
            // 无缝循环：仅当全量缓存且已播完第一轮后才启用（Swift PetManager.swift:387-388）。
            // LoopPoint < CachedCount 是防御性守卫，避免 CachedCount - LoopPoint 除零。
            if (CachedCount >= TotalFrames && LoopPoint > 0 && LoopPoint < CachedCount
                && _frameProgress >= CachedCount)
            {
                idx = LoopPoint + (int)(_frameProgress - CachedCount) % (CachedCount - LoopPoint);
            }
            else
            {
                idx = (int)_frameProgress % CachedCount;
            }
        }
        else
        {
            idx = Math.Min((int)_frameProgress, CachedCount - 1);
            // 播完用 TotalFrames（素材全量）判断，显示索引用 CachedCount（已缓存）钳制
            if (TotalFrames > 0 && (int)_frameProgress >= TotalFrames - 1)
            {
                OneShotFinished = true;
            }
        }

        _frameProgress += tick * Fps;

        if (idx != _lastIndex)
        {
            _lastIndex = idx;
            CurrentIndex = idx;
            return true;
        }
        return false;
    }
}
