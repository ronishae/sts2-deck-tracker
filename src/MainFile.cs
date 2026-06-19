using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace DeckTracker.DeckTrackerCode;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    private const string ModId = "DeckTracker"; 
    
    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        Harmony harmony = new(ModId);
        
        harmony.PatchAll();
    }
}