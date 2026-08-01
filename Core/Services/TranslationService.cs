using Core.Agents;
using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Services
{
    public interface ITranslationService
    {
        /// <summary>Переводит пользовательский ввод на рабочий язык (обычно English).</summary>
        Task<string> ToWorkingAsync(string text, CancellationToken ct = default);

        /// <summary>Переводит вывод модели с рабочего языка обратно на язык пользователя.</summary>
        Task<string> ToUserAsync(string text, CancellationToken ct = default);

        bool IsEnabled { get; }
        string UserLanguage { get; }
        string WorkingLanguage { get; }
    }

    public sealed class TranslationService : ITranslationService
    {
        private readonly TranslatorAgent _translator;
        private readonly AppSettingsStore _settings;
        private readonly ILogger<TranslationService> _logger;
        private readonly ConcurrentDictionary<string, string> _cache = new();

        public TranslationService(TranslatorAgent translator,
                                  AppSettingsStore settings,
                                  ILogger<TranslationService> logger)
        {
            _translator = translator;
            _settings = settings;
            _logger = logger;
        }

        public bool IsEnabled => _settings.GetTranslationEnabled();
        public string UserLanguage => _settings.GetUserLanguage();
        public string WorkingLanguage => _settings.GetWorkingLanguage();

        public Task<string> ToWorkingAsync(string text, CancellationToken ct = default)
            => TranslateAsync(text, UserLanguage, WorkingLanguage, ct);

        public Task<string> ToUserAsync(string text, CancellationToken ct = default)
            => TranslateAsync(text, WorkingLanguage, UserLanguage, ct);

        private async Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct)
        {
            if (!IsEnabled) return text;
            if (string.IsNullOrWhiteSpace(text)) return text;

            // Быстрый пропуск: если текст уже похож на целевой язык — не трогаем.
            if (LooksLike(text, to)) return text;

            var key = HashKey(text, from, to);
            if (_cache.TryGetValue(key, out var cached)) return cached;

            try
            {
                var directive =
                    $"TARGET language: {to}. Source language: {from}.\n\n" +
                    "Translate the following text. Output only the translation, nothing else.\n\n" +
                    "===BEGIN===\n" + text + "\n===END===";

                var ctx = new AgentContext
                {
                    ProjectId = "translator",
                    UserPrompt = directive
                };
                ctx.SharedData["translator_input"] = directive;

                var result = await _translator.ExecuteAsync(ctx, ct);
                var translated = result.Success ? Clean(result.Output) : text;
                _cache[key] = translated;
                return translated;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Translation failed, returning original text.");
                return text;
            }
        }

        // ---- helpers ----

        private static string Clean(string s)
        {
            // На всякий случай снимаем ===BEGIN===/===END===, если модель повторила.
            s = Regex.Replace(s, @"===\s*BEGIN\s*===\s*", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"===\s*END\s*===\s*", "", RegexOptions.IgnoreCase);
            return s.Trim();
        }

        private static string HashKey(string t, string from, string to)
        {
            var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(from + "|" + to + "|" + t));
            return Convert.ToHexString(bytes);
        }

        /// <summary>Простейший детектор: если целевой язык English и в тексте только латиница — уже English.</summary>
        private static bool LooksLike(string text, string language)
        {
            if (string.Equals(language, "English", StringComparison.OrdinalIgnoreCase))
            {
                // >95% символов — базовая латиница/пунктуация ⇒ считаем, что уже English.
                int total = 0, latin = 0;
                foreach (var c in text)
                {
                    if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsDigit(c)) continue;
                    total++;
                    if (c <= 0x007F && char.IsLetter(c)) latin++;
                }
                return total > 0 && latin * 100 / total >= 95;
            }
            return false;
        }
    }
}
