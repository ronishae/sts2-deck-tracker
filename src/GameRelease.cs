using MegaCrit.Sts2.Core.Debug;

namespace DeckTracker;

// Resolves the running Slay the Spire 2 patch version once from the game's ReleaseInfoManager —
// the same source the game uses for feedback uploads. Session-constant; null-safe fallback.
public static class GameRelease
{
    private static string? _version;

    public static string Version => _version ??= Resolve();

    private static string Resolve()
    {
        try
        {
            return ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown";
        }
        catch (Exception e)
        {
            Log.Error($"GameRelease.Resolve failed: {e.Message}");
            return "unknown";
        }
    }
}
