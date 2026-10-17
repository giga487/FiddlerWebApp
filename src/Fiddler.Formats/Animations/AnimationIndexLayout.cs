namespace Fiddler.Formats.Animations;

/// <summary>Physical IDX layout from Ultima.Animations.GetFileIndex/GetActionCapacity.</summary>
public static class AnimationIndexLayout
{
    public const int MaxBodyId = 2048;

    public static int ActionCapacity(int body, int fileType)
    {
        Validate(body, fileType);
        return fileType switch
        {
            2 => body < 200 ? 22 : 13,
            3 => body < 300 ? 13 : body < 400 ? 22 : 35,
            5 => body < 200 && body != 34 ? 22 : body < 400 ? 13 : 35,
            _ => body < 200 ? 22 : body < 400 ? 13 : 35
        };
    }

    public static int Slot(int body, int action, int direction, int fileType)
    {
        int capacity = ActionCapacity(body, fileType);
        if (action < 0 || action >= capacity) throw new ArgumentOutOfRangeException(nameof(action));
        if (direction < 0 || direction > 7) throw new ArgumentOutOfRangeException(nameof(direction));
        int start = fileType switch
        {
            2 => body < 200 ? body * 110 : 22000 + (body - 200) * 65,
            3 => body < 300 ? body * 65 : body < 400 ? 33000 + (body - 300) * 110 : 35000 + (body - 400) * 175,
            5 when body == 34 => 22000 + (body - 200) * 65,
            _ => body < 200 ? body * 110 : body < 400 ? 22000 + (body - 200) * 65 : 35000 + (body - 400) * 175
        };
        return start + action * 5 + (direction <= 4 ? direction : 8 - direction);
    }

    public static AnimationBodyType BodyType(int body, int fileType)
    {
        Validate(body, fileType);
        return fileType switch
        {
            2 => body < 200 ? AnimationBodyType.Monster : AnimationBodyType.Animal,
            3 => body < 300 ? AnimationBodyType.Animal : body < 400 ? AnimationBodyType.Monster : AnimationBodyType.Human,
            _ => body < 200 ? AnimationBodyType.Monster : body < 400 ? AnimationBodyType.Animal : AnimationBodyType.Human
        };
    }

    private static void Validate(int body, int fileType)
    {
        if (body < 0 || body > MaxBodyId) throw new ArgumentOutOfRangeException(nameof(body));
        if (fileType < 1 || fileType > 6) throw new ArgumentOutOfRangeException(nameof(fileType));
    }
}
