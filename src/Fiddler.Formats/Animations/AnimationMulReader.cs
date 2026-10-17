using Fiddler.Formats.Containers.Mul;

namespace Fiddler.Formats.Animations;

/// <summary>Direct file/body browsing, matching Fiddler's explicit animX file mode.</summary>
public sealed class AnimationMulReader : IDisposable
{
    private readonly IndexedMulFile file;
    public int FileType { get; }
    public string FileName => file.DataFileName;
    public IReadOnlyList<AnimationBody> Bodies { get; }
    private static readonly string[] AnimalActions = ["Walk", "Run", "Idle", "Eat", "Alert", "Attack1", "Attack2", "GetHit", "Die1", "Idle", "Fidget", "LieDown", "Die2"];
    private static readonly string[] MonsterActions = ["Walk", "Idle", "Die1", "Die2", "Attack1", "Attack2", "Attack3", "AttackBow", "AttackCrossBow", "AttackThrow", "GetHit", "Pillage", "Stomp", "Cast2", "Cast3", "BlockRight", "BlockLeft", "Idle", "Fidget", "Fly", "TakeOff", "GetHitInAir"];

    private AnimationMulReader(IndexedMulFile file, int fileType)
    {
        this.file = file;
        FileType = fileType;
        var bodies = new List<AnimationBody>();
        for (int body = 0; body <= AnimationIndexLayout.MaxBodyId; body++)
        {
            int capacity = AnimationIndexLayout.ActionCapacity(body, fileType);
            if (Enumerable.Range(0, capacity).Any(action => Enumerable.Range(0, 5)
                .Any(direction => HasSequence(body, action, direction))))
                bodies.Add(new AnimationBody(body, AnimationIndexLayout.BodyType(body, fileType)));
        }
        Bodies = bodies.AsReadOnly();
    }

    public static AnimationMulReader Open(string indexPath, string dataPath, int fileType = 1)
    {
        _ = AnimationIndexLayout.ActionCapacity(0, fileType);
        var file = IndexedMulFile.Open(indexPath, dataPath);
        try { return new AnimationMulReader(file, fileType); }
        catch { file.Dispose(); throw; }
    }

    public bool HasSequence(int body, int action, int direction) =>
        file.Contains(AnimationIndexLayout.Slot(body, action, direction, FileType));

    public IReadOnlyList<AnimationAction> Actions(int body)
    {
        int count = AnimationIndexLayout.ActionCapacity(body, FileType);
        var type = AnimationIndexLayout.BodyType(body, FileType);
        var names = type == AnimationBodyType.Animal ? AnimalActions : type == AnimationBodyType.Monster ? MonsterActions : [];
        return Enumerable.Range(0, count).Where(action => Enumerable.Range(0, 5)
            .Any(direction => HasSequence(body, action, direction)))
            .Select(action => new AnimationAction(action, action < names.Length ? names[action] : $"Action {action}"))
            .ToArray();
    }

    public AnimationClip Read(int body, int action, int direction) =>
        AnimationDecoder.Decode(file.Read(AnimationIndexLayout.Slot(body, action, direction, FileType)), direction > 4);

    public void Dispose() => file.Dispose();
}
