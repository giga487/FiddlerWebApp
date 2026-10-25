using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UOAS.Contracts;

namespace UOAS.Application.Regions;

/// <summary>Static ModernUO definitions. Unknown type-specific fields remain in the original JSON object.</summary>
public static class RegionDocument
{
    public static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 32 };
    public static readonly string[] Types = ["Region", "BaseRegion", "TownRegion", "DungeonRegion", "VisibilityRegion", "GuardedRegion", "GreenAcresRegion", "JailRegion", "MondainRegion", "NewMaginciaRegion", "NoHousingRegion", "NoHousingGuardedRegion", "NoTravelSpellsAllowedRegion", "TwistedWealdDesertRegion", "CrystalFieldRegion", "IcyRiverRegion", "AcidRiverRegion", "PoisonedTreeRegion", "LostCityEntranceRegion", "PoisonedCemeteryRegion", "WrongLevel3Region", "WrongJailRegion", "BlackthornDungeonRegion", "CousteauPerronHouseRegion", "ApprenticeRegion", "SeaMarketRegion", "ExploringDeepCreaturesRegion", "UnderwaterRegion", "ExodusDungeonRegion", "DoomGuardianRegion", "TokunoDocksRegion", "TombOfKingsRegion", "ToKBridgeRegion", "BattleRegion"];
    public static string Text(JsonElement value, string key, string fallback = "") => value.TryGetProperty(key, out var field) && field.ValueKind != JsonValueKind.Null ? field.ToString() : fallback;
    public static int Number(JsonElement value, string key, int fallback = 0) => value.TryGetProperty(key, out var field) ? field.GetInt32() : fallback;
    public static string Identity(JsonElement value) => Text(value, "Map") + "\0" + Text(value, "Name");
    public static string? Parent(JsonElement value) => value.TryGetProperty("Parent", out var parent) && parent.ValueKind == JsonValueKind.Object ? Identity(parent) : null;
    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in value.EnumerateObject()) { if (!keys.Add(field.Name)) throw new ArgumentException("Duplicate region property: " + field.Name); UniqueProperties(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) UniqueProperties(item);
    }
    private static int[] Point(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) { if (!Regex.IsMatch(value.GetString()!, @"^\s*\(\s*-?\d+\s*,\s*-?\d+\s*,\s*-?\d+\s*\)\s*$")) throw new ArgumentException("Invalid point coordinate string."); return Coordinates(value.GetString()!, 3); }
        if (value.ValueKind == JsonValueKind.Array) { var values = value.EnumerateArray().Select(item => item.GetInt32()).ToArray(); if (values.Length > 3) throw new ArgumentException("A point has at most three coordinates."); return [..values, ..Enumerable.Repeat(0, 3 - values.Length)]; }
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Provide a point object, array or coordinate string.");
        if (value.EnumerateObject().Any(field => field.Name is not ("x" or "y" or "z"))) throw new ArgumentException("A point object accepts x, y and z.");
        return [Number(value, "x"), Number(value, "y"), Number(value, "z")];
    }
    private static int[] Coordinates(string value, int count)
    {
        var numbers = Regex.Matches(value, @"-?\d+").Select(match => int.Parse(match.Value)).ToArray();
        if (numbers.Length != count) throw new ArgumentException("Invalid coordinate string."); return numbers;
    }
    public static RegionVolume Volume(JsonElement value)
    {
        int[] v;
        if (value.ValueKind == JsonValueKind.Array)
        {
            v = value.EnumerateArray().Select(item => item.GetInt32()).ToArray();
            if (v.Length > 6) throw new ArgumentException("A volume has at most six coordinates.");
            v = [..v, ..Enumerable.Repeat(0, 6 - v.Length)];
            return new(v[0], v[1], v[2], checked(v[0] + v[3]), checked(v[1] + v[4]), checked(v[2] + v[5]));
        }
        if (value.ValueKind == JsonValueKind.String) { string[] points = value.GetString()!.Split('+'); if (points.Length != 2) throw new ArgumentException("A rectangle string requires two points separated by +."); _ = Point(JsonSerializer.SerializeToElement(points[0])); _ = Point(JsonSerializer.SerializeToElement(points[1])); v = Coordinates(value.GetString()!, 6); return new(v[0], v[1], v[2], v[3], v[4], v[5]); }
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Provide a region volume.");
        var keys = value.EnumerateObject().Select(field => field.Name).ToArray();
        string[][] groups = [["start", "end"], ["x1", "y1", "z1", "x2", "y2", "z2"], ["x", "y", "z", "w", "h", "d", "width", "height", "depth"]];
        if (!groups.Any(group => keys.All(key => group.Contains(key)))) throw new ArgumentException("Use one rectangle encoding: start/end, x1/y1/z1/x2/y2/z2, or x/y/z/width/height/depth.");
        if (keys.Contains("start") || keys.Contains("end"))
        {
            var a = value.TryGetProperty("start", out var start) ? Point(start) : [0, 0, 0]; var b = value.TryGetProperty("end", out var end) ? Point(end) : [0, 0, 0];
            // The server's start/end converter uses its implicit vertical range.
            return new(a[0], a[1], -128, b[0], b[1], 127);
        }
        bool z = value.TryGetProperty("z", out _) || value.TryGetProperty("z1", out _) || value.TryGetProperty("z2", out _);
        if (value.TryGetProperty("x1", out _) || value.TryGetProperty("x2", out _))
            return new(Number(value, "x1"), Number(value, "y1"), z ? Number(value, "z1") : -128, Number(value, "x2"), Number(value, "y2"), z ? Number(value, "z2") : 127);
        int x = Number(value, "x"), y = Number(value, "y"), bottom = z ? Number(value, "z") : -128;
        return new(x, y, bottom, checked(x + Number(value, "w", Number(value, "width"))), checked(y + Number(value, "h", Number(value, "height"))), checked(bottom + (z ? Number(value, "d", Number(value, "depth")) : 127)));
    }
    public static IReadOnlyList<RegionVolume> Areas(JsonElement value) => value.GetProperty("Area").EnumerateArray().Select(Volume).ToArray();
    public static void Validate(JsonElement value, IReadOnlyCollection<string>? maps = null)
    {
        if (value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > 256 * 1024) throw new ArgumentException("Provide a region object below 256 KiB.");
        UniqueProperties(value);
        foreach (string field in new[] { "Map" })
            if (!value.TryGetProperty(field, out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString()) || text.GetString()!.Length > 200 || text.GetString()!.Any(char.IsControl)) throw new ArgumentException($"Provide a {field} from 1 to 200 characters.");
        if (value.TryGetProperty("Name", out var regionName) && regionName.ValueKind != JsonValueKind.Null && (regionName.ValueKind != JsonValueKind.String || regionName.GetString()!.Length > 200 || regionName.GetString()!.Any(char.IsControl))) throw new ArgumentException("Region names must be strings of at most 200 characters, or null for unnamed regions.");
        if (maps is not null && !maps.Contains(Text(value, "Map"))) throw new ArgumentException("Select a map from this server source.");
        if (value.TryGetProperty("$type", out var type) && (type.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(type.GetString()))) throw new ArgumentException("Provide a region type name.");
        _ = Number(value, "Priority");
        if (!value.TryGetProperty("Area", out var area) || area.ValueKind != JsonValueKind.Array || area.GetArrayLength() is < 1 or > 512) throw new ArgumentException("Provide from 1 to 512 region volumes.");
        foreach (var volume in Areas(value)) if (volume.X1 < 0 || volume.Y1 < 0 || volume.X2 > 1_000_000 || volume.Y2 > 1_000_000 || volume.X2 <= volume.X1 || volume.Y2 <= volume.Y1 || volume.Z2 <= volume.Z1) throw new ArgumentException("Volumes require increasing bounds and nonnegative X/Y coordinates.");
        foreach (string field in new[] { "GoLocation", "Entrance" }) if (value.TryGetProperty(field, out var point)) _ = Point(point);
        if (value.TryGetProperty("Parent", out var parent) && parent.ValueKind != JsonValueKind.Null)
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String || !parent.TryGetProperty("Map", out var map) || map.ValueKind != JsonValueKind.String) throw new ArgumentException("A parent requires its Name and Map.");
        if (value.TryGetProperty("Visibility3DMode", out var mode) && mode.GetString() is not ("Inherit" or "Enabled" or "Disabled")) throw new ArgumentException("Choose Inherit, Enabled or Disabled visibility.");
        if (value.TryGetProperty("Visibility3DRadius", out var radius) && radius.GetInt32() is < 0 or > 24) throw new ArgumentException("Visibility radius must be from 0 (inherit) to 24.");
        foreach (string field in new[] { "Visibility3DCheckLineOfSight", "GuardsDisabled", "NoLogoutDelay", "ExcludeFromParentSpawns" })
            if (value.TryGetProperty(field, out var boolean) && boolean.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException(field + " must be a boolean.");
        int Expansion(string field, int fallback)
        {
            if (!value.TryGetProperty(field, out var expansion)) return fallback;
            int index = expansion.ValueKind == JsonValueKind.Number ? expansion.GetInt32() : Array.FindIndex(RegionSchema.Expansions, name => name.Equals(expansion.GetString(), StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index >= RegionSchema.Expansions.Length) throw new ArgumentException("Unknown expansion: " + expansion); return index;
        }
        if (Expansion("MinExpansion", 0) > Expansion("MaxExpansion", 11)) throw new ArgumentException("Minimum expansion must not exceed maximum expansion.");
        if (value.TryGetProperty("Music", out var music) && music.ValueKind != JsonValueKind.Number && !RegionSchema.Music.Contains(music.GetString(), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Unknown server music name.");
    }
    public static JsonElement[] Parse(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 })) bytes = bytes[3..];
        using var doc = JsonDocument.Parse(bytes, ReadOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() > 10000) throw new ArgumentException("Provide an array of at most 10,000 server regions.");
        var rows = doc.RootElement.EnumerateArray().Select(value => value.Clone()).ToArray(); foreach (var value in rows) Validate(value); Ordered(rows); return rows;
    }
    public static JsonElement[] Ordered(IEnumerable<JsonElement> records)
    {
        var all = records.ToArray();
        var named = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < all.Length; i++) if (Text(all[i], "Name").Length > 0 && !named.TryAdd(Identity(all[i]), i)) throw new InvalidOperationException("Region names must be unique within their map: " + Text(all[i], "Name"));
        var seen = new HashSet<int>(); var pending = new HashSet<int>(); var ordered = new List<JsonElement>();
        void Visit(int key)
        {
            if (seen.Contains(key)) return;
            if (!pending.Add(key)) throw new InvalidOperationException("The region parent hierarchy contains a cycle.");
            if (pending.Count > 128) throw new ArgumentException("Region hierarchy depth exceeds 128 levels.");
            var value = all[key];
            if (Parent(value) is { } parent) { if (!named.TryGetValue(parent, out int parentIndex)) throw new InvalidOperationException("Parent region not found for " + Text(value, "Name") + ". Rename or remove dependent children in the same publication."); Visit(parentIndex); }
            pending.Remove(key); seen.Add(key); ordered.Add(value);
        }
        for (int i = 0; i < all.Length; i++) Visit(i); return ordered.ToArray();
    }
    public static byte[] Export(IEnumerable<JsonElement> records)
    {
        var array = new JsonArray();
        foreach (var record in Ordered(records))
        {
            var canonical = new JsonObject();
            if (record.TryGetProperty("$type", out var type)) canonical["$type"] = type.GetString();
            foreach (var field in record.EnumerateObject()) if (field.Name != "$type") canonical[field.Name] = JsonNode.Parse(field.Value.GetRawText());
            array.Add(canonical);
        }
        return JsonSerializer.SerializeToUtf8Bytes(array, new JsonSerializerOptions { WriteIndented = true });
    }
}
