using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Freelance;

public enum FreelanceRecommendation { Skip, Review, Accept }

public enum FreelanceJobStatus
{
    New,
    Skipped,
    Accepted,
    Running,
    AwaitingUserReview,
    Succeeded,
    Failed
}

public sealed class FreelanceSearchQuery
{
    public string Keywords { get; set; } = "C# .NET";
    public decimal? MinBudget { get; set; }
    public int MaxResults { get; set; } = 25;
    public IReadOnlyList<string> Sources { get; set; } = Array.Empty<string>();
}

public sealed class FreelanceJobOffer
{
    public required string ExternalId { get; init; }
    public required string Source { get; init; } // FlRu | Kwork | GitHub | Demo
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public decimal? BudgetMin { get; init; }
    public decimal? BudgetMax { get; init; }
    public string Currency { get; init; } = "USD";
    public List<string> Tags { get; init; } = new();
    public DateTime? DeadlineUtc { get; init; }
    public string? ClientName { get; init; }
    public string Url { get; init; } = "";
    public string RawJson { get; init; } = "";
    public DateTime FetchedAtUtc { get; init; } = DateTime.UtcNow;

    public decimal? BudgetMid =>
        BudgetMin is null && BudgetMax is null ? null
        : ((BudgetMin ?? BudgetMax)! + (BudgetMax ?? BudgetMin)!) / 2m;
}

public sealed class FreelanceScoreResult
{
    public int FeasibilityScore { get; init; }
    public double ExpectedEffortHours { get; init; }
    public decimal ExpectedRevenue { get; init; }
    public decimal ExpectedProfit { get; init; }
    public List<string> RiskFlags { get; init; } = new();
    public FreelanceRecommendation Recommendation { get; init; }
    public int FinalScore { get; init; }
    public string Explain { get; init; } = "";
}

public interface IFreelanceMarketplace
{
    string Name { get; }
    Task<IReadOnlyList<FreelanceJobOffer>> SearchAsync(FreelanceSearchQuery query, CancellationToken ct = default);
    Task<FreelanceJobOffer?> GetDetailsAsync(string externalId, CancellationToken ct = default);
}
