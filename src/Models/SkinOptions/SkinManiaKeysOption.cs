namespace OsuSkinMixer.Models;

/// <summary>Represents an entire osu!mania skin.ini section and its referenced assets for a specific key count.</summary>
public class SkinManiaKeysOption : SkinOption
{
    public SkinManiaKeysOption(int keys)
    {
        Keys = keys;
    }

    public int Keys { get; set; }

    public override string Name => $"{Keys}K";

    public override string ToString() => $"Copy entire osu!mania section and referenced files where:\n\n[Mania]\nKeys: {Keys}";
}
