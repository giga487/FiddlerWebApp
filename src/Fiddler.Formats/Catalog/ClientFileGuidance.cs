namespace Fiddler.Formats.Catalog;

public enum ClientFileImportance { RequiredForFeature, Optional, NotUsedByViewer, CoveredByAlternative }

/// <summary>Requirements refer to this viewer's features, not to a universally complete game installation.</summary>
public sealed record ClientFileGuidance(ClientFileImportance Importance, string Feature, string Purpose, string Impact)
{
    public static ClientFileGuidance Describe(ClientGroupResult group, ClientFileResult file)
    {
        string name = file.Name;
        string feature = group.Definition.Name;
        bool animation = feature.StartsWith("Animations ", StringComparison.Ordinal);
        string purpose = PurposeFor(group.Definition, name);
        bool usableAlternative = group.ActiveAlternative is not null && !group.ActiveAlternative.Contains(name) &&
            (group.CanView || !group.Definition.ViewerSupported);
        if (usableAlternative)
            return new(ClientFileImportance.CoveredByAlternative, feature, purpose,
                $"No additional file is needed for {feature}: {string.Join(" + ", group.ActiveAlternative!)} already provides this resource.");

        if (animation && name.EndsWith(".uop", StringComparison.Ordinal))
            return new(ClientFileImportance.NotUsedByViewer, feature, purpose,
                "Animation UOP playback is not implemented. The web viewer requires the matching anim MUL/IDX pair instead.");
        if (group.Definition.ViewerSupported)
        {
            string alternatives = string.Join(" or ", group.Definition.Alternatives.Select(a => string.Join(" + ", a)));
            string impact = animation
                ? $"Without the complete MUL/IDX pair, bodies in {feature} cannot be played. Other animation archives remain independent; UOP does not replace MUL playback in this viewer."
                : feature == "Land tiles and items"
                    ? "The Art catalog requires art.mul + artidx.mul. Multi and map scene images require either that pair or artlegacymul.uop. Gump and animation playback are independent."
                    : $"Without a complete supported archive, {feature} cannot be viewed. Provide {alternatives}; these are alternatives, not files that must all be installed.";
            if (group.Status == ClientGroupStatus.Available && group.CanView)
                impact = $"The selected archive is available for {feature}.";
            return new(ClientFileImportance.RequiredForFeature, feature, purpose, impact);
        }
        if (feature.StartsWith("Map statics ", StringComparison.Ordinal))
            return new(ClientFileImportance.Optional, feature, purpose,
                "Terrain can still be viewed. Without both staidx and statics files for this facet, map previews omit static objects such as walls and buildings.");
        return new(ClientFileImportance.NotUsedByViewer, feature, purpose,
            group.Definition.Category == "Optional patches" || name == "verdata.mul"
                ? "Optional legacy patch data. The current viewer does not apply these patches; previews use the base archives. This may differ from a client that applies them."
                : "Not required by the current Gump, animation, Multi or map viewers. This resource is not consumed by the implemented viewers; its absence does not block them.");
    }

    private static string PurposeFor(ClientFileGroup group, string name)
    {
        bool index = name.EndsWith(".idx", StringComparison.Ordinal) || name is "artidx.mul" or "gumpidx.mul" or "texidx.mul" or "lightidx.mul" or "soundidx.mul";
        if (group.Name == "Gump") return index ? "Index of Gump image locations and dimensions in gumpart.mul." : "Gump images: interface backgrounds, buttons and other UI pieces.";
        if (group.Name == "Land tiles and items") return index ? "Index locating land and item graphics in art.mul." : "Land and item images used by the Art catalog and isometric Multi/map previews.";
        if (group.Name == "Multi") return index ? "Index locating structures in multi.mul." : "Multi definitions: item IDs, relative X/Y/Z positions and visibility flags for structures.";
        if (group.Name.StartsWith("Animations ", StringComparison.Ordinal)) return index ? "Index locating body/action/direction sequences in the matching anim MUL archive." : "Body animation frames; each numbered archive contains its own set of sequences.";
        if (group.Name.StartsWith("Map statics ", StringComparison.Ordinal)) return name.StartsWith("staidx", StringComparison.Ordinal) ? "Index locating static objects for this map facet." : "Placed static objects, including item IDs, coordinates, heights and hues for this facet.";
        if (group.Name.StartsWith("Map patches ", StringComparison.Ordinal)) return name.StartsWith("mapdifl", StringComparison.Ordinal) ? "List of terrain blocks replaced by a legacy map patch." : "Replacement terrain blocks for a legacy map patch.";
        if (group.Name.StartsWith("Static patches ", StringComparison.Ordinal)) return name.StartsWith("stadifl", StringComparison.Ordinal) ? "List of map blocks affected by a legacy statics patch." : name.StartsWith("stadifi", StringComparison.Ordinal) ? "Index into replacement static-object records." : "Replacement static objects for a legacy map patch.";
        if (group.Name.StartsWith("Map ", StringComparison.Ordinal)) return "Base terrain blocks, land tile IDs and elevations for this map facet. Only needed to inspect this particular map.";
        if (group.Name == "Textures") return index ? "Index into terrain texture images." : "Terrain texture images used for textured terrain rendering.";
        if (group.Name == "Lights") return index ? "Index into light-shape images." : "Light-shape images for lighting effects.";
        if (group.Name == "Sounds") return index ? "Index locating sound records." : "Sound effects archive.";
        if (group.Name == "Skills") return index ? "Index locating skill records." : "Skill names and related skill data.";
        if (name.StartsWith("cliloc", StringComparison.Ordinal)) return "Localized game strings. Only the languages or custom string sets used by the client are relevant.";
        if (name.StartsWith("unifont", StringComparison.Ordinal)) return "Unicode font glyphs. Different numbered files provide different fonts.";
        if (name.StartsWith("facet", StringComparison.Ordinal)) return "Pre-rendered facet overview data; distinct from the terrain blocks in mapN.mul.";
        return name switch
        {
            "animdata.mul" => "Frame sequences and timing for animated item Art; distinct from body animation frames in anim.mul.",
            "animationsequence.uop" => "Sequence metadata for UOP body animations.",
            "body.def" => "Body-ID remapping definitions. The current animation viewer uses physical archive IDs.",
            "bodyconv.def" => "Body conversions between animation archives. The current viewer selects physical archives directly.",
            "equipconv.def" => "Equipment animation conversion definitions for body/equipment combinations.",
            "mobtypes.txt" => "Body type classifications for animation selection.",
            "gump.def" => "Gump-ID replacement definitions. The current viewer reads raw archive IDs.",
            "sound.def" => "Sound-ID replacement definitions.",
            "client.exe" or "uotd.exe" => "Game client executable. Fiddler Web App reads data directly and does not launch the game client.",
            "fonts.mul" => "Legacy font glyphs for game text.",
            "hues.mul" => "Color palettes for applying game hues. Current previews show base colors without hue application.",
            "multimap.rle" => "Compressed map overview image data, rather than individual terrain blocks.",
            "radarcol.mul" => "Land/item colors used for radar and minimap rendering.",
            "skillgrp.mul" => "Grouping of skills into categories.",
            "speech.mul" => "Speech keyword data used by the game client.",
            "tiledata.mul" => "Land/item names, flags and properties. Current previews decode Art and coordinates without tile metadata.",
            "verdata.mul" => "Legacy replacement records that patch base client archives.",
            _ => throw new InvalidOperationException($"No file guidance is defined for {name}.")
        };
    }
}
