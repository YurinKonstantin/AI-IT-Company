using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Ai
{
    public sealed class ChatService
    {
        private readonly IChatClient _chat;
        public ChatService(IChatClient chat) => _chat = chat;

        public async IAsyncEnumerable<string> AskStreamAsync(
            string system, string user,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User,   user)
        };

            var options = new ChatOptions { Temperature = 0.7f /*, MaxOutputTokens = ... */ };

            await foreach (var update in _chat.GetStreamingResponseAsync(messages, options, ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                    yield return update.Text;
            }
        }
    }
}
