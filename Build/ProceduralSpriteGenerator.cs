using System;
using System.Collections.Generic;
using System.IO;

namespace Build;

public enum SpriteKind
{
    Character,
    Enemy,
    Item,
    Coin,
    Tile,
    Background,
    Projectile,
    Ui,
    Generic
}

public sealed record SpriteRequest(
    string Id,
    SpriteKind Kind,
    int Width,
    int Height,
    string Description,
    string RelativePath);

/// <summary>
/// Процедурный pixel-art генератор спрайтов (локально, без image-gen API).
/// </summary>
public static class ProceduralSpriteGenerator
{
    public static string Generate(string projectRoot, SpriteRequest req)
    {
        var w = Math.Clamp(req.Width, 8, 512);
        var h = Math.Clamp(req.Height, 8, 512);
        var rgba = new byte[w * h * 4];

        // Прозрачный фон по умолчанию
        Clear(rgba, 0, 0, 0, 0);

        var primary = PickColor(req.Description, req.Id, fallbackHue: HashHue(req.Id));
        var secondary = Darken(primary, 0.65f);
        var accent = Lighten(primary, 1.35f);
        var outline = ((byte)20, (byte)20, (byte)28, (byte)255);

        switch (req.Kind)
        {
            case SpriteKind.Enemy:
                DrawEnemy(rgba, w, h, primary, secondary, accent, outline);
                break;
            case SpriteKind.Character:
                DrawCharacter(rgba, w, h, primary, secondary, accent, outline);
                break;
            case SpriteKind.Coin:
            case SpriteKind.Item when req.Id.Contains("coin", StringComparison.OrdinalIgnoreCase)
                                   || req.Description.Contains("coin", StringComparison.OrdinalIgnoreCase)
                                   || req.Description.Contains("монет", StringComparison.OrdinalIgnoreCase):
                DrawCoin(rgba, w, h);
                break;
            case SpriteKind.Item:
                DrawItem(rgba, w, h, primary, accent, outline);
                break;
            case SpriteKind.Tile:
                DrawTile(rgba, w, h, primary, secondary);
                break;
            case SpriteKind.Background:
                DrawBackground(rgba, w, h, primary, secondary, accent);
                break;
            case SpriteKind.Projectile:
                DrawProjectile(rgba, w, h, primary, accent);
                break;
            case SpriteKind.Ui:
                DrawUi(rgba, w, h, primary, secondary, accent);
                break;
            default:
                DrawGeneric(rgba, w, h, primary, secondary, outline);
                break;
        }

        var full = Path.Combine(projectRoot, req.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        PngWriter.WriteRgba(full, w, h, rgba);
        return full;
    }

    public static SpriteKind ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return SpriteKind.Generic;
        return kind.Trim().ToLowerInvariant() switch
        {
            "character" or "player" or "hero" or "персонаж" or "игрок" => SpriteKind.Character,
            "enemy" or "monster" or "враг" => SpriteKind.Enemy,
            "coin" or "монета" => SpriteKind.Coin,
            "item" or "pickup" or "предмет" => SpriteKind.Item,
            "tile" or "ground" or "platform" or "тайл" => SpriteKind.Tile,
            "background" or "bg" or "фон" => SpriteKind.Background,
            "projectile" or "bullet" or "пуля" => SpriteKind.Projectile,
            "ui" or "button" or "hud" => SpriteKind.Ui,
            _ => SpriteKind.Generic
        };
    }

    // ---------- drawing ----------

    private static void DrawCharacter(byte[] px, int w, int h,
        (byte r, byte g, byte b, byte a) body,
        (byte r, byte g, byte b, byte a) shade,
        (byte r, byte g, byte b, byte a) accent,
        (byte r, byte g, byte b, byte a) outline)
    {
        // Пропорции в «пиксельной сетке» относительно размера.
        int headY = h / 6, headH = h / 4, headW = w / 2, headX = (w - headW) / 2;
        FillRect(px, w, h, headX, headY, headW, headH, body);
        // глаза
        int eyeY = headY + headH / 3;
        SetPixel(px, w, h, headX + headW / 4, eyeY, 240, 240, 255, 255);
        SetPixel(px, w, h, headX + 3 * headW / 4, eyeY, 240, 240, 255, 255);
        // тело
        int bodyY = headY + headH;
        int bodyW = w * 2 / 5, bodyH = h / 3, bodyX = (w - bodyW) / 2;
        FillRect(px, w, h, bodyX, bodyY, bodyW, bodyH, shade);
        // ноги
        int legW = Math.Max(2, bodyW / 3), legH = h / 5;
        FillRect(px, w, h, bodyX, bodyY + bodyH, legW, legH, outline);
        FillRect(px, w, h, bodyX + bodyW - legW, bodyY + bodyH, legW, legH, outline);
        // акцент (пояс / шарф)
        FillRect(px, w, h, bodyX, bodyY + bodyH / 3, bodyW, Math.Max(1, h / 16), accent);
        OutlineRect(px, w, h, headX, headY, headW, headH, outline);
    }

    private static void DrawEnemy(byte[] px, int w, int h,
        (byte r, byte g, byte b, byte a) body,
        (byte r, byte g, byte b, byte a) shade,
        (byte r, byte g, byte b, byte a) accent,
        (byte r, byte g, byte b, byte a) outline)
    {
        // Более «квадратный» силуэт + рога
        int cx = w / 2, cy = h / 2;
        int bw = w * 3 / 5, bh = h * 3 / 5;
        FillRect(px, w, h, cx - bw / 2, cy - bh / 3, bw, bh, body);
        // рога
        FillRect(px, w, h, cx - bw / 2 - 1, cy - bh / 3 - h / 8, Math.Max(2, w / 10), h / 6, shade);
        FillRect(px, w, h, cx + bw / 2 - w / 10, cy - bh / 3 - h / 8, Math.Max(2, w / 10), h / 6, shade);
        // глаза-щели
        FillRect(px, w, h, cx - bw / 5, cy - bh / 8, Math.Max(2, w / 10), Math.Max(1, h / 16), accent);
        FillRect(px, w, h, cx + bw / 10, cy - bh / 8, Math.Max(2, w / 10), Math.Max(1, h / 16), accent);
        OutlineRect(px, w, h, cx - bw / 2, cy - bh / 3, bw, bh, outline);
    }

    private static void DrawCoin(byte[] px, int w, int h)
    {
        var gold = ((byte)240, (byte)190, (byte)40, (byte)255);
        var dark = ((byte)180, (byte)120, (byte)20, (byte)255);
        var shine = ((byte)255, (byte)240, (byte)160, (byte)255);
        FillCircle(px, w, h, w / 2, h / 2, Math.Min(w, h) / 2 - 1, gold);
        FillCircle(px, w, h, w / 2, h / 2, Math.Min(w, h) / 4, dark);
        SetPixel(px, w, h, w / 3, h / 3, shine.Item1, shine.Item2, shine.Item3, shine.Item4);
    }

    private static void DrawItem(byte[] px, int w, int h,
        (byte r, byte g, byte b, byte a) primary,
        (byte r, byte g, byte b, byte a) accent,
        (byte r, byte g, byte b, byte a) outline)
    {
        int s = Math.Min(w, h) * 2 / 3;
        int x = (w - s) / 2, y = (h - s) / 2;
        // ромб / кристалл
        FillDiamond(px, w, h, w / 2, h / 2, s / 2, primary);
        FillRect(px, w, h, w / 2 - 1, y + 2, 2, s / 3, accent);
        OutlineRect(px, w, h, x, y, s, s, outline);
    }

    private static void DrawTile(byte[] px, int w, int h,
        (byte r, byte g, byte b, byte a) primary,
        (byte r, byte g, byte b, byte a) secondary)
    {
        FillRect(px, w, h, 0, 0, w, h, primary);
        // шахматные точки / трава сверху
        for (int x = 0; x < w; x += 4)
            for (int y = 0; y < h / 4; y += 2)
                SetPixel(px, w, h, x + (y % 4), y, secondary.r, secondary.g, secondary.b, 255);
        // рамка
        OutlineRect(px, w, h, 0, 0, w, h, Darken(primary, 0.5f));
    }

