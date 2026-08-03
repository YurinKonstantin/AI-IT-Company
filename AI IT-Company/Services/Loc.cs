using Windows.ApplicationModel.Resources;

namespace AI_IT_Company;

/// <summary>UI strings via PRI / Resources.resw. Falls back to key or English defaults.</summary>
public static class Loc
{
    private static readonly ResourceLoader? Loader = TryCreate();

    private static ResourceLoader? TryCreate()
    {
        try { return new ResourceLoader(); }
        catch { return null; }
    }

    public static string Get(string key, string? fallback = null)
    {
        try
        {
            var s = Loader?.GetString(key);
            if (!string.IsNullOrEmpty(s)) return s;
        }
        catch { /* missing key */ }

        return fallback ?? Fallback(key);
    }

    private static string Fallback(string key) => key switch
    {
        "ModeHint_Auto" => "Auto: Interpreter picks the mode. Folder needed for existing-code tasks.",
        "ModeHint_Create" => "Create a new project. Folder optional — output goes to Output.",
        "ModeHint_Improve" => "Improve: select an existing project folder. Review diffs on Review.",
        "ModeHint_Fix" => "Fix error: select project folder. Check build log on failure.",
        "ModeHint_Document" => "Document: select folder. Writes README / ARCHITECTURE without rewriting code.",
        "ModeHint_Analyze" => "Analyze: select folder. Result is ANALYSIS.md without code edits.",
        "ModeHint_Plan" => "Plan only: no code generation — TZ.md and architecture plan.",
        "Timeline_You" => "You",
        "Status_Ready" => "Ready",
        _ => key
    };
}
