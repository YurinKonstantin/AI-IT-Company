using Core.Contracts;
using Microsoft.ML.OnnxRuntimeGenAI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ai;

/// <summary>
/// Локальный inference через Microsoft.ML.OnnxRuntimeGenAI + Windows ML.
/// ModelName = имя подпапки в корне моделей (genai_config.json внутри).
/// </summary>
public sealed class OnnxProvider : IAiProvider, IDisposable
{
    private string _modelsRoot;
    private readonly Dictionary<string, Model> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly object _rootLock = new();

    public string Name => "ONNX (WinML)";

    public string ModelsRoot
    {
        get { lock (_rootLock) return _modelsRoot; }
    }

    public OnnxProvider(string modelsRoot)
    {
        _modelsRoot = string.IsNullOrWhiteSpace(modelsRoot)
            ? throw new ArgumentException("Models root is required.", nameof(modelsRoot))
            : Path.GetFullPath(modelsRoot);
    }

    public void SetModelsRoot(string modelsRoot)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot))
            throw new ArgumentException("Models root is required.", nameof(modelsRoot));

        var full = Path.GetFullPath(modelsRoot);
        lock (_rootLock)
        {
            if (string.Equals(_modelsRoot, full, StringComparison.OrdinalIgnoreCase))
                return;
            ClearCache();
            _modelsRoot = full;
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(Directory.Exists(ModelsRoot));

    public IReadOnlyList<string> ListLocalModelFolders()
    {
        var root = ModelsRoot;
        if (!Directory.Exists(root)) return Array.Empty<string>();

        return Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        AiRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ModelName))
            throw new ArgumentException("ONNX model folder name is required.", nameof(request));

        var model = await GetOrLoadModelAsync(request.ModelName.Trim(), ct);
        using var tokenizer = new Tokenizer(model);

        var prompt = BuildPromptTemplate(request.SystemPrompt, request.UserPrompt);
        using var tokens = tokenizer.Encode(prompt);

        using var gp = new GeneratorParams(model);
        gp.SetSearchOption("temperature", request.Temperature);
        var maxLen = request.MaxTokens == 0 ? Math.Min(request.ContextWindow, 4096) : request.MaxTokens;
        gp.SetSearchOption("max_length", maxLen);
        gp.SetSearchOption("past_present_share_buffer", false);

        using var generator = new Generator(model, gp);
        generator.AppendTokenSequences(tokens);
        using var stream = tokenizer.CreateStream();

        while (!generator.IsDone())
        {
            ct.ThrowIfCancellationRequested();
            generator.GenerateNextToken();

            var seq = generator.GetSequence(0);
            var nextToken = seq[seq.Length - 1];
            var piece = stream.Decode(nextToken);
            if (!string.IsNullOrEmpty(piece))
                yield return piece;

            await Task.Yield();
        }
    }

    private async Task<Model> GetOrLoadModelAsync(string modelName, CancellationToken ct)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(modelName, out var cached)) return cached;
        }

        await _loadLock.WaitAsync(ct);
        try
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(modelName, out var cached)) return cached;
            }

            var path = Path.Combine(ModelsRoot, modelName);
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException(
                    $"Модель '{modelName}' не найдена в '{ModelsRoot}'. Положите ONNX GenAI-папку модели туда.");

            var m = new Model(path);
            lock (_cache) _cache[modelName] = m;
            return m;
        }
        finally { _loadLock.Release(); }
    }

    private static string BuildPromptTemplate(string system, string user)
        => $"<|system|>\n{system}<|end|>\n<|user|>\n{user}<|end|>\n<|assistant|>\n";

    private void ClearCache()
    {
        lock (_cache)
        {
            foreach (var m in _cache.Values)
            {
                try { m.Dispose(); } catch { /* ignore */ }
            }
            _cache.Clear();
        }
    }

    public void Dispose()
    {
        ClearCache();
        _loadLock.Dispose();
    }
}