    private static void DrawBackground(byte[] px, int w, int h,
        (byte r, byte g, byte b, byte a) top,
        (byte r, byte g, byte b, byte a) mid,
        (byte r, byte g, byte b, byte a) accent)
    {
        for (int y = 0; y < h; y++)
        {
            float t = (float)y / Math.Max(1, h - 1);
            var c = Lerp(top, mid, t);
            for (int x = 0; x < w; x++)
                SetPixel(px, w, h, x, y, c.r, c.g, c.b, 255);
        }
        // «звёзды» / облака
        var rng = new Random(HashHue(top.r.ToString() + mid.g));
        int dots = Math.Max(8, w * h / 400);
        for (int i = 0; i < dots; i++)
        {
            int x = rng.Next(w), y = rng.Next(h * 2 / 3);
            SetPixel(px, w, h, x, y, accent.r, accent.g, accent.b, 200);
        }
    }

    private static void DrawProjectile(byte[] px, int w, int h,
        (byte r, byte g, byte b, byte a) primary,
        (byte r, byte g, byte b, byte a) accent)
    {
        FillDiamond(px, w, h, w / 2, h / 2, Math.Min(w, h) / 2 - 1, primary);
        FillCircle(px, w, h, w / 2, h / 2, Math.Max(1, Math.Min(w, h) / 6), accent);
    }

    private static void DrawUi(byte[] px, int w, int h,
        (byte r, byte g, byte b, byte a) primary,
        (byte r, byte g, byte b, byte a) secondary,
        (byte r, byte g, byte b, byte a) accent)
    {
        FillRect(px, w, h, 1, 1, w - 2, h - 2, primary);
        FillRect(px, w, h, 2, 2, w - 4, Math.Max(1, h / 5), accent);
        OutlineRect(px, w, h, 0, 0, w, h, secondary);
    }

    private static void DrawGeneric(byte[] px, int w, int h,
        (byte r, byte g, byte b, byte a) primary,
        (byte r, byte g, byte b, byte a) secondary,
        (byte r, byte g, byte b, byte a) outline)
    {
        FillRect(px, w, h, 2, 2, w - 4, h - 4, primary);
        FillRect(px, w, h, 4, 4, w - 8, h - 8, secondary);
        OutlineRect(px, w, h, 2, 2, w - 4, h - 4, outline);
    }

    // ---------- primitives ----------

