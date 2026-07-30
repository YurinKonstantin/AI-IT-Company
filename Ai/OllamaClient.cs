using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ai
{
    public sealed class OllamaModelCatalog : IModelCatalog, IDisposable
    {
        private HttpClient _http;
        public string ProviderName => "Ollama";

        public OllamaModelCatalog(string baseUrl) => _http = Create(baseUrl);

        public void SetBaseUrl(string baseUrl)
        {
            _http.Dispose();
            _http = Create(baseUrl);
        }

        private static HttpClient Create(string url) => new()
        {
            BaseAddress = new Uri(url),
            Timeout = Timeout.InfiniteTimeSpan
        };

        public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            try { using var r = await _http.GetAsync("/api/tags", ct); return r.IsSuccessStatusCode; }
            catch { return false; }
        }

        public async Task<IReadOnlyList<AiModelInfo>> ListAsync(CancellationToken ct = default)
        {
            var resp = await _http.GetFromJsonAsync<TagsResponse>("/api/tags", ct);
            return resp?.Models.Select(m => new AiModelInfo(
                m.Name, m.Size, m.ModifiedAt,
                m.Details?.Family, m.Details?.ParameterSize, m.Details?.QuantizationLevel)).ToList()
                ?? new List<AiModelInfo>();
        }

        public async Task<bool> DeleteAsync(string modelName, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, "/api/delete")
            { Content = JsonContent.Create(new { name = modelName }) };
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }

        public async IAsyncEnumerable<ModelPullProgress> PullAsync(
            string modelName, [EnumeratorCancellation] CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/pull")
            { Content = JsonContent.Create(new { name = modelName, stream = true }) };
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;

                PullDto? p = null;
                try { p = JsonSerializer.Deserialize<PullDto>(line); } catch { }
                if (p is not null)
                    yield return new ModelPullProgress(p.Status, p.Total, p.Completed, p.Error);
            }
        }

        public void Dispose() => _http.Dispose();

        // ----- DTO для десериализации -----
        private sealed class TagsResponse
        {
            [JsonPropertyName("models")] public List<Local> Models { get; set; } = new();
            public sealed class Local
            {
                [JsonPropertyName("name")] public string Name { get; set; } = "";
                [JsonPropertyName("size")] public long Size { get; set; }
                [JsonPropertyName("modified_at")] public DateTime ModifiedAt { get; set; }
                [JsonPropertyName("details")] public Det? Details { get; set; }
                public sealed class Det
                {
                    [JsonPropertyName("family")] public string? Family { get; set; }
                    [JsonPropertyName("parameter_size")] public string? ParameterSize { get; set; }
                    [JsonPropertyName("quantization_level")] public string? QuantizationLevel { get; set; }
                }
            }
        }
        private sealed class PullDto
        {
            [JsonPropertyName("status")] public string Status { get; set; } = "";
            [JsonPropertyName("total")] public long? Total { get; set; }
            [JsonPropertyName("completed")] public long? Completed { get; set; }
            [JsonPropertyName("error")] public string? Error { get; set; }
        }
    }
    public sealed class OllamaClient
    {
        private HttpClient _http;

        public OllamaClient(string baseUrl = "http://localhost:11434")
       => _http = Create(baseUrl);
        public void SetBaseUrl(string baseUrl)
        {
            _http.Dispose();
            _http = Create(baseUrl);
        }

        public string BaseUrl => _http.BaseAddress?.ToString() ?? "";

        private static HttpClient Create(string url) => new()
        {
            BaseAddress = new Uri(url),
            Timeout = Timeout.InfiniteTimeSpan
        };
        public async Task<bool> PingAsync(CancellationToken ct = default)
        {
            try
            {
                using var r = await _http.GetAsync("/api/tags", ct);
                return r.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>Список установленных моделей: GET /api/tags</summary>
        public async Task<IReadOnlyList<OllamaLocalModel>> ListAsync(CancellationToken ct = default)
        {
            var resp = await _http.GetFromJsonAsync<TagsResponse>("/api/tags", ct);
            return resp?.Models ?? new List<OllamaLocalModel>();
        }

        /// <summary>Удалить модель: DELETE /api/delete</summary>
        public async Task<bool> DeleteAsync(string modelName, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, "/api/delete")
            {
                Content = JsonContent.Create(new { name = modelName })
            };
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }

        /// <summary>Скачать модель со стримингом статусов: POST /api/pull</summary>
        public async IAsyncEnumerable<PullProgress> PullAsync(
            string modelName,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var body = new { name = modelName, stream = true };
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/pull")
            {
                Content = JsonContent.Create(body)
            };
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;

                PullProgress? p = null;
                try { p = JsonSerializer.Deserialize<PullProgress>(line); }
                catch { /* пропускаем битые фреймы */ }

                if (p is not null) yield return p;
            }
        }

        // ---------- DTO ----------

        private sealed class TagsResponse
        {
            [JsonPropertyName("models")] public List<OllamaLocalModel> Models { get; set; } = new();
        }
    }

    public sealed class OllamaLocalModel
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("modified_at")] public DateTime ModifiedAt { get; set; }
        [JsonPropertyName("digest")] public string Digest { get; set; } = "";
        [JsonPropertyName("details")] public OllamaModelDetails? Details { get; set; }
    }

    public sealed class OllamaModelDetails
    {
        [JsonPropertyName("family")] public string? Family { get; set; }
        [JsonPropertyName("parameter_size")] public string? ParameterSize { get; set; }
        [JsonPropertyName("quantization_level")] public string? QuantizationLevel { get; set; }
    }

    public sealed class PullProgress
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("digest")] public string? Digest { get; set; }
        [JsonPropertyName("total")] public long? Total { get; set; }
        [JsonPropertyName("completed")] public long? Completed { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}
