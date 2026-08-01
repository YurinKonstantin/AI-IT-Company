using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

/// <summary>
/// Кодер-геймдев для Monogame (DesktopGL).
/// Генерирует Game1.cs, Player/Enemy/Level; текстуры берёт из манифеста Artist.
/// </summary>
public sealed class GameCoderAgent : AgentBase
{
    public override AgentRole Role => AgentRole.GameCoder;

    protected override string DefaultSystemPrompt => """
    You are a Game Coder using MonoGame (.NET 8, MonoGame.Framework.DesktopGL 3.8.*).

    REQUIREMENTS:
    1. Entry point — Game1 : Game, standard lifecycle
       (Initialize / LoadContent / Update / Draw).
    2. Separate entities into files: Player.cs, Enemy.cs, Coin.cs, Level.cs, Sprite.cs.
    3. Use Vector2 for positions/velocities, Rectangle for collisions (AABB).
    4. Input: Keyboard.GetState() — WASD and Space by default.
    5. TEXTURES — REQUIRED when an asset manifest is provided in the user prompt:
       - Load PNG files with Texture2D.FromStream(GraphicsDevice, stream).
       - Prefer TitleContainer.OpenStream("Content/Sprites/....png") or File.OpenRead
         with paths from the manifest (relative to the output directory).
       - Do NOT use colored Texture2D stubs when real PNG paths are listed.
       - Do NOT require .xnb / MGCB editor.
       - Fallback ONLY if the manifest is missing: Texture2D + SetData<Color>().
    6. Ensure Content files are copied to output (csproj None Update Content\**\* CopyToOutputDirectory).
       Prefer a small xml:diff on .csproj rather than rewriting the whole file.
    7. PackageReference to MonoGame.Framework.DesktopGL via xml:diff if missing.

    RESPONSE FORMAT:
    Only code blocks with paths:
    ```csharp:Game1.cs
        ...
        ```
        ```csharp:Entities/Player.cs
        ...
        ```
        ```xml:diff:MyGame.csproj
        + <None Update="Content\**\*"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
        ```
        No text outside blocks.
    """;

    public GameCoderAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore,
                          ILogger<GameCoderAgent> logger)
        : base(factory, configStore, promptStore, logger) { }

    protected override string BuildUserPrompt(AgentContext ctx)
        => StageContextBuilder.Build(ctx, coderRole: "Кодер-Геймдев (MonoGame.Framework.DesktopGL)");

    protected override Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
    {
        ctx.SharedData["game_code"] = output;
        var count = CodeExtractor.Extract(output).Count(b => b.Path is not null);
        Logger.LogInformation("[GameCoder] Сгенерировано файлов: {Count}", count);
        Debug.WriteLine("AgentResult " + output);
        return Task.FromResult(new AgentResult(count > 0, output,
            count == 0 ? "Не удалось извлечь блоки кода." : null));
    }
}
