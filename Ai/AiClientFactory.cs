using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Text;


namespace Ai
{
    public sealed class AiClientFactory : IAiClientFactory
    {
        private readonly ILogger<AiClientFactory> _logger;

        public AiClientFactory(ILogger<AiClientFactory> logger) => _logger = logger;

        public IChatClient CreateChatClient(AiProviderKind kind, AiClientOptions o)
        {
            switch (kind)
            {
                //case AiProviderKind.Ollama:
                //    {
                //        var endpoint = new Uri(new Uri(o.BaseUrl), "v1");
                //        var openAi = new OpenAIClient(
                //            new ApiKeyCredential(o.ApiKey ?? "ollama"),
                //            new OpenAIClientOptions { Endpoint = endpoint });

                //        //IChatClient client = openAi.AsChatClient(o.ModelName);

                //        //// Пайплайн middleware Microsoft.Extensions.AI:
                //        //return new ChatClientBuilder(client)
                //        //    .UseLogging(_logger)              // логирование
                //        //    .UseFunctionInvocation()          // готово к tool-calls в будущем
                //            //.Build();
                //    }

                case AiProviderKind.OnnxDirectML:
                case AiProviderKind.PhiSilica:
                case AiProviderKind.OpenAI:
                    throw new NotImplementedException($"{kind} — в следующих спринтах.");

                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public IModelCatalog CreateCatalog(AiProviderKind kind, AiClientOptions o) => kind switch
        {
            AiProviderKind.Ollama => new OllamaModelCatalog(o.BaseUrl),
            _ => throw new NotImplementedException($"Каталог для {kind} — позже.")
        };
    }
}
