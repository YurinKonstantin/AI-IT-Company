
using Core.Agents;
using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

/// <summary>
/// Кодер-тестировщик. Генерирует юнит-тесты xUnit для сгенерированного/существующего кода.
/// В режиме CreateNew — создаёт отдельный тестовый проект.
/// В режиме Improve/FixError/Document — дополняет существующие тесты или создаёт их для непокрытого кода.
/// </summary>
public sealed class TesterAgent : AgentBase
{
    public override AgentRole Role => AgentRole.Tester;

    protected override string SystemPrompt => """
        Ты — Тестировщик. Пишешь юнит-тесты на xUnit для .NET 8+.

        ПРАВИЛА:
        1. Используй xUnit + FluentAssertions + Moq (для моков зависимостей).
        2. Каждый тест — паттерн Arrange / Act / Assert, комментарии на русском.
        3. Имена тестов: MethodName_Condition_ExpectedResult.
        4. Покрывай: happy-path, граничные случаи, исключения, null-аргументы.
        5. Для async-кода используй async Task и await.
        6. Не тестируй тривиальные геттеры/сеттеры.
        7. Если тестируешь существующий код — НЕ переписывай его, только пиши тесты.

        ФОРМАТ ОТВЕТА (СТРОГО):
        - Только блоки кода с указанием пути:
        ```csharp:Tests/ServiceNameTests.cs
        using Xunit;
        using FluentAssertions;
        // ... код ...
        ```
        - Если нужно, включи отдельный тестовый .csproj:
        ```xml:Tests/Tests.csproj
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <IsPackable>false</IsPackable>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.*" />
            <PackageReference Include="xunit" Version="2.9.*" />
            <PackageReference Include="xunit.runner.visualstudio" Version="2.8.*" />
            <PackageReference Include="FluentAssertions" Version="6.12.*" />
            <PackageReference Include="Moq" Version="4.20.*" />
          </ItemGroup>
        </Project>
        ```
        - Никакого текста вне блоков кода.
    """;

    public TesterAgent(IAiProviderFactory factory, AgentConfigStore configStore,
                        ILogger<InterpreterAgent> logger) : base(factory, configStore, logger) { }

    protected override string BuildUserPrompt(AgentContext ctx)
    {
        var architecture = ctx.SharedData.GetValueOrDefault("architecture", "(нет данных)");
        var backend = ctx.SharedData.GetValueOrDefault("backend_code", "");
        var frontend = ctx.SharedData.GetValueOrDefault("frontend_code", "");
        var game = ctx.SharedData.GetValueOrDefault("game_code", "");

        var existingFiles = ctx.Files.Count > 0
            ? string.Join("\n", ctx.Files.Where(f => f.EndsWith(".cs")).Take(30))
            : "(новый проект)";

        return $"""
            Режим: {ctx.Mode}
            Тип проекта: {ctx.Type}
            Задача пользователя: {ctx.UserPrompt}

            === Архитектура ===
            {architecture}

            === Существующие .cs файлы ===
            {existingFiles}

            === Сгенерированный backend-код ===
            {Truncate(backend, 8000)}

            === Сгенерированный frontend-код ===
            {Truncate(frontend, 4000)}

            === Сгенерированный game-код ===
            {Truncate(game, 4000)}

            Сгенерируй полный набор юнит-тестов (xUnit) для всей бизнес-логики.
            Если это WinUI/Game — тестируй только сервисы и модели, не UI.
            """;
    }

    protected override Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
    {
        // Сохраняем в SharedData — Builder заберёт и запишет файлы.
        ctx.SharedData["tests_code"] = output;

        // Подсчёт для отчёта.
        var blocks = CodeExtractor.Extract(output).ToList();
        var testFilesCount = blocks.Count(b => b.Path?.Contains("Tests", StringComparison.OrdinalIgnoreCase) == true);
        ctx.SharedData["tests_files_count"] = testFilesCount.ToString();

        Logger.LogInformation("[Tester] Сгенерировано тестовых файлов: {Count}", testFilesCount);
        return Task.FromResult(new AgentResult(
            Success: blocks.Count > 0,
            Output: output,
            Error: blocks.Count == 0 ? "Не удалось извлечь ни одного блока кода." : null,
            Artifacts: new Dictionary<string, string> { ["tests_files_count"] = testFilesCount.ToString() }));
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "(пусто)" : s.Length <= max ? s : s[..max] + "\n... (обрезано)";
}