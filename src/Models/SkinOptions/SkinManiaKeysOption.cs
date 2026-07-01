namespace OsuSkinMixer.Models;

/// <summary>Represents an entire osu!mania skin.ini section and its referenced assets for a specific key count.</summary>
public class SkinManiaKeysOption : SkinOption
{
    public SkinManiaKeysOption(int keys)
    {
        Keys = keys;
    }

    public int Keys { get; }

    public override string Name => $"{Keys}K";

    public override string ToString() => $"[skin.ini]\n[Mania]\nKeys: {Keys}";
}
