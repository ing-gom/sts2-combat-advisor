namespace Sts2CombatAI;

/// Source-include stub. The linked Sts2CombatAI planner files reference
/// MainFile.Logger directly (some without null-safe `?.`), so provide a non-null
/// Logger. Distinct namespace from this mod's Sts2CombatAdvisor.MainFile — no conflict.
internal static class MainFile
{
    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(
        "Sts2CombatAI-via-Overlay",
        MegaCrit.Sts2.Core.Logging.LogType.Generic);

    // Some planner files reference MainFile.Initialize; harmless no-op stub.
    public static void Initialize() { }
}
