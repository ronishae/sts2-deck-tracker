using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public class OrbExecutionContext
{
    public OrbModel Orb { get; }
    public bool IsEvoke { get; }
    public decimal ExpectedValue { get; }
    public string? ForcedActorId { get; }

    public OrbExecutionContext(OrbModel orb, bool isEvoke, decimal expectedValue, string? forcedActorId = null)
    {
        Orb = orb;
        IsEvoke = isEvoke;
        ExpectedValue = expectedValue;
        ForcedActorId = forcedActorId;
    }
}
