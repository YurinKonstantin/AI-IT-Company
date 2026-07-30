
using Core.Agents;
using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

/// <summary>
/// Кодер-геймдев для Monogame (DesktopGL).
/// Генерирует Game1.cs, Player/Enemy/Level, csproj с зависимостями.
/// </summary>
public sealed class GameCoderAgent : AgentBase
{
    public override AgentRole Role => AgentRole.GameCoder;

    protected override string DefaultSystemPrompt => """
        Ты — Кодер игр на MonoGame (.NET 8, MonoGame.Framework.DesktopGL 3.8.*).

        ТРЕБОВАНИЯ:
        1. Точка входа — Game1 : Game, стандартный жизненный цикл
           (Initialize / LoadContent / Update / Draw).
        2. Разделяй сущности по файлам: Player.cs, Enemy.cs, Coin.cs, Level.cs, Sprite.cs.
        3. Используй Vector2 для позиций/скоростей, Rectangle для коллизий (AABB).
        4. Ввод: Keyboard.GetState() — WASD и Space по умолчанию.
        5. Ресурсы (Content): используй тестовые заглушки Texture2D через
           new Texture2D(GraphicsDevice, w, h) + SetData<Color>() — БЕЗ .xnb файлов,
           чтобы проект собирался без MGCB-редактора.
        6. Обязателен .csproj с PackageReference на MonoGame.Framework.DesktopGL.

        ФОРМАТ ОТВЕТА:
        Только блоки кода с путями:
        ```csharp:Game1.cs
        ...
        ```
        ```csharp:Entities/Player.cs
        ...
        ```
        ```xml:MyGame.csproj
        <Project Sdk="Microsoft.NET.Sdk">...</Project>
        ```
        Никакого текста вне блоков.
    """;

    public GameCoderAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore,
                          ILogger<GameCoderAgent> logger)
        : base(factory, configStore, promptStore, logger) { }

    protected override string BuildUserPrompt(AgentContext ctx) => StageContextBuilder.Build(ctx, coderRole: "Кодер-Геймдев (MonoGame.Framework.DesktopGL)");

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