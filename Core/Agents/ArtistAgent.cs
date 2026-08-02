using Build;
using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

/// <summary>
/// Художник для MonoGame: art-brief (LLM) + PNG (procedural / OpenRouter / Ollama).
/// При сбое image-gen всегда откатывается на процедурный pixel-art.
/// </summary>
public sealed class ArtistAgent : AgentBase
{
    public override AgentRole Role => AgentRole.Artist;

    private readonly AppSettingsStore _settings;
    private readonly IReadOnlyDictionary<ArtistImageMode, IImageGenClient> _imageClients;

    protected override string DefaultSystemPrompt => """
    You are a Game Artist for MonoGame pixel-art projects.
    Return STRICTLY one JSON object (no markdown fences, no commentary):
    {
      "style": "pixel-art",
      "palette": "short palette description",
      "assets": [
        {
          "id": "player",
          "kind": "character|enemy|coin|item|tile|background|projectile|ui",
          "width": 32,
          "height": 32,
          "description": "blue hero with cape",
          "path": "Content/Sprites/player.png"
        }
      ]
    }

    RULES:
    - Include sprites needed for the CURRENT game stage (player, enemies, tiles, items, bg as relevant).
    - Paths MUST be under Content/Sprites/ and end with .png.
    - Prefer sizes: characters/enemies 32x32, coins/items 16x16, tiles 32x32, background 320x180.
    - 4–12 assets is enough. Do not invent animation frames unless asked.
    - id: snake_case or lowercase words, unique.
    """;

    public ArtistAgent(
        IAiProviderFactory factory,
        AgentConfigStore configStore,
        AgentPromptStore promptStore,
        AppSettingsStore settings,
        IEnumerable<IImageGenClient> imageClients,
        ILogger<ArtistAgent> logger)
        : base(factory, configStore, promptStore, logger)
    {
        _settings = settings;
        _imageClients = imageClients.ToDictionary(c => c.Mode);
    }

    protected override string BuildUserPrompt(AgentContext ctx)
    {
        var stage = ctx.CurrentStage;
        var stageInfo = stage is null
            ? "(no stage — create a minimal starter set)"
            : $"""
              Stage: {stage.Id} {stage.Name}
              Goal: {stage.Goal}
              Deliverables: {string.Join("; ", stage.Deliverables)}
              """;

        var imageMode = _settings.GetArtistImageMode();
        return $"""
            Create an art brief JSON for a MonoGame game.
            Image backend (for pixel generation after this brief): {imageMode}

            User task: {ctx.UserPrompt}
            Project type: {ctx.Type}
            Project title: {ctx.Plan?.Title ?? "(untitled)"}
            Summary: {ctx.Plan?.Summary ?? ctx.SharedData.GetValueOrDefault("summary", "")}

            {stageInfo}

            TZ excerpt:
            {Truncate(ctx.SharedData.GetValueOrDefault("tz_markdown", ""), 3000)}
            """;
    }

    protected override async Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
    {
        var root = ctx.OutputRoot ?? ctx.ProjectPath
            ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
        PathHelper.EnsureDirectory(root);

        ArtBrief brief;
        try
        {
            brief = ParseBrief(output);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Artist] Не удалось разобрать brief — используем fallback-набор");
            brief = FallbackBrief(ctx);
        }

        if (brief.Assets.Count == 0)
            brief = FallbackBrief(ctx);

        PathHelper.EnsureDirectory(Path.Combine(root, "Content", "Sprites"));

        var mode = _settings.GetArtistImageMode();
        var generatorUsed = mode.ToString().ToLowerInvariant();
        var generated = new List<string>();
        var manifestAssets = new List<object>();
        int imageGenOk = 0, proceduralFallback = 0;

        foreach (var a in brief.Assets)
        {
            var id = SanitizeId(a.Id);
            var rel = NormalizePath(a.Path, id);
            var kind = ProceduralSpriteGenerator.ParseKind(a.Kind);
            var w = a.Width > 0 ? a.Width : DefaultWidth(kind);
            var h = a.Height > 0 ? a.Height : DefaultHeight(kind);
            var desc = string.IsNullOrWhiteSpace(a.Description) ? id : a.Description;
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            PathHelper.EnsureDirectory(Path.GetDirectoryName(full)!);

            string source = "procedural";
            try
            {
                bool wroteRemote = false;
                if (mode != ArtistImageMode.Procedural)
                {
                    wroteRemote = await TryRemoteImageAsync(
                        mode, id, kind, w, h, desc, brief.Style, full, ct);
                    if (wroteRemote)
                    {
                        source = mode.ToString().ToLowerInvariant();
                        imageGenOk++;
                    }
                    else
                    {
                        proceduralFallback++;
                    }
                }

                if (!wroteRemote)
                {
                    ProceduralSpriteGenerator.Generate(root,
                        new SpriteRequest(id, kind, w, h, desc, rel));
                    source = "procedural";
                }

                generated.Add(rel.Replace('\\', '/'));
                manifestAssets.Add(new
                {
                    id,
                    kind = kind.ToString(),
                    width = w,
                    height = h,
                    description = desc,
                    path = rel.Replace('\\', '/'),
                    source
                });
                Logger.LogInformation("[Artist] Спрайт ({Source}): {Path}", source, full);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[Artist] Не удалось сгенерировать {Id}", id);
            }
        }

