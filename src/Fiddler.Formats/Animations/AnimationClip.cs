namespace Fiddler.Formats.Animations;

/// <summary>The center is the UO anchor: image top-left is (-CenterX, -CenterY - Height).</summary>
public sealed record AnimationFrame(DecodedImage? Image, int CenterX, int CenterY);

public sealed class AnimationClip
{
    public IReadOnlyList<AnimationFrame> Frames { get; }
    public bool Mirrored { get; }

    internal AnimationClip(AnimationFrame[] frames, bool mirrored)
    {
        Frames = Array.AsReadOnly(frames);
        Mirrored = mirrored;
    }
}

public enum AnimationBodyType { Monster, Animal, Human }
public sealed record AnimationBody(int Id, AnimationBodyType Type);
public sealed record AnimationAction(int Id, string Name);
