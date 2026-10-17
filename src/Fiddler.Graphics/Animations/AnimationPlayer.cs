using Fiddler.Formats.Animations;
using SkiaSharp;

namespace Fiddler.Graphics.Animations;

/// <summary>Host-driven playback clock, reusable with any Skia canvas; no UI timer or HTTP dependency.</summary>
public sealed class AnimationPlayer(AnimationClip clip)
{
    private readonly AnimationRenderer renderer = new();
    private TimeSpan delay = TimeSpan.FromMilliseconds(150);
    private long remainder;
    public int FrameIndex { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool Loop { get; set; } = true;
    public TimeSpan FrameDelay
    {
        get => delay;
        set
        {
            if (value <= TimeSpan.Zero || value > TimeSpan.FromSeconds(10))
                throw new ArgumentOutOfRangeException(nameof(value));
            delay = value;
            remainder = 0;
        }
    }

    public void Play() => IsPlaying = true;
    public void Pause() => IsPlaying = false;
    public void Seek(int frame)
    {
        if (frame < 0 || frame >= clip.Frames.Count) throw new ArgumentOutOfRangeException(nameof(frame));
        FrameIndex = frame;
        remainder = 0;
    }

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (!IsPlaying) return;
        long steps = elapsed.Ticks / delay.Ticks;
        remainder += elapsed.Ticks % delay.Ticks;
        steps += remainder / delay.Ticks;
        remainder %= delay.Ticks;
        if (Loop) FrameIndex = (int)((FrameIndex + steps % clip.Frames.Count) % clip.Frames.Count);
        else if (steps >= clip.Frames.Count - 1 - FrameIndex)
        {
            FrameIndex = clip.Frames.Count - 1;
            IsPlaying = false;
        }
        else FrameIndex += (int)steps;
    }

    public void Draw(SKCanvas canvas, float anchorX, float anchorY, float scale = 1) =>
        renderer.DrawFrame(canvas, clip, FrameIndex, anchorX, anchorY, scale);
}