        if (mode != ArtistImageMode.Procedural && imageGenOk == 0 && generated.Count > 0)
            generatorUsed = "procedural-fallback";
        else if (proceduralFallback > 0 && imageGenOk > 0)
            generatorUsed = $"{mode.ToString().ToLowerInvariant()}+procedural-fallback";
        else if (mode == ArtistImageMode.Procedural)
            generatorUsed = "procedural-pixel";

        var manifest = new
        {
            style = brief.Style ?? "pixel-art",
            palette = brief.Palette ?? "procedural",
            generator = generatorUsed,
            imageMode = mode.ToString(),
            imageGenOk,
            proceduralFallback,
            assets = manifestAssets,
            loadHint = "Use Texture2D.FromStream(GraphicsDevice, TitleContainer.OpenStream(path)) or FileStream. Ensure Content/** is copied to output."
        };

        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var manifestPath = Path.Combine(root, "Content", "assets.manifest.json");
        PathHelper.EnsureDirectory(Path.GetDirectoryName(manifestPath)!);
        await File.WriteAllTextAsync(manifestPath, manifestJson, ct);

        await EnsureContentCopyToOutputAsync(root, ct);

        try
        {
            var mgcb = await MgcbContentBuilder.EnsureAsync(root, ct);
            ctx.SharedData["mgcb_summary"] = mgcb.Summary;
            if (mgcb.MgcbPath is not null)
                ctx.SharedData["mgcb_path"] = "Content/Content.mgcb";
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Artist] MGCB/Content step skipped.");
        }

        ctx.SharedData["artist_manifest"] = manifestJson;
        ctx.SharedData["artist_assets"] = string.Join(", ", generated);
        ctx.SharedData["artist_assets_count"] = generated.Count.ToString();
        ctx.SharedData["artist_brief"] = output;
        ctx.SharedData["artist_image_mode"] = mode.ToString();
        ctx.SharedData["artist_generator"] = generatorUsed;

        var summary = new StringBuilder();
        summary.AppendLine($"Generated {generated.Count} sprites via {generatorUsed} (mode={mode}).");
        if (imageGenOk > 0) summary.AppendLine($"Image-gen OK: {imageGenOk}");
        if (proceduralFallback > 0) summary.AppendLine($"Procedural fallback: {proceduralFallback}");
        foreach (var g in generated) summary.AppendLine("- " + g);

        return new AgentResult(
            generated.Count > 0,
            summary.ToString(),
            generated.Count == 0 ? "Не удалось создать спрайты." : null,
            Artifacts: new Dictionary<string, string>
            {
                ["manifest"] = "Content/assets.manifest.json",
                ["assets"] = ctx.SharedData["artist_assets"],
                ["generator"] = generatorUsed
            });
    }

    private async Task<bool> TryRemoteImageAsync(
        ArtistImageMode mode,
        string id,
        SpriteKind kind,
        int w,
        int h,
        string desc,
        string? style,
        string fullPath,
        CancellationToken ct)
    {
        if (!_imageClients.TryGetValue(mode, out var client))
        {
            Logger.LogWarning("[Artist] Клиент image-gen для {Mode} не зарегистрирован.", mode);
            return false;
        }

        var model = mode switch
        {
            ArtistImageMode.OpenRouter => _settings.GetArtistOpenRouterImageModel(),
            ArtistImageMode.Ollama => _settings.GetArtistOllamaImageModel(),
            _ => null
        };

        var req = new ImageGenRequest(
            Prompt: desc,
            Width: w,
            Height: h,
            Model: model,
            StyleHint: string.IsNullOrWhiteSpace(style) ? "pixel art game sprite" : style!,
            AssetId: id,
            Kind: kind.ToString());

        try
        {
            var result = await client.GeneratePngAsync(req, ct);
            if (!result.Success || result.PngBytes is null || result.PngBytes.Length < 32)
            {
                Logger.LogWarning("[Artist] {Client} не смог для {Id}: {Err}",
                    client.Name, id, result.Error ?? "empty");
                return false;
            }

            // Простая проверка PNG signature; иначе всё равно сохраняем — MonoGame может принять jpeg как файл с другим расширением реже.
            await File.WriteAllBytesAsync(fullPath, result.PngBytes, ct);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Artist] Ошибка {Client} для {Id}", client.Name, id);
            return false;
        }
    }

    // ---------- brief parsing ----------

    private sealed class ArtBrief
    {
        public string? Style { get; set; }
        public string? Palette { get; set; }
        public List<ArtAsset> Assets { get; set; } = new();
    }

    private sealed class ArtAsset
    {
        public string Id { get; set; } = "sprite";
        public string Kind { get; set; } = "generic";
        public int Width { get; set; }
        public int Height { get; set; }
        public string Description { get; set; } = "";
        public string Path { get; set; } = "";
    }

    private static ArtBrief ParseBrief(string output)
    {
        var json = ExtractJson(output);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var brief = JsonSerializer.Deserialize<ArtBrief>(json, options)
                    ?? throw new InvalidOperationException("empty brief");
        return brief;
    }

    private static string ExtractJson(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "{}";
        var fence = System.Text.RegularExpressions.Regex.Match(
            s, @"```(?:json)?\s*\r?\n(?<body>.*?)\r?\n```",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (fence.Success) s = fence.Groups["body"].Value;

        int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        return (a >= 0 && b > a) ? s[a..(b + 1)] : "{}";
    }

    private static ArtBrief FallbackBrief(AgentContext ctx)
    {
        var theme = ctx.Plan?.Title ?? "game";
        return new ArtBrief
        {
            Style = "pixel-art",
            Palette = "procedural",
            Assets = new List<ArtAsset>
            {
                new() { Id = "player", Kind = "character", Width = 32, Height = 32, Description = $"blue hero for {theme}", Path = "Content/Sprites/player.png" },
                new() { Id = "enemy", Kind = "enemy", Width = 32, Height = 32, Description = $"red enemy for {theme}", Path = "Content/Sprites/enemy.png" },
                new() { Id = "coin", Kind = "coin", Width = 16, Height = 16, Description = "gold coin", Path = "Content/Sprites/coin.png" },
                new() { Id = "tile_ground", Kind = "tile", Width = 32, Height = 32, Description = "green grass ground tile", Path = "Content/Sprites/tile_ground.png" },
                new() { Id = "background", Kind = "background", Width = 320, Height = 180, Description = "blue sky background", Path = "Content/Sprites/background.png" },
            }
        };
    }

    private static string SanitizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "sprite";
        var sb = new StringBuilder();
        foreach (var c in id.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is '_' or '-' or ' ') sb.Append('_');
        }
        var s = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(s) ? "sprite" : s;
    }

    private static string NormalizePath(string? path, string id)
    {
        if (string.IsNullOrWhiteSpace(path))
            return $"Content/Sprites/{id}.png";

        var p = path.Trim().Replace('\\', '/').TrimStart('/');
        if (p.Contains("..", StringComparison.Ordinal))
            return $"Content/Sprites/{id}.png";
        if (!p.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
            p = "Content/Sprites/" + Path.GetFileName(p);
        if (!p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            p += ".png";
        return p;
    }

    private static int DefaultWidth(SpriteKind k) => k switch
    {
        SpriteKind.Background => 320,
        SpriteKind.Coin or SpriteKind.Item or SpriteKind.Projectile => 16,
        SpriteKind.Ui => 64,
        _ => 32
    };

    private static int DefaultHeight(SpriteKind k) => k switch
    {
        SpriteKind.Background => 180,
        SpriteKind.Coin or SpriteKind.Item or SpriteKind.Projectile => 16,
        SpriteKind.Ui => 24,
        _ => 32
    };

    private static async Task EnsureContentCopyToOutputAsync(string root, CancellationToken ct)
    {
        var projects = NuGetPackageResolver.FindProjectFiles(root);
        foreach (var csproj in projects)
        {
            string text;
            try { text = await File.ReadAllTextAsync(csproj, ct); }
            catch { continue; }

            if (text.Contains("Content\\**", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Content/**", StringComparison.OrdinalIgnoreCase))
                continue;

            bool looksGame = text.Contains("MonoGame", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(csproj)
                    .Contains("Game", StringComparison.OrdinalIgnoreCase)
                || projects.Count == 1;
            if (!looksGame) continue;

            const string snippet = """

  <ItemGroup>
    <None Update="Content\**\*">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
""";
            var idx = text.LastIndexOf("</Project>", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            text = text.Insert(idx, snippet + Environment.NewLine);
            await File.WriteAllTextAsync(csproj, text, ct);
            break;
        }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "\n…";
}
