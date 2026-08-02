using Core.Freelance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ai.Freelance;

/// <summary>Демо-лента для отладки UI/скоринга без реальных API.</summary>
public sealed class DemoMarketplaceAdapter : IFreelanceMarketplace
{
    public string Name => "Demo";

    public Task<IReadOnlyList<FreelanceJobOffer>> SearchAsync(FreelanceSearchQuery query, CancellationToken ct = default)
    {
        var kw = query.Keywords ?? "";
        var all = new List<FreelanceJobOffer>
        {
            new()
            {
                ExternalId = "demo-winui-1",
                Source = Name,
                Title = "WinUI 3 desktop app for inventory ($400)",
                Description = "Need a C# WinUI 3 app with SQLite, CRUD for products, search, and export to CSV. MVVM preferred.",
                BudgetMin = 350, BudgetMax = 450, Currency = "USD",
                Tags = new List<string> { "C#", "WinUI", ".NET" },
                Url = "https://example.com/demo/winui-1",
                ClientName = "DemoClient"
            },
            new()
            {
                ExternalId = "demo-api-1",
                Source = Name,
                Title = "ASP.NET Core Web API for bookings",
                Description = "Build REST API with JWT auth, EF Core SQLite, swagger. Deadline in a week.",
                BudgetMin = 200, BudgetMax = 300, Currency = "USD",
                Tags = new List<string> { "C#", "API", "ASP.NET" },
                DeadlineUtc = DateTime.UtcNow.AddDays(7),
                Url = "https://example.com/demo/api-1"
            },
            new()
            {
                ExternalId = "demo-game-1",
                Source = Name,
                Title = "MonoGame 2D platformer prototype bounty $250",
                Description = "Short platformer with player, enemies, coins. Prefer MonoGame DesktopGL.",
                BudgetMin = 250, BudgetMax = 250, Currency = "USD",
                Tags = new List<string> { "MonoGame", "C#", "game" },
                Url = "https://example.com/demo/game-1"
            },
            new()
            {
                ExternalId = "demo-php-1",
                Source = Name,
                Title = "WordPress plugin tweak",
                Description = "Small PHP change on WordPress site.",
                BudgetMin = 40, BudgetMax = 60, Currency = "USD",
                Tags = new List<string> { "PHP", "WordPress" },
                Url = "https://example.com/demo/php-1"
            }
        };

        IEnumerable<FreelanceJobOffer> q = all;
        if (!string.IsNullOrWhiteSpace(kw))
        {
            var parts = kw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            q = q.Where(o => parts.Any(p =>
                o.Title.Contains(p, StringComparison.OrdinalIgnoreCase)
                || o.Description.Contains(p, StringComparison.OrdinalIgnoreCase)
                || o.Tags.Any(t => t.Contains(p, StringComparison.OrdinalIgnoreCase))));
        }

        if (query.MinBudget is { } min)
            q = q.Where(o => (o.BudgetMid ?? 0) >= min);

        return Task.FromResult<IReadOnlyList<FreelanceJobOffer>>(q.Take(query.MaxResults).ToList());
    }

    public async Task<FreelanceJobOffer?> GetDetailsAsync(string externalId, CancellationToken ct = default)
    {
        var list = await SearchAsync(new FreelanceSearchQuery { MaxResults = 50 }, ct);
        return list.FirstOrDefault(o => o.ExternalId == externalId);
    }
}
