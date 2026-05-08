namespace DeckTracker;

public class DamageIncrement(string cardId)
{
    public required string CardId = cardId;
    public int HallwayDamage { get; set; }
    public int EliteDamage { get; set; }
    public int BossDamage { get; set; }
    public int HallwayConnectedForget { get; set; }
    public int EliteConnectedForget { get; set; }
    public int BossConnectedForget { get; set; }
}