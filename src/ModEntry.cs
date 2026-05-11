using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace DeckTracker;

[ModInitializer("Initialize")]
public static class ModEntry
{
    private static Harmony? _harmony;

    public static void Initialize()
    {
        if (_harmony != null) return;
        _harmony = new Harmony("com.yourname.sts2.deck_tracker");

        // --- Core Lifecycle Hooks ---
        PatchHook(nameof(Hook.AfterRoomEntered), nameof(HookPatches.AfterRoomEnteredPostfix));
        PatchHook(nameof(Hook.BeforeCombatStart), nameof(HookPatches.BeforeCombatStartPostfix));
        PatchHook(nameof(Hook.AfterCombatEnd), nameof(HookPatches.AfterCombatEndPostfix)); 
        
        // --- Damage Hooks ---
        PatchHook(nameof(Hook.AfterDamageGiven), nameof(HookPatches.AfterDamageGivenPostfix));
        PatchHook(nameof(Hook.AfterForge), nameof(HookPatches.AfterForgePostfix));
        
        // --- Card Removal Hook ---
        PatchHook(nameof(Hook.BeforeCardRemoved), nameof(HookPatches.BeforeCardRemovedPostfix));
        
        // --- Card Event Hooks ---
        PatchHook(nameof(Hook.AfterCardDrawn), nameof(HookPatches.AfterCardDrawnPostfix));
        PatchHook(nameof(Hook.AfterCardChangedPiles), nameof(HookPatches.AfterCardChangedPilesPostfix));
        PatchHook(nameof(Hook.BeforeCardPlayed), nameof(HookPatches.BeforeCardPlayedPostfix));
        PatchHook(nameof(Hook.AfterCardPlayed), nameof(HookPatches.AfterCardPlayedPostfix));
        PatchHook(nameof(Hook.BeforePowerAmountChanged), nameof(HookPatches.BeforePowerAmountChangedPostfix));
        
        MethodInfo poisonOriginal = AccessTools.Method(typeof(PoisonPower), nameof(PoisonPower.AfterSideTurnStart));
        MethodInfo poisonPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PoisonAfterSideTurnStartPrefix));
        MethodInfo poisonPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PoisonAfterSideTurnStartPostfix));
        
        _harmony!.Patch(poisonOriginal, 
            prefix: new HarmonyMethod(poisonPrefix), 
            postfix: new HarmonyMethod(poisonPostfix));
        
        MethodInfo fumesOriginal = AccessTools.Method(typeof(NoxiousFumesPower), nameof(NoxiousFumesPower.AfterSideTurnStart));
        MethodInfo fumesPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.FumesAfterSideTurnStartPrefix));
        MethodInfo fumesPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.FumesAfterSideTurnStartPostfix));
        
        _harmony!.Patch(fumesOriginal, 
            prefix: new HarmonyMethod(fumesPrefix), 
            postfix: new HarmonyMethod(fumesPostfix));
        
        MethodInfo waveOriginal = AccessTools.Method(typeof(CorrosiveWavePower), nameof(CorrosiveWavePower.AfterCardDrawn));
        MethodInfo wavePrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.WaveAfterCardDrawnPrefix));
        MethodInfo wavePostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.WaveAfterCardDrawnPostfix));
        
        _harmony!.Patch(waveOriginal, 
            prefix: new HarmonyMethod(wavePrefix), 
            postfix: new HarmonyMethod(wavePostfix));
        
        PatchHook(nameof(Hook.AfterDiedToDoom), nameof(HookPatches.AfterDiedToDoomPostfix));
        
        MethodInfo doomKillOriginal = AccessTools.Method(typeof(DoomPower), nameof(DoomPower.DoomKill));
        MethodInfo doomKillPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.DoomKillPrefix));
        _harmony!.Patch(doomKillOriginal, prefix: new HarmonyMethod(doomKillPrefix));
        
        MethodInfo countdownOriginal = AccessTools.Method(typeof(CountdownPower), nameof(CountdownPower.AfterSideTurnStart));
        MethodInfo countdownPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.CountdownAfterSideTurnStartPrefix));
        MethodInfo countdownPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.CountdownAfterSideTurnStartPostfix));
        
        _harmony!.Patch(countdownOriginal, 
            prefix: new HarmonyMethod(countdownPrefix), 
            postfix: new HarmonyMethod(countdownPostfix));
        
        // --- LIGHTNING ORB PATTERN ---
        MethodInfo lightningPassive = AccessTools.Method(typeof(LightningOrb), nameof(LightningOrb.Passive));
        _harmony!.Patch(lightningPassive, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePostfix))));

        MethodInfo lightningEvoke = AccessTools.Method(typeof(LightningOrb), nameof(LightningOrb.Evoke));
        _harmony!.Patch(lightningEvoke, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePostfix))));

        // --- GLASS ORB PATTERN ---
        MethodInfo glassPassive = AccessTools.Method(typeof(GlassOrb), nameof(GlassOrb.Passive));
        _harmony!.Patch(glassPassive, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePostfix))));

        MethodInfo glassEvoke = AccessTools.Method(typeof(GlassOrb), nameof(GlassOrb.Evoke));
        _harmony!.Patch(glassEvoke, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePostfix))));
        
        MethodInfo darkPassive = AccessTools.Method(typeof(DarkOrb), nameof(DarkOrb.Passive));
        _harmony!.Patch(darkPassive, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePostfix))));

        MethodInfo darkEvoke = AccessTools.Method(typeof(DarkOrb), nameof(DarkOrb.Evoke));
        _harmony!.Patch(darkEvoke, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePostfix))));
        
        // Keep the Channel patch exactly as you have it, because OrbCmd is a static class!
        MethodInfo orbChannel = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.OrbCmd), nameof(MegaCrit.Sts2.Core.Commands.OrbCmd.Channel), new Type[] { typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext), typeof(OrbModel), typeof(MegaCrit.Sts2.Core.Entities.Players.Player) });
        _harmony!.Patch(orbChannel, postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbChannelPostfix))));
        
        MethodInfo tempBefore = AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.BeforeApplied));
        _harmony!.Patch(tempBefore, prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusApplyPrefix))), postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusApplyPostfix))));

        MethodInfo tempAfterAmount = AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.AfterPowerAmountChanged));
        _harmony!.Patch(tempAfterAmount, prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusApplyPrefix))), postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusApplyPostfix))));

        MethodInfo tempEnd = AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.AfterTurnEnd));
        _harmony!.Patch(tempEnd, prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusExpirePrefix))), postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusExpirePostfix))));
    }

    private static void PatchHook(string hookName, string postfixName)
    {
        MethodInfo original = AccessTools.Method(typeof(Hook), hookName);
        MethodInfo postfix = AccessTools.Method(typeof(HookPatches), postfixName);
        _harmony!.Patch(original, postfix: new HarmonyMethod(postfix));
    }
}