    private static void Clear(byte[] px, byte r, byte g, byte b, byte a)
    {
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = a;
        }
    }

    private static void SetPixel(byte[] px, int w, int h, int x, int y, byte r, byte g, byte b, byte a)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        int i = (y * w + x) * 4;
        px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = a;
    }

    private static void FillRect(byte[] px, int w, int h, int x, int y, int rw, int rh,
        (byte r, byte g, byte b, byte a) c)
    {
        for (int yy = y; yy < y + rh; yy++)
            for (int xx = x; xx < x + rw; xx++)
                SetPixel(px, w, h, xx, yy, c.r, c.g, c.b, c.a);
    }

    private static void OutlineRect(byte[] px, int w, int h, int x, int y, int rw, int rh,
        (byte r, byte g, byte b, byte a) c)
    {
        for (int xx = x; xx < x + rw; xx++)
        {
            SetPixel(px, w, h, xx, y, c.r, c.g, c.b, c.a);
            SetPixel(px, w, h, xx, y + rh - 1, c.r, c.g, c.b, c.a);
        }
        for (int yy = y; yy < y + rh; yy++)
        {
            SetPixel(px, w, h, x, yy, c.r, c.g, c.b, c.a);
            SetPixel(px, w, h, x + rw - 1, yy, c.r, c.g, c.b, c.a);
        }
    }

    private static void FillCircle(byte[] px, int w, int h, int cx, int cy, int radius,
        (byte r, byte g, byte b, byte a) c)
    {
        int r2 = radius * radius;
        for (int y = -radius; y <= radius; y++)
            for (int x = -radius; x <= radius; x++)
                if (x * x + y * y <= r2)
                    SetPixel(px, w, h, cx + x, cy + y, c.r, c.g, c.b, c.a);
    }

    private static void FillDiamond(byte[] px, int w, int h, int cx, int cy, int radius,
        (byte r, byte g, byte b, byte a) c)
    {
        for (int y = -radius; y <= radius; y++)
            for (int x = -radius; x <= radius; x++)
                if (Math.Abs(x) + Math.Abs(y) <= radius)
                    SetPixel(px, w, h, cx + x, cy + y, c.r, c.g, c.b, c.a);
    }

    // ---------- color ----------

    private static readonly Dictionary<string, (byte r, byte g, byte b)> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = (220, 60, 60), ["красный"] = (220, 60, 60),
        ["blue"] = (60, 120, 220), ["синий"] = (60, 120, 220),
        ["green"] = (60, 180, 90), ["зелёный"] = (60, 180, 90), ["зеленый"] = (60, 180, 90),
        ["yellow"] = (230, 200, 50), ["жёлтый"] = (230, 200, 50), ["желтый"] = (230, 200, 50),
        ["orange"] = (230, 140, 40), ["оранжевый"] = (230, 140, 40),
        ["purple"] = (160, 80, 200), ["фиолетовый"] = (160, 80, 200),
        ["pink"] = (230, 120, 170), ["розовый"] = (230, 120, 170),
        ["brown"] = (140, 90, 50), ["коричневый"] = (140, 90, 50),
        ["gray"] = (140, 140, 150), ["grey"] = (140, 140, 150), ["серый"] = (140, 140, 150),
        ["black"] = (40, 40, 45), ["чёрный"] = (40, 40, 45), ["черный"] = (40, 40, 45),
        ["white"] = (235, 235, 240), ["белый"] = (235, 235, 240),
        ["gold"] = (240, 190, 40), ["золотой"] = (240, 190, 40),
        ["cyan"] = (60, 200, 210), ["teal"] = (40, 160, 150),
    };

    private static (byte r, byte g, byte b, byte a) PickColor(string description, string id, int fallbackHue)
    {
        foreach (var (name, rgb) in NamedColors)
        {
            if (description.Contains(name, StringComparison.OrdinalIgnoreCase)
                || id.Contains(name, StringComparison.OrdinalIgnoreCase))
                return (rgb.r, rgb.g, rgb.b, 255);
        }

        // HSV → RGB from hash
        float hue = (fallbackHue % 360) / 360f;
        return HsvToRgba(hue, 0.65f, 0.85f);
    }

    private static int HashHue(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (var c in s) h = h * 31 + c;
            return Math.Abs(h) % 360;
        }
    }

    private static (byte r, byte g, byte b, byte a) HsvToRgba(float h, float s, float v)
    {
        float c = v * s;
        float x = c * (1 - Math.Abs(h * 6 % 2 - 1));
        float m = v - c;
        float r1, g1, b1;
        int sector = (int)(h * 6) % 6;
        (r1, g1, b1) = sector switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x)
        };
        return ((byte)((r1 + m) * 255), (byte)((g1 + m) * 255), (byte)((b1 + m) * 255), 255);
    }

    private static (byte r, byte g, byte b, byte a) Darken((byte r, byte g, byte b, byte a) c, float f)
        => ((byte)(c.r * f), (byte)(c.g * f), (byte)(c.b * f), c.a);

    private static (byte r, byte g, byte b, byte a) Lighten((byte r, byte g, byte b, byte a) c, float f)
        => ((byte)Math.Min(255, c.r * f), (byte)Math.Min(255, c.g * f), (byte)Math.Min(255, c.b * f), c.a);

    private static (byte r, byte g, byte b, byte a) Lerp(
        (byte r, byte g, byte b, byte a) a,
        (byte r, byte g, byte b, byte a) b,
        float t)
        => (
            (byte)(a.r + (b.r - a.r) * t),
            (byte)(a.g + (b.g - a.g) * t),
            (byte)(a.b + (b.b - a.b) * t),
            255);
}
