using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    public static readonly object SyncRoot = new();

    public static Dictionary<string, EntityStats> EntityLedger = new();
    private static string _currentRunSeed = "";
    // Set by BeginNewRun when the game starts a fresh run; makes the next SyncRun reset rather than resume,
    // even if the seed matches a previous run's save (same-seed restart).
    private static bool _pendingFreshRun;

    private static int _currentAct = 1;
    private static string _currentCombatType = "Unknown";

    private static HashSet<string> _incrementedThisCombat = new();

    // Maps player index (order in IRunState.Players) to the character's display name
    public static readonly Dictionary<int, string> PlayerLabels = new();

    // Maps a player's stable NetId (string) to their current ordered index. Rebuilt every deck scan.
    // Used only to colour/order rows (CardStats.PlayerIndex) — never baked into a tracking ID.
    private static readonly Dictionary<string, int> _playerIndexByNetId = new();

    // Sentinel owner key for cards with no resolvable owning player (e.g. enemy-owned cards).
    private const string UnknownOwnerKey = "NONE";

    // Per-physical-copy index, so identical copies are distinct rows in the overlay (which then stacks them).
    // _cardInstanceIds maps a live CardModel -> its copy index. For deck cards it is rebuilt deterministically
    // every scan (count-capped ordinal) so it stays stable across multiplayer client re-syncs; generated/combat
    // cards fall back to a persistent per-{baseId}_F{floor} counter.
    private static readonly Dictionary<CardModel, int> _cardInstanceIds = new();
    private static readonly Dictionary<string, int> _cardInstanceCounters = new();
    // Transient per-scan ordinal counters, keyed by "{netId}|{baseId}_F{floor}_U{upgrade}_{enchant}".
    private static readonly Dictionary<string, int> _deckScanOrdinals = new();
    // Shared "created by" map: the model that created a transient combat card. Auto-filled from the
    // game's CloneOf for clones (Anger); generated cards like Shiv will populate this manually from the
    // playing card/potion/power context (planned follow-up). Cleared each combat (transient cards only).
    private static readonly Dictionary<CardModel, AbstractModel> _cardCreatedBy = new();
    // Memoized origin: maps a card to the persistent deck card its damage should roll up to, so we never
    // re-walk the chain. A clone inherits its creator's cached origin in O(1); a non-created card maps to
    // itself (or its DeckVersion). Cleared each combat.
    private static readonly Dictionary<CardModel, CardModel> _cardOrigin = new();
    // Generated card model -> the tracking id of the source (card/power/potion/relic) that created it,
    // captured from whatever was executing at generation time. Distinct from the self-clone seam
    // (_cardCreatedBy): this covers cross-source generation (Shiv, etc.). The generated card keeps its own
    // row; its damage is also summed onto this creator's generated bucket. Cleared each combat.
    private static readonly Dictionary<CardModel, string> _cardGeneratedBy = new();
    // The IMMEDIATE generator (one step up the chain, before rolling up to the root) for each generated card
    // model. Parallel to _cardGeneratedBy; used to build the overlay's multi-level generation tree. Cleared
    // each combat alongside _cardGeneratedBy.
    private static readonly Dictionary<CardModel, string> _cardGeneratedByImmediate = new();
    // The tracking id each live card model was last registered under this combat. When a card's identity
    // changes mid-combat (upgrade/downgrade/enchant — Cunning Potion, Armaments, Drain Power, enemy
    // debuffs), its tracking id changes; this lets us migrate the old entry's stats onto the new id so the
    // card stays one row instead of splitting. Cleared each combat (ids are recomputed fresh per combat).
    private static readonly Dictionary<CardModel, string> _cardCurrentTrackingId = new();
    // Tracking id each deck card was marked removed under, keyed on the deck-master CardModel object.
    // Lets us revive the original entry when the SAME object returns to the deck under a new tracking
    // id (e.g. a Thieving Hopper steal/return, where the game rewrites FloorAddedToDeck to the current
    // floor and so changes the id). Persists across combats within a run; cleared only on ResetRun
    // because the stolen card is typically returned mid-combat but the deck is re-scanned afterward.
    private static readonly Dictionary<CardModel, string> _removedDeckCardIds = new();

    // Cards that always share one tracking entry regardless of how many instances are generated mid-combat.
    private static readonly HashSet<string> SingletonCardIds = new() { "SOVEREIGN_BLADE" };

    // Caches resolved Steam names by NetId so we don't query the platform on every room/combat.
    // Only successful resolutions are cached, so a not-yet-available name is retried next time.
    private static readonly Dictionary<string, string> _steamNameCache = new();

    // Tracks the card currently being played
    private static readonly AsyncLocal<CardModel?> _currentPlayingCard = new();

    // Cards added to hand during a play (to wait for enchantments)
    private static readonly AsyncLocal<List<CardModel>?> _deferredDraws = new();

    // --- UNIFIED TRACKER REGISTRY ---

    public static readonly Dictionary<string, GenericDamageTracker> SimpleDamageTrackers = new()
    {
        { "FLAME_BARRIER_POWER", new GenericDamageTracker("FLAME_BARRIER_POWER") },
        { "JUGGERNAUT_POWER", new GenericDamageTracker("JUGGERNAUT_POWER") },
        { "HAUNT_POWER", new GenericDamageTracker("HAUNT_POWER") },
        { "SPEEDSTER_POWER", new GenericDamageTracker("SPEEDSTER_POWER") },
        { "THUNDER_POWER", new GenericDamageTracker("THUNDER_POWER") },
        { "HAILSTORM_POWER", new GenericDamageTracker("HAILSTORM_POWER") },
        { "THORNS_POWER", new GenericDamageTracker("THORNS_POWER") },
        { "SERPENT_FORM_POWER", new GenericDamageTracker("SERPENT_FORM_POWER") },
        { "BLACK_HOLE_POWER", new GenericDamageTracker("BLACK_HOLE_POWER") },
        { "SLEIGHT_OF_FLESH_POWER", new GenericDamageTracker("SLEIGHT_OF_FLESH_POWER") },
        { "INFERNO_POWER", new GenericDamageTracker("INFERNO_POWER") },
        { "OUTBREAK_POWER", new GenericDamageTracker("OUTBREAK_POWER") },
        { "SMOKESTACK_POWER", new GenericDamageTracker("SMOKESTACK_POWER") },
    };

    public static readonly Dictionary<string, TargetedDamageTracker> TargetedTrackers = new()
    {
        { "STRANGLE_POWER", new TargetedDamageTracker("STRANGLE_POWER") },
        { "OBLIVION_POWER", new TargetedDamageTracker("OBLIVION_POWER") },
        // Enemy-side debuff (Powdered Demise potion); per-target ledger handles multiple enemies.
        { "DEMISE_POWER", new TargetedDamageTracker("DEMISE_POWER") },
    };

    public static readonly Dictionary<string, BuffHandoffTracker> HandoffTrackers = new()
    {
        { "DEMON_FORM_POWER", new BuffHandoffTracker("DEMON_FORM_POWER", "DEMON_FORM_POWER", HandoffStrategy.ExactFifo) },
        { "ARSENAL_POWER", new BuffHandoffTracker("ARSENAL_POWER", "ARSENAL_POWER", HandoffStrategy.ExactFifo) },
        { "PREP_TIME_POWER", new BuffHandoffTracker("PREP_TIME_POWER", "PREP_TIME_POWER", HandoffStrategy.Proportional) },
        { "SHADOW_STEP_POWER", new BuffHandoffTracker("SHADOW_STEP_POWER", "SHADOW_STEP_POWER", HandoffStrategy.ExactFifo) },
        { "MONOLOGUE_POWER", new BuffHandoffTracker("MONOLOGUE_POWER", "MONOLOGUE_POWER", HandoffStrategy.ExactFifo) },
    };

    // Powers that apply poison or deal with Strength handoffs — must remain proportional so
    // RoutePoisonApplication and RouteStrengthApplication can find the executing tracker.
    public static readonly Dictionary<string, ProportionalShareTracker> ProportionalTrackers = new()
    {
        { "RUPTURE_POWER", new ProportionalShareTracker("RUPTURE_POWER") },
        { "CORROSIVE_WAVE_POWER", new ProportionalShareTracker("CORROSIVE_WAVE_POWER") },
        { "ENVENOM_POWER", new ProportionalShareTracker("ENVENOM_POWER") },
        { "NOXIOUS_FUMES_POWER", new ProportionalShareTracker("NOXIOUS_FUMES_POWER") },
    };

    public static readonly Dictionary<string, QueueBuilderTracker> QueueTrackers = new()
    {
        { "STORM_POWER", new QueueBuilderTracker("STORM_POWER", needsFlattening: true) },
        { "TRASH_TO_TREASURE_POWER", new QueueBuilderTracker("TRASH_TO_TREASURE_POWER", needsFlattening: true) },
        { "LIGHTNING_ROD_POWER", new QueueBuilderTracker("LIGHTNING_ROD_POWER") },
        { "SPINNER_POWER", new QueueBuilderTracker("SPINNER_POWER") },
    };

    public static readonly InstancedPowerTracker InstancedTracker = new();
}