internal static class HookPatches
{
    private static bool _overlayScheduled;
    
    public static void AfterRoomEnteredPostfix(IRunState runState, AbstractRoom room)
    {
        int currentFloor = ExtractFloorNum(runState);
        List<string> activeDeckIds = ScanDeckForCards(runState);
        
        // Sync the deck the moment we step into a new room to catch Upgrades/Transforms immediately
        CardRegistry.SyncDeckState(currentFloor, activeDeckIds);
    }
    
    public static void BeforeCardRemovedPostfix(IRunState runState, CardModel card)
    {
        int currentFloor = ExtractFloorNum(runState);
        CardRegistry.HandleRemove(card, currentFloor);
    }
    
    // Catches cards that enter the hand via Generation, Exhaust, or Discard retrieval!
    public static void AfterCardChangedPilesPostfix(IRunState runState, ICombatState? combatState, CardModel card, PileType oldPile, AbstractModel? source)
    {
        // We only care if we are actively in combat
        if (combatState == null) return;

        try
        {
            // If the card is now in the Hand, but it didn't come from the Draw pile 
            // (because our other AfterCardDrawn hook already handles standard draws)
            if (card.Pile != null && card.Pile.Type == PileType.Hand && oldPile != PileType.Draw)
            {
                CardRegistry.RegisterCard(card);
                CardRegistry.AddDraw(card);
                CardRegistry.ForcePublish();
            }
        }
        catch { /* Fails silently if Pile data is missing */ }
    }
    
    public static void BeforeCombatStartPostfix(IRunState? runState, CombatState? combatState)
    {
        string seed = ExtractRunSeed(runState);
        CardRegistry.SyncRun(seed);
    
        int currentFloor = ExtractFloorNum(runState);
        string combatType = GetCombatType(runState);
        
        // 1. Scan deck first to register cards and get the active list
        List<string> activeDeckIds = ScanDeckForCards(runState);
        
        // 2. Start combat and pass the active list so it can diff against the tracker history
        CardRegistry.StartCombat(combatType, currentFloor, activeDeckIds);
        
        CardRegistry.ForcePublish();

        if (!_overlayScheduled)
        {
            _overlayScheduled = true;
            DeckTrackerOverlay.EnsureCreated();
        }
    }

    public static void AfterCombatEndPostfix(IRunState? runState, CombatState? combatState)
    {
        CardRegistry.ProcessCombatEnd();
    }

    // --- NEW: CLEAN EVENT HOOKS ---

    // Catches ALL cards entering your hand
    public static void AfterCardDrawnPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)
    {
        CardRegistry.RegisterCard(card);
        CardRegistry.AddDraw(card);
    }

    // Catches manual plays (spending energy)
    public static void BeforeCardPlayedPostfix(ICombatState combatState, CardPlay cardPlay)
    {
        try
        {
            // We use reflection here just in case CardPlay changes in future Early Access builds,
            // but it reliably holds a reference to the CardModel being played.
            var cardProp = cardPlay.GetType().GetProperty("Card");
            if (cardProp?.GetValue(cardPlay) is CardModel card)
            {
                CardRegistry.CurrentPlayingCard.Value = card;
                CardRegistry.RegisterCard(card);
                // Do not count Replay in the times play tracker
                if (cardPlay.PlayIndex == 0)
                {
                    CardRegistry.AddPlay(card);
                }
                CardRegistry.ForcePublish();
            }
        }
        catch { /* Silently fail if STS2 changes the CardPlay object */ }
    }
    
    public static void BeforePowerAmountChangedPostfix(
        ICombatState combatState,
        PowerModel power,
        Decimal amount,
        Creature target,
        Creature? applier,
        CardModel? cardSource)
    {
        GD.Print($"[DeckTracker] Card {cardSource?.Id.Entry} did {power} power with amount {amount} {target.Name}.");
        switch (power)
        {
            case ConquerorPower:
                CardRegistry.UpdateConquerorTracker(target, amount, cardSource);
                break;
            case SwordSagePower:
            {
                CardRegistry.UpdateSovereignBladeReplayModifierTracker(amount, cardSource);
                break;
            }
            case FurnacePower:
                CardRegistry.UpdateFurnaceHistory(amount, cardSource);
                break;
            case NoxiousFumesPower:
                if (amount > 0) CardRegistry.AddFumesShares(amount, cardSource);
                break;
            case CorrosiveWavePower:
                if (amount > 0) 
                {
                    CardRegistry.AddCorrosiveWaveShares(amount, cardSource);
                }
                else if (amount < 0) 
                {
                    // The power is removed at end of turn. Just dump the bucket!
                    CardRegistry.ClearCorrosiveWaveShares();
                }
                break;
            case PoisonPower:
                if (amount > 0)
                {
                    // Is Fumes currently the thing applying this Poison?
                    if (CardRegistry.IsNoxiousFumesExecuting.Value)
                    {
                        lock (CardRegistry.SyncRoot)
                        {
                            decimal totalFumes = CardRegistry.FumesShares.Sum(x => x.Shares);
                            if (totalFumes > 0)
                            {
                                // Distribute the new Poison proportionally based on who applied the Fumes
                                foreach (var share in CardRegistry.FumesShares)
                                {
                                    decimal proportion = share.Shares / totalFumes;
                                    CardRegistry.AddPoisonSharesById(target, amount * proportion, share.TrackingId);
                                }
                            }
                        }
                    }
                    else if (CardRegistry.IsCorrosiveWaveExecuting.Value)
                    {
                        lock (CardRegistry.SyncRoot)
                        {
                            decimal totalWave = CardRegistry.CorrosiveWaveShares.Sum(x => x.Shares);
                            if (totalWave > 0)
                            {
                                foreach (var share in CardRegistry.CorrosiveWaveShares)
                                {
                                    decimal proportion = share.Shares / totalWave;
                                    CardRegistry.AddPoisonSharesById(target, amount * proportion, share.TrackingId);
                                }
                            }
                        }
                    }
                    else
                    {
                        GD.Print($"[DeckTracker] Adding poison power {amount} from card {cardSource?.Id.Entry}.");
                        CardRegistry.AddPoisonShares(target, amount, cardSource);
                    }
                }
                else if (amount < 0)
                {
                    // Amount is negative (e.g., -1), so we use Math.Abs to pass a positive 1 to our math function.
                    CardRegistry.RemovePoisonSharesProportionally(target, Math.Abs(amount));
                }
                break;
            case CountdownPower:
                if (amount > 0) CardRegistry.AddCountdownHistory(amount, cardSource);
                break;
            case DoomPower:
                if (amount > 0)
                {
                    // Is Countdown currently applying this Doom?
                    if (CardRegistry.IsCountdownExecuting.Value)
                    {
                        lock (CardRegistry.SyncRoot)
                        {
                            decimal remainingDoom = amount;

                            // Read the Countdown history top-to-bottom and assign the Doom
                            foreach (var contribution in CardRegistry.CountdownHistory)
                            {
                                if (remainingDoom <= 0) break;

                                decimal amountToAttribute = Math.Min(remainingDoom, contribution.Amount);
                                CardRegistry.AddDoomHistoryById(target, amountToAttribute, contribution.TrackingId);

                                remainingDoom -= amountToAttribute;
                            }
                        }
                    }
                    else {
                        CardRegistry.AddDoomHistory(target, amount, cardSource);
                    }
                }
                break;
            case FocusPower:
                CardRegistry.LogFocusChange(cardSource, amount);
                break;
        }
    }
    
    public static void AfterCardPlayedPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        CardRegistry.CurrentPlayingCard.Value = null;
        string cardId = cardPlay.Card.Id.Entry ?? "";
        GD.Print($"[DeckTracker] Card {cardId} played with PlayCount: {cardPlay.PlayCount} and PlayIndex: {cardPlay.PlayIndex}.");

        if (cardId.Equals("SEEKING_EDGE")) 
        {
            CardRegistry.UpdateSeekingEdge(cardPlay.Card);
        }
        else if (cardId.Equals("SOVEREIGN_BLADE"))
        {
            CardRegistry.ProcessSovereignBladeHistory(cardPlay);
        }
    }
    
    public static void AfterForgePostfix(ICombatState combatState, decimal amount, Player forger, AbstractModel? source)
    {
        GD.Print($"[DeckTracker] Card {source?.Id.Entry} did {amount} forge.");
        if (source is CardModel card)
        {
            CardRegistry.AddForge(card, amount);
        }
        else if (source is PowerModel power)
        {
            if (power is FurnacePower)
            {
                CardRegistry.HandleFurnaceForge(amount);
            }
        }
    }
    
    // The parameters MUST be named this way, with double underscores or Harmony will have errors. Do not change.
    public static void PoisonAfterSideTurnStartPrefix(PoisonPower __instance)
    {
        if (!__instance.Owner.IsPlayer)
        {
            GD.Print($"[DeckTracker] Setting Poison context for {__instance.Owner.Name} in PREFIX");
            CardRegistry.CurrentPoisonTarget.Value = __instance.Owner;
        }
    }
    
    public static void PoisonAfterSideTurnStartPostfix(PoisonPower __instance, ref Task __result)
    {
        if (!__instance.Owner.IsPlayer)
        {
            __result = CardRegistry.AwaitPoisonTaskAsync(__result);
        }
    }
    
    public static void FumesAfterSideTurnStartPrefix(NoxiousFumesPower __instance)
    {
        CardRegistry.IsNoxiousFumesExecuting.Value = true;
    }

    public static void FumesAfterSideTurnStartPostfix(NoxiousFumesPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitFumesTaskAsync(__result);
    }
    
    public static void WaveAfterCardDrawnPrefix(CorrosiveWavePower __instance)
    {
        CardRegistry.IsCorrosiveWaveExecuting.Value = true;
    }

    public static void WaveAfterCardDrawnPostfix(CorrosiveWavePower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitCorrosiveWaveTaskAsync(__result);
    }
    
    public static void DoomKillPrefix(IReadOnlyList<Creature> creatures)
    {
        CardRegistry.CapturePendingDoomHp(creatures);
    }

    public static void AfterDiedToDoomPostfix(ICombatState combatState, IReadOnlyList<Creature> creatures)
    {
        CardRegistry.DistributeDoomDamage(creatures);
    }
    
    public static void CountdownAfterSideTurnStartPrefix(CountdownPower __instance)
    {
        CardRegistry.IsCountdownExecuting.Value = true;
    }

    public static void CountdownAfterSideTurnStartPostfix(CountdownPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitCountdownTaskAsync(__result);
    }
    
    // --- ORB WRAPPERS ---

    public static void OrbChannelPostfix(PlayerChoiceContext choiceContext, OrbModel orb, MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        CardRegistry.RegisterChanneledOrb(orb, CardRegistry.CurrentPlayingCard.Value);
    }

    public static void OrbPassivePrefix(OrbModel __instance)
    {
        GD.Print($"[DeckTracker] Trap SET for {__instance.Id.Entry} Passive");
        // Cache the PassiveVal before the Glass Orb decrements it!
        CardRegistry.ExecutingOrb.Value = new CardRegistry.OrbExecutionContext(__instance, false, __instance.PassiveVal);
    }
    
    public static void OrbPassivePostfix(OrbModel __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitOrbExecutionTaskAsync(__result, __instance, isEvoke: false);
    }

    public static void OrbEvokePrefix(OrbModel __instance)
    {
        GD.Print($"[DeckTracker] Trap SET for {__instance.Id.Entry} Evoke");
        // Cache the EvokeVal before the execution!
        CardRegistry.ExecutingOrb.Value = new CardRegistry.OrbExecutionContext(__instance, true, __instance.EvokeVal);
    }
    
    public static void OrbEvokePostfix(OrbModel __instance, ref Task<IEnumerable<Creature>> __result)
    {
        // Smoothly wraps the Task<T> without losing the IEnumerable<Creature> return value!
        __result = CardRegistry.AwaitOrbEvokeTaskAsync(__result, __instance);
    }
    
    public static void TempFocusApplyPrefix(TemporaryFocusPower __instance)
    {
        CardRegistry.IsApplyingTemporaryFocus.Value = true;
    }

    public static void TempFocusApplyPostfix(TemporaryFocusPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitTempFocusApplyAsync(__result);
    }

    public static void TempFocusExpirePrefix(TemporaryFocusPower __instance)
    {
        CardRegistry.IsExpiringTemporaryFocus.Value = true;
    }

    public static void TempFocusExpirePostfix(TemporaryFocusPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitTempFocusExpireAsync(__result);
    }
    
    // Catches all damage dealt
    public static void AfterDamageGivenPostfix(PlayerChoiceContext? choiceContext, ICombatState combatState, Creature? dealer, DamageResult results, ValueProp props, Creature target, CardModel? cardSource)
    {
        GD.Print($"[DeckTracker] AfterDamageGivePostfix triggered");
        if (CardRegistry.CurrentPoisonTarget.Value == target && results.TotalDamage > 0)
        {
            GD.Print($"[DeckTracker] Poison detected");
            CardRegistry.DistributePoisonDamage(target, results.TotalDamage);
            
            if (!target.IsAlive) 
                CardRegistry.ClearStateForTarget(target);
                
            return;
        }
        
        // ORB INTERCEPT
        if (CardRegistry.ExecutingOrb.Value != null && results.TotalDamage > 0)
        {
            Creature player = combatState.Players[0].Creature;
            CardRegistry.DistributeOrbDamage(CardRegistry.ExecutingOrb.Value, results.TotalDamage, player);
            return; 
        }
        
        if (cardSource == null)
        {
            GD.Print($"[DeckTracker] CardSource is null and not poison or supported orb. Returning..." +
                     $"with value: {CardRegistry.ExecutingOrb.Value} and damage: {results.TotalDamage}");
            return;
        }
        
        // If the card is Sovereign Blade, process the forge distribution!
        GD.Print($"[DeckTracker] Card {cardSource.Id.Entry} did {results.TotalDamage} damage to {target.Name} with target type {cardSource.TargetType}");
        GD.Print($"[DeckTracker] {combatState.Enemies.Count} enemies in the combat via after damage");
        if (cardSource.Id.Entry.Equals("SOVEREIGN_BLADE")) 
        {
            var damageHistoryItem = new DamageHistoryItem(combatState, dealer, results, target, cardSource);
            CardRegistry.AddSovereignBladeDamageHistoryItem(damageHistoryItem);
        }
        else
        {
            CardRegistry.AddDamage(cardSource, results.TotalDamage); 
        }
    }

    // --- HELPERS & EXTRACTORS ---
    private static int ExtractFloorNum(IRunState? runState)
    {
        if (runState == null) return 1;
        return runState.TotalFloor;
    }
    
    private static string GetCombatType(IRunState? runState)
    {
        if (runState != null)
        {
            try
            {
                var roomType = runState.BaseRoom?.RoomType;
                if (roomType == RoomType.Monster) return "Hallway";
                if (roomType == RoomType.Elite) return "Elite";
                if (roomType == RoomType.Boss) return "Boss";
            }
            catch { }
        }
        return "Hallway";
    }

    private static string ExtractRunSeed(IRunState? runState)
    {
        if (runState == null) return "";
        try 
        {
            var rngProp = runState.GetType().GetProperty("Rng");
            var rng = rngProp?.GetValue(runState);
            var seedProp = rng?.GetType().GetProperty("StringSeed");
            return seedProp?.GetValue(rng)?.ToString() ?? "";
        } 
        catch { return ""; }
    }

    private static List<string> ScanDeckForCards(IRunState? runState)
    {
        List<string> deckIds = new();
        
        if (runState == null) return deckIds;
        try 
        {
            var players = runState.Players;
            if (players.Count == 0) return deckIds;

            ScanPlayerPiles(players[0], deckIds);
        } 
        catch { }
        return deckIds;
    }

    private static void ScanPlayerPiles(Player player, List<string> deckIds)
    {
        var deck = player.Deck;

        foreach (var card in deck.Cards) 
        {
            CardRegistry.RegisterCard(card);
            deckIds.Add(CardRegistry.GetTrackingId(card));
        }
    }

    private static System.Collections.IEnumerable? GetEnumerableProperty(object obj, string propertyName)
    {
        return obj.GetType().GetProperty(propertyName)?.GetValue(obj) as System.Collections.IEnumerable;
    }
}