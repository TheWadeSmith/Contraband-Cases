using ContrabandCases.Shared.Catalog;
using UnityEngine;

namespace ContrabandCases.Client.UI;

/// <summary>Small built-in shipping seals; always available without item bundles.</summary>
internal static class LootArtwork
{
    private static readonly Dictionary<RewardRarity, Sprite> Seals = new();

    public static Sprite Seal(RewardRarity grade)
    {
        if (Seals.TryGetValue(grade, out var existing) && existing != null)
            return existing;
        const int size = 128;
        var pixels = new Color[size * size];
        var color = UiFactory.RarityColor(grade);
        void Line(int x1, int y1, int x2, int y2)
        {
            var steps = Math.Max(Math.Abs(x2 - x1), Math.Abs(y2 - y1));
            for (var i = 0; i <= steps; i++)
            {
                var t = steps == 0 ? 0 : (float)i / steps;
                var x = (int)Mathf.Lerp(x1, x2, t);
                var y = (int)Mathf.Lerp(y1, y2, t);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                        if (x + dx >= 0 && x + dx < size && y + dy >= 0 && y + dy < size)
                            pixels[(y + dy) * size + x + dx] = color;
            }
        }
        // Isometric sealed crate with two straps and corner registration marks.
        Line(22, 77, 64, 99); Line(64, 99, 106, 77); Line(106, 77, 64, 54); Line(64, 54, 22, 77);
        Line(22, 77, 22, 37); Line(22, 37, 64, 15); Line(64, 15, 106, 37); Line(106, 37, 106, 77);
        Line(64, 54, 64, 15); Line(43, 88, 85, 65); Line(85, 65, 85, 26);
        Line(43, 65, 85, 88); Line(43, 65, 43, 26);
        Line(7, 107, 7, 119); Line(7, 119, 19, 119); Line(109, 119, 121, 119); Line(121, 119, 121, 107);
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "BR12 built-in seal", filterMode = FilterMode.Bilinear };
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        Seals[grade] = sprite;
        return sprite;
    }

    public static void Clear()
    {
        foreach (var sprite in Seals.Values)
        {
            if (sprite == null) continue;
            UnityEngine.Object.Destroy(sprite.texture);
            UnityEngine.Object.Destroy(sprite);
        }
        Seals.Clear();
    }
}
