using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Sts2CombatAdvisor.Reflection;

/// <summary>
/// Reflection access points for read-only combat state inspection.
/// Mirrors the subset of Sts2UndoMod's ReflectionCache that's relevant for
/// damage prediction (no visual / scene-tree fields). Startup logs every
/// NULL entry so a game-update rename is immediately visible.
/// </summary>
internal static class CombatReflection
{
    public static readonly FieldInfo? CombatManagerStateField =
        AccessTools.Field(typeof(CombatManager), "_state");

    public static readonly FieldInfo? CreatureHpField =
        AccessTools.Field(typeof(Creature), "_currentHp");
    public static readonly FieldInfo? CreatureMaxHpField =
        AccessTools.Field(typeof(Creature), "_maxHp");
    public static readonly FieldInfo? CreatureBlockField =
        AccessTools.Field(typeof(Creature), "_block");

    public static readonly FieldInfo? PcsEnergyField =
        AccessTools.Field(typeof(PlayerCombatState), "_energy");
    public static readonly FieldInfo? PcsStarsField =
        AccessTools.Field(typeof(PlayerCombatState), "_stars");

    public static readonly FieldInfo? PowerAmountField =
        AccessTools.Field(typeof(PowerModel), "_amount");

    // AttackIntent — STS2 stores damage as `DamageCalc: Func<int>` (computed
    // dynamically per relic/power state) and multi-hit count as `Repeats: int`.
    // Per-frame access means the value reflects current Strength/Vulnerable etc.
    public static readonly Type? AttackIntentType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent");

    public static readonly PropertyInfo? AttackIntentDamageCalcProp =
        AttackIntentType != null ? AccessTools.Property(AttackIntentType, "DamageCalc") : null;
    public static readonly PropertyInfo? AttackIntentRepeatsProp =
        AttackIntentType != null ? AccessTools.Property(AttackIntentType, "Repeats") : null;

    public static int GetAttackIntentDamage(object intent)
    {
        if (AttackIntentDamageCalcProp?.GetValue(intent) is not Delegate calc) return 0;
        try
        {
            var result = calc.DynamicInvoke();
            return result == null ? 0 : Convert.ToInt32(result);
        }
        catch { return 0; }
    }

    public static int GetAttackIntentRepeats(object intent)
    {
        var v = AttackIntentRepeatsProp?.GetValue(intent);
        return v is int n && n > 0 ? n : 1;
    }

    static CombatReflection()
    {
        var nulls = new List<string>();
        void Check(string name, object? m) { if (m == null) nulls.Add(name); }
        Check(nameof(CombatManagerStateField), CombatManagerStateField);
        Check(nameof(CreatureHpField), CreatureHpField);
        Check(nameof(CreatureMaxHpField), CreatureMaxHpField);
        Check(nameof(CreatureBlockField), CreatureBlockField);
        Check(nameof(PcsEnergyField), PcsEnergyField);
        Check(nameof(PcsStarsField), PcsStarsField);
        Check(nameof(PowerAmountField), PowerAmountField);
        Check(nameof(AttackIntentType), AttackIntentType);
        Check(nameof(AttackIntentDamageCalcProp), AttackIntentDamageCalcProp);
        Check(nameof(AttackIntentRepeatsProp), AttackIntentRepeatsProp);

        if (nulls.Count > 0)
            MainFile.Logger.Warn($"[Reflection] {nulls.Count} member(s) NULL — game update may have changed: {string.Join(", ", nulls)}");
        else
            MainFile.Logger.Info("[Reflection] all targets resolved.");
    }
}
