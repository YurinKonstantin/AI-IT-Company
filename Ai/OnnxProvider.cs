using Core.Contracts;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ai;

//public sealed class OnnxProvider : IAiProvider, IDisposable
//{
//    private readonly string _modelsRoot;
//    private readonly Dictionary<string, Model> _cache = new();
//    private readonly SemaphoreSlim _loadLock = new(1, 1);
//    public string Name => "Microsoft ONNX (CPU)";

//    public OnnxProvider(string modelsRoot) => _modelsRoot = modelsRoot;

//    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
//        => Task.FromResult(Directory.Exists(_modelsRoot));

//    public async IAsyncEnumerable<string> GenerateStreamAsync(
//        AiRequest request,
//        [EnumeratorCancellation] CancellationToken ct = default)
//    {
//        var model = await GetOrLoadModelAsync(request.ModelName, ct);
//        using var tokenizer = new Tokenizer(model);

//        var prompt = BuildPromptTemplate(request.SystemPrompt, request.UserPrompt);
//        using var tokens = tokenizer.Encode(prompt);

//        using var gp = new GeneratorParams(model);
//        gp.SetSearchOption("temperature", request.Temperature);
//        gp.SetSearchOption("max_length",
//            request.MaxTokens == 0 ? request.ContextWindow : request.MaxTokens);
//        gp.SetSearchOption("past_present_share_buffer", false);

//        using var generator = new Generator(model, gp);
//        // API 0.4+: токены добавляются в generator, а не в params
//        generator.AppendTokenSequences(tokens);

//        // API 0.4+: stream создаётся фабричным методом tokenizer'а
//        using var stream = tokenizer.CreateStream();

//        while (!generator.IsDone())
//        {
//            ct.ThrowIfCancellationRequested();

//            // API 0.4+: один вызов вместо ComputeLogits + GenerateNextToken
//            generator.GenerateNextToken();

//            var seq = generator.GetSequence(0);
//            var nextToken = seq[seq.Length - 1];

//            var piece = stream.Decode(nextToken);
//            if (!string.IsNullOrEmpty(piece))
//                yield return piece;

//            await Task.Yield();
//        }
//    }

//    private async Task<Model> GetOrLoadModelAsync(string modelName, CancellationToken ct)
//    {
//        if (_cache.TryGetValue(modelName, out var cached)) return cached;

//        await _loadLock.WaitAsync(ct);
//        try
//        {
//            if (_cache.TryGetValue(modelName, out cached)) return cached;

//            var path = Path.Combine(_modelsRoot, modelName);
//            if (!Directory.Exists(path))
//                throw new DirectoryNotFoundException(
//                    $"Модель '{modelName}' не найдена в '{_modelsRoot}'. Скачайте её через ModelsPage.");

//            var m = new Model(path);
//            _cache[modelName] = m;
//            return m;
//        }
//        finally { _loadLock.Release(); }
//    }

//    /// <summary>Шаблон промпта для Phi-семейства (chat-template).</summary>
//    private static string BuildPromptTemplate(string system, string user)
//        => $"<|system|>\n{system}<|end|>\n<|user|>\n{user}<|end|>\n<|assistant|>\n";

//    public void Dispose()
//    {
//        foreach (var m in _cache.Values) m.Dispose();
//        _cache.Clear();
//        _loadLock.Dispose();
//    }
//}