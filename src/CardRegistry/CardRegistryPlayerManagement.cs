using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

namespace DeckTracker;

public static partial class CardRegistry
{
    public static MegaCrit.Sts2.Core.Runs.RunState? GetLiveRunState()
    {
        var stateProperty = AccessTools.Property(typeof(MegaCrit.Sts2.Core.Runs.RunManager), "State");
        return stateProperty?.GetValue(MegaCrit.Sts2.Core.Runs.RunManager.Instance) as MegaCrit.Sts2.Core.Runs.RunState;
    }

    public static void SetPlayerLabel(int playerIndex, string label)
    {
        PlayerLabels[playerIndex] = label;
        Log.Debug($"SetPlayerLabel. Player {playerIndex}: {label}");
    }

    // Records a player's stable NetId -> ordered index mapping (used only for row colour/ordering).
    public static void SetPlayerIndexForNetId(string netId, int playerIndex)
    {
        _playerIndexByNetId[netId] = playerIndex;
    }

    public static string GetPlayerDisplayName(Player player)
    {
        var characterTitle = player.Character.Title.GetFormattedText();
        if (player.RunState.Players.Count <= 1)
        {
            return characterTitle;
        }

        var netKey = player.NetId.ToString();
        if (_steamNameCache.TryGetValue(netKey, out var cachedName))
        {
            return cachedName;
        }

        // The Steam name lookup can throw or return null on a client where a remote player's name
        // is not yet cached, or while NetService is still initialising. This runs inside synchronized
        // game hooks during co-op, so any throw here would desync the run — fall back to the character title.
        try
        {
            var netService = RunManager.Instance?.NetService;
            if (netService?.Platform == null)
            {
                Log.Warn("GetPlayerDisplayName. NetService unavailable, using character title.");
                return characterTitle;
            }

            var steamName = PlatformUtil.GetPlayerNameRaw(netService.Platform, player.NetId);
            if (string.IsNullOrEmpty(steamName))
            {
                return characterTitle;
            }

            _steamNameCache[netKey] = steamName;
            return steamName;
        }
        catch (Exception e)
        {
            Log.Error($"GetPlayerDisplayName failed, using character title: {e.Message}");
            return characterTitle;
        }
    }

    private static void RestoreLiveInstances()
    {
        var run = GetLiveRunState();
        if (run == null)
        {
            return;
        }

        BeginDeckScan();
        for (var playerIdx = 0; playerIdx < run.Players.Count; playerIdx++)
        {
            var player = run.Players[playerIdx];
            var netId = player.NetId.ToString();
            _playerIndexByNetId[netId] = playerIdx;
            PlayerLabels[playerIdx] = GetPlayerDisplayName(player);

            foreach (var relic in player.Relics)
            {
                RelicNameCache[relic.Id.Entry] = relic.Title.GetFormattedText();
                var stats = GetOrCreateRelicStats(relic.Id.Entry);
                stats.Model = relic;
                stats.IsActive = true;
                stats.PlayerIndex = playerIdx;
            }

            foreach (var card in player.Deck.Cards)
            {
                RegisterCard(card, netId, isDeckScan: true);
            }

            for (var i = 0; i < player.PotionSlots.Count; i++)
            {
                var potion = player.PotionSlots[i];
                if (potion == null)
                {
                    continue;
                }

                var existingId = PotionInstanceIds.FirstOrDefault(kvp => kvp.Key == potion).Value;

                if (string.IsNullOrEmpty(existingId))
                {
                    var idPrefix = $"POTION_{potion.Id.Entry}_";
                    existingId = EntityLedger.Values.OfType<PotionStats>()
                        .FirstOrDefault(p => p.Model == null && MatchesPotionType(p.Id, idPrefix)
                            && (p.OwnerNetId == netId || p.OwnerNetId == null))?.Id;

                    if (string.IsNullOrEmpty(existingId))
                    {
                        _potionCounter++;
                        existingId = $"POTION_{potion.Id.Entry}_{_potionCounter}";

                        string displayName = potion.Title?.GetFormattedText() ?? potion.Id.Entry ?? "Unknown Potion";
                        EntityLedger[existingId] = new PotionStats
                        {
                            Id = existingId,
                            DisplayName = displayName,
                            FloorObtained = 1,
                            OwnerNetId = netId,
                            IsActive = true
                        };
                    }
                    PotionInstanceIds[potion] = existingId;
                }

                var potionStat = (PotionStats)EntityLedger[existingId];
                potionStat.OwnerNetId ??= netId; // Backfill owner for saves made before owner tracking.
                potionStat.Model = potion;
                potionStat.PlayerIndex = playerIdx;
            }
        }
        Log.Debug("RestoreLiveInstances. Live object references restored.");
    }
}
