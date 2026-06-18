using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2CombatAdvisor;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "Sts2CombatAdvisor";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; }
        = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        try
        {
            var harmony = new Harmony(ModId);
            harmony.PatchAll(typeof(MainFile).Assembly);
            Logger.Info("[CombatAdvisor] Harmony patches applied.");

            // Enable empty-hand draw-engine recognition (UnceasingTop draw-on-empty + ScreamingFlagon
            // payoff) in the planner. The env gate (STS2_SIM_DRAW_ENGINE) can't be set in-game, and the
            // feature is a no-op for any deck without those relics, so the overlay turns it on directly.
            Sts2CombatAI.Sim.AnalyticalSimulator.UseDrawEngine = true;

            if (Engine.GetMainLoop() is SceneTree tree)
                RecommendationOverlayService.Install(tree);

            Logger.Info("[CombatAdvisor] initialized (v0.3.0 — in-combat recommendation overlay).");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[CombatAdvisor] init failed: {ex.Message}");
        }
    }
}
