using System;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using Sts2CombatAdvisor.Reflection;
using Sts2CombatAI.Sim;

namespace Sts2CombatAdvisor;

/// 2026-06-02 — in-combat recommendation overlay (in-process planner; no external helper).
/// Every frame it reads the live combat via the linked Sts2CombatAI planner and draws three
/// game-arrow markers: the hovered card's target enemy, the recommended first card, and a
/// colour-coded arrow under the potion worth using. All self-contained — no F10, no IPC.
public partial class RecommendationOverlayService : Node
{
    private CanvasLayer? _layer;

    // Hover-driven marker over the enemy the HOVERED card should hit (game arrow; same-name safe).
    private Control? _marker;
    private Creature? _markTarget;
    private string _lastHoverCard = "";

    // "Play this first" indicator next to the recommended card.
    private Control? _badge;
    private string _firstCard = "";
    private Control? _badgeHolder;
    private Control? _badgeShortcut;   // the card's shortcut-number node, if found
    private bool _loggedHolderTree;
    private double _pollAccum;
    private string _lastSig = "";

    // Potion-use recommendation (heuristic): defensive on crisis, offensive on a sure kill,
    // buff/utility only in elite/boss. Auto-updated; shown as a colour-coded arrow below the potion
    // (red = sure kill, blue = defend, green = elite/boss buff).
    private Control? _potionMarker;
    private PotionModel? _potionTarget;
    // 2026-06-04 — when the planner itself recommends USING a potion as the next action
    // (sequencing value, e.g. amplifier → big attack), its Id is stored here and takes
    // precedence over the heuristic advice below. "" = planner has no potion recommendation.
    private string _plannerPotionId = "";
    private NPotionContainer? _potionContainer;
    private FieldInfo? _holdersField;

    public static void Install(SceneTree tree)
    {
        var svc = new RecommendationOverlayService { Name = "RecommendationOverlayService" };
        tree.Root.CallDeferred("add_child", svc);
    }

    public override void _Ready()
    {
        _layer = new CanvasLayer { Layer = 128 };
        AddChild(_layer);

        var arrow = LoadArrowTexture();
        _marker = MakeMarker(arrow, "▼", new Color(1f, 0.35f, 0.35f));   // points at hovered card's target enemy
        _badge = MakeMarker(arrow, "▶", new Color(0.4f, 1f, 0.45f));     // "play this first"
        _potionMarker = MakeMarker(arrow, "▲", new Color(1f, 1f, 1f), 0f); // BELOW the potion, points up (colour set per advice)
        _layer.AddChild(_marker);
        _layer.AddChild(_badge);
        _layer.AddChild(_potionMarker);

        MainFile.Logger.Info("[CombatAdvisor] recommendation overlay ready.");
    }

    private static Texture2D? LoadArrowTexture()
    {
        // The game's own targeting-arrow head (used when you drag a card onto an enemy).
        try { return PreloadManager.Cache.GetTexture2D(ImageHelper.GetImagePath("ui/combat/targeting_arrow_head.png")); }
        catch (Exception ex) { MainFile.Logger.Warn($"[CombatAdvisor] arrow texture load failed: {ex.Message}"); return null; }
    }

    // Visual tuning (one place — adjust from in-game feedback).
    private const float ArrowSize = 39f;       // marker px (1.5× of 26, per in-game feedback)
    private const float MarkerGap = 2f;        // gap above the enemy head before the arrow tip
    private const float BadgeGap = 2f;         // gap above the card before the arrow tip
    private const float PotionGap = 2f;        // gap below the potion before the (up-pointing) arrow tip

    private static Control MakeMarker(Texture2D? tex, string glyph, Color color, float rotationDeg = 180f)
    {
        if (tex != null)
            return new TextureRect
            {
                Texture = tex,
                Visible = false,
                // IgnoreSize → the texture scales to Size (default KeepSize ignored Size = native px).
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(ArrowSize, ArrowSize),
                Size = new Vector2(ArrowSize, ArrowSize),
                Modulate = color,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                PivotOffset = new Vector2(ArrowSize / 2f, ArrowSize / 2f),
                RotationDegrees = rotationDeg,   // texture points UP by default; 180 flips it to point DOWN
            };
        var lbl = new Label { Text = glyph, Visible = false };
        lbl.AddThemeColorOverride("font_color", color);
        lbl.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0));
        lbl.AddThemeConstantOverride("outline_size", 8);
        lbl.AddThemeFontSizeOverride("font_size", 44);
        return lbl;
    }

    public override void _Input(InputEvent @event)
    {
        // F9 — diagnostic dump of the planner's per-card scoring for the current hand.
        if (@event is InputEventKey { Pressed: true, Keycode: Key.F9 })
            try { DumpPlannerCandidates(); } catch (Exception ex) { MainFile.Logger.Warn($"[CombatAdvisor] dump: {ex.Message}"); }
    }

    /// F9 — log every hand candidate's score breakdown. firstScore = the card played alone,
    /// secondScore = the best depth-2 follow-up after it, total = the 2-play sequence value,
    /// bestNext = which follow-up the lookahead chose. This explains why a setup card (e.g.
    /// BLOODLETTING → THE_BOMB) did or didn't beat an immediate play.
    private static void DumpPlannerCandidates()
    {
        var sim = CaptureCombat();
        if (sim == null) { MainFile.Logger.Info("[CombatAdvisor] F9: not in combat."); return; }
        var step = Sts2CombatAI.Planner.ActionPlanner.PlanNextStep(sim);   // (re)populates LastCandidates / LastEmptyReason
        var cands = Sts2CombatAI.Planner.ActionPlanner.LastCandidates;
        var emptyReason = Sts2CombatAI.Planner.ActionPlanner.LastEmptyReason;
        var sb = new System.Text.StringBuilder();
        sb.Append($"[CombatAdvisor] F9 dump — energy={sim.PlayerEnergy} hp={sim.PlayerHp}/{sim.PlayerMaxHp} aliveEnemies={sim.Enemies.Count(e => e.IsAlive)}\n");
        // Per-hand-card facts, independent of the planner trace (so stale traces can't mislead).
        foreach (var c in sim.Hand)
        {
            int sc = 0; try { sc = Sts2CombatAI.Planner.PlanScorer.Score(c, -1, sim); } catch { }
            sb.Append($"  HAND {c.Id,-20} cost={c.Cost} playable={c.IsPlayable} eGain={c.EnergyGain} afford={(c.Cost <= sim.PlayerEnergy)} score(-1)={sc}\n");
        }
        sb.Append($"  RECOMMENDED first = {(step is { } ps && ps.Card != null ? ps.Card.Id : "<none>")}\n");
        if (emptyReason != null) sb.Append($"  emptyReason = {emptyReason}\n");
        var handIds = new System.Collections.Generic.HashSet<string>(sim.Hand.Select(c => c.Id));
        foreach (var c in cands.OrderByDescending(c => c.total))
            sb.Append($"    CAND {c.id,-20} tgt={c.targetIdx,2} first={c.firstScore,7} second={c.secondScore,7} total={c.total,7} next={c.bestNextId ?? "-"}{(handIds.Contains(c.id) ? "" : "  [STALE: not in hand]")}\n");
        MainFile.Logger.Info(sb.ToString());
    }

    /// Hover marker every frame; the recommended first card + potion advice recompute on a
    /// throttle, and only when a cheap combat-state signature actually changes.
    public override void _Process(double delta)
    {
        // Hover marker (target enemy of the hovered card) — every frame, cheap.
        try { HoverUpdate(); } catch (Exception ex) { MainFile.Logger.Warn($"[CombatAdvisor] hover: {ex.Message}"); }
        // "Play this first" badge — recompute the top card only when the state changes (throttled).
        _pollAccum += delta;
        if (_pollAccum >= 0.3) { _pollAccum = 0; try { FirstCardUpdate(); } catch (Exception ex) { MainFile.Logger.Warn($"[CombatAdvisor] firstcard: {ex.Message}"); } }
        PositionMarker();        // ▼ over target enemy (hover)
        PositionBadge();         // ▶ over the recommended first card
        PositionPotionMarker();  // ▲ below the recommended potion
    }

    private void FirstCardUpdate()
    {
        var sim = CaptureCombat();
        if (sim == null) { _firstCard = ""; _badgeHolder = null; _lastSig = ""; _potionTarget = null; _plannerPotionId = ""; return; }

        string sig = Sig(sim);
        bool stateChanged = sig != _lastSig;
        if (stateChanged)
        {
            _lastSig = sig;
            // 2026-06-04 — considerPotions:true lets the planner recommend USING a potion as the
            // next action (e.g. amplifier potion before a big attack, or a finisher potion).
            var step = Sts2CombatAI.Planner.ActionPlanner.PlanNextStep(sim, considerPotions: true);
            if (step is { } ps && ps.IsPotion)
            {
                _plannerPotionId = ps.Potion!.Id;   // recommend the potion; no card badge this step
                _firstCard = "";
            }
            else
            {
                _plannerPotionId = "";
                _firstCard = (step is { } ps2 && ps2.Card != null) ? ps2.Card.Id : "";
            }
        }

        // Potion marker: a planner potion recommendation (gold ▲ = "use now") takes precedence;
        // otherwise fall back to the lightweight heuristic advice (defend/kill/buff colours).
        if (!string.IsNullOrEmpty(_plannerPotionId))
        {
            _potionTarget = FindPotionById(_plannerPotionId);
            if (_potionMarker != null && _potionTarget != null) _potionMarker.Modulate = PotionPlanner;
        }
        else
        {
            var (pot, col) = ComputePotionAdvice(sim);
            _potionTarget = pot;
            if (_potionMarker != null && pot != null) _potionMarker.Modulate = col;
        }

        if (!stateChanged) return;     // badge holder lookup only needs to run on state change
        _badgeHolder = string.IsNullOrEmpty(_firstCard) ? null : FindHolderForCard(_firstCard);
        _badgeShortcut = _badgeHolder != null ? FindShortcutNode(_badgeHolder) : null;
        if (_badgeHolder != null && !_loggedHolderTree)   // one-time diag: dump the card node tree.
        {
            _loggedHolderTree = true;
            MainFile.Logger.Info("[CombatAdvisor] card node tree:\n" + DumpTree(_badgeHolder, 0));
            MainFile.Logger.Info("[CombatAdvisor] shortcut node = " + (_badgeShortcut?.Name.ToString() ?? "<none found>"));
        }
    }

    private static readonly string[] ShortcutHints = { "shortcut", "hotkey", "keyhint", "inputhint", "keybind", "number", "index" };
    private static Control? FindShortcutNode(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is Control c)
            {
                string n = c.Name.ToString().ToLowerInvariant();
                foreach (var h in ShortcutHints) if (n.Contains(h)) return c;
            }
            var deep = FindShortcutNode(child);
            if (deep != null) return deep;
        }
        return null;
    }

    private static string DumpTree(Node root, int depth)
    {
        if (depth > 4) return "";
        var sb = new System.Text.StringBuilder();
        sb.Append(' ', depth * 2).Append(root.Name).Append(" [").Append(root.GetType().Name).Append("]\n");
        foreach (var child in root.GetChildren()) sb.Append(DumpTree(child, depth + 1));
        return sb.ToString();
    }

    private static string Sig(SimState sim)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in sim.Hand) sb.Append(c.Id).Append(',');
        sb.Append('|').Append(sim.PlayerEnergy);
        foreach (var e in sim.Enemies) sb.Append('|').Append(e.Hp);
        return sb.ToString();
    }

    private static Control? FindHolderForCard(string cardId)
    {
        var container = NCombatRoom.Instance?.Ui?.Hand?.CardHolderContainer;
        if (container == null) return null;
        foreach (var child in container.GetChildren())
            if (child is Control ctrl && ctrl.Visible
                && string.Equals(GetHolderCardId(child), cardId, System.StringComparison.OrdinalIgnoreCase))
                return ctrl;
        return null;
    }

    private void PositionBadge()
    {
        if (_badge == null) return;
        Control? anchor = (_badgeShortcut != null && GodotObject.IsInstanceValid(_badgeShortcut)) ? _badgeShortcut
                        : (_badgeHolder != null && GodotObject.IsInstanceValid(_badgeHolder)) ? _badgeHolder : null;
        if (anchor == null) { if (_badge.Visible) _badge.Visible = false; return; }
        // down-arrow above the card's top-centre (tip points down at the card).
        Vector2 topCenter = anchor.GlobalPosition + new Vector2(anchor.Size.X / 2f, 0f);
        _badge.Position = topCenter + new Vector2(-ArrowSize / 2f, -ArrowSize - BadgeGap);
        _badge.Visible = true;
    }

    // Place the ▶ marker over the enemy the next recommended card should hit.
    private void PositionMarker()
    {
        if (_marker == null) return;
        try
        {
            var node = _markTarget != null ? NCombatRoom.Instance?.GetCreatureNode(_markTarget) : null;
            var vis = node?.Visuals;
            if (vis == null || !GodotObject.IsInstanceValid(vis)) { if (_marker.Visible) _marker.Visible = false; return; }
            // Anchor at the creature's INTENT marker (above its head, on the sprite), world→screen.
            // Place the down-arrow so its tip sits just above the head, centred horizontally.
            Vector2 screen = vis.IntentPosition.GetGlobalTransformWithCanvas().Origin;
            _marker.Position = screen + new Vector2(-ArrowSize / 2f, -ArrowSize - MarkerGap);
            _marker.Visible = true;
        }
        catch { if (_marker.Visible) _marker.Visible = false; }
    }

    private void HoverUpdate()
    {
        // The game already tracks the hovered/focused card holder; read its CardModel via reflection
        // (robust to exact member names). Only recompute the target when the hovered card changes.
        var holder = NCombatRoom.Instance?.Ui?.Hand?.FocusedHolder;
        if (holder == null) { _markTarget = null; _lastHoverCard = ""; return; }
        string cardId = GetHolderCardId(holder);
        if (cardId == _lastHoverCard) return;
        _lastHoverCard = cardId;
        _markTarget = string.IsNullOrEmpty(cardId) ? null : ComputeTargetFor(cardId);
    }

    private static SimState? CaptureCombat()
    {
        var cm = CombatManager.Instance;
        if (cm == null) return null;
        if (CombatReflection.CombatManagerStateField?.GetValue(cm) is not CombatState cs) return null;
        var player = cs.Players.FirstOrDefault();
        if (player == null) return null;
        return StateSnapshotter.Capture(player);
    }

    // CardModel of the hovered holder via reflection (robust to exact member names) → Id.Entry.
    private static string GetHolderCardId(object holder)
    {
        try
        {
            var t = holder.GetType();
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var p in t.GetProperties(BF))
                if (p.GetIndexParameters().Length == 0 && typeof(CardModel).IsAssignableFrom(p.PropertyType)
                    && p.GetValue(holder) is CardModel cmp)
                    return cmp.Id.Entry;
            foreach (var f in t.GetFields(BF))
                if (typeof(CardModel).IsAssignableFrom(f.FieldType) && f.GetValue(holder) is CardModel cmf)
                    return cmf.Id.Entry;
        }
        catch { }
        return "";
    }

    // The enemy the AI would hit with the hovered card (best PlanScorer target). Null for non-attacks.
    private static Creature? ComputeTargetFor(string cardId)
    {
        var sim = CaptureCombat();
        if (sim == null) return null;
        var card = sim.Hand.FirstOrDefault(c => string.Equals(c.Id, cardId, System.StringComparison.OrdinalIgnoreCase));
        // Show the marker for any card that targets ONE enemy — attacks AND enemy-targeting skills
        // (e.g. apply Vulnerable). AOE (AllEnemies), random, self and untargeted cards get no marker.
        if (card == null || card.Target != MegaCrit.Sts2.Core.Entities.Cards.TargetType.AnyEnemy) return null;
        int best = -1, bestScore = int.MinValue;
        for (int idx = 0; idx < sim.Enemies.Count; idx++)
        {
            if (!sim.Enemies[idx].IsAlive) continue;
            int s = Sts2CombatAI.Planner.PlanScorer.Score(card, idx, sim);
            if (s > bestScore) { bestScore = s; best = idx; }
        }
        return best >= 0 ? sim.Enemies[best].SourceRef : null;
    }

    // Arrow colours per advice category.
    private static readonly Color PotionKill = new(1f, 0.35f, 0.35f);   // 처치 확정 (red)
    private static readonly Color PotionDefend = new(0.4f, 0.7f, 1f);   // 위기 방어 (blue)
    private static readonly Color PotionBuff = new(0.45f, 1f, 0.5f);    // 엘리트/보스 버프 (green)
    private static readonly Color PotionPlanner = new(1f, 0.85f, 0.2f); // 플래너 추천: 지금 사용 (gold)

    // Potion-use recommendation (lightweight heuristic, per the chosen triggers):
    //   위기 방어 — lethal incoming, or incoming drops HP below 30% maxHP → a block/heal potion
    //   처치 확정 — a damage potion outright kills an alive enemy (EffectiveHp ≤ potion damage)
    //   엘리트/보스 — buff/utility potions only suggested in elite/boss rooms
    // Reads the LIVE potions (DynamicVars effect amounts) + encounter RoomType.
    // Returns the recommended potion + its arrow colour; (null, _) = nothing to suggest.
    private static (PotionModel? potion, Color color) ComputePotionAdvice(SimState sim)
    {
        var cm = CombatManager.Instance;
        if (cm == null) return (null, default);
        if (CombatReflection.CombatManagerStateField?.GetValue(cm) is not CombatState cs) return (null, default);
        var player = cs.Players.FirstOrDefault();
        var potions = player?.Potions?.Where(p => p != null).ToList();
        if (potions == null || potions.Count == 0) return (null, default);

        int incoming = Sts2CombatAI.Sim.EnemyTurnSimulator.PredictPlayerDmg(sim);
        int hpAfter = sim.PlayerHp - incoming;
        // 2026-06-03 — relaxed defensive threshold (was 30% maxHP) so block/heal potions are
        // suggested in normal fights too, not only at near-death. 50% = "taking real damage".
        int dangerFloor = (int)System.Math.Ceiling(sim.PlayerMaxHp * 0.50);

        PotionModel? best = null; Color bestColor = default; int bestPri = 0;
        void Consider(int pri, PotionModel p, Color c) { if (pri > bestPri) { bestPri = pri; best = p; bestColor = c; } }

        foreach (var p in potions)
        {
            try
            {
                var vars = p.DynamicVars;
                var tgt = p.TargetType;
                bool hasBlock = vars.ContainsKey("Block");
                bool hasHeal = vars.ContainsKey("Heal");
                bool hasDmg = vars.ContainsKey("Damage");

                // 위기 방어 — block/heal potion
                if (hasBlock || hasHeal)
                {
                    if (incoming >= sim.PlayerHp) Consider(4, p, PotionDefend);            // lethal this turn
                    else if (incoming > 0 && hpAfter <= dangerFloor) Consider(2, p, PotionDefend); // < 30% maxHP
                }

                // 처치 확정 — damage potion that outright kills (single- or all-enemy)
                if (hasDmg && (tgt == MegaCrit.Sts2.Core.Entities.Cards.TargetType.AnyEnemy
                            || tgt == MegaCrit.Sts2.Core.Entities.Cards.TargetType.AllEnemies))
                {
                    int dmg = vars.Damage.IntValue;
                    if (sim.Enemies.Any(e => e.IsAlive && e.EffectiveHp > 0 && e.EffectiveHp <= dmg))
                        Consider(3, p, PotionKill);
                }

                // 2026-06-03 — buff/utility potions (neither defensive nor damage) are now
                // suggested in normal fights too (was elite/boss-only), at the lowest priority
                // so defend/kill advice still wins when present.
                if (!hasBlock && !hasHeal && !hasDmg)
                    Consider(1, p, PotionBuff);
            }
            catch { }
        }
        return (best, bestColor);
    }

    // 2026-06-04 — live PotionModel whose Id matches the planner's recommendation. Null if not held.
    private static PotionModel? FindPotionById(string id)
    {
        try
        {
            var cm = CombatManager.Instance;
            if (cm == null) return null;
            if (CombatReflection.CombatManagerStateField?.GetValue(cm) is not CombatState cs) return null;
            var player = cs.Players.FirstOrDefault();
            var potions = player?.Potions;
            if (potions == null) return null;
            foreach (var p in potions)
                if (p != null && p.Id.Entry == id) return p;
        }
        catch { }
        return null;
    }

    // The on-screen NPotionHolder showing the given potion model (live combat HUD). Null if not found.
    private NPotionHolder? FindPotionHolder(PotionModel potion)
    {
        var container = GetPotionContainer();
        if (container == null) return null;
        _holdersField ??= typeof(NPotionContainer).GetField("_holders", BindingFlags.NonPublic | BindingFlags.Instance);
        if (_holdersField?.GetValue(container) is not System.Collections.IEnumerable holders) return null;
        foreach (var h in holders)
            if (h is NPotionHolder nh && ReferenceEquals(nh.Potion?.Model, potion))
                return nh;
        return null;
    }

    private NPotionContainer? GetPotionContainer()
    {
        if (_potionContainer != null && GodotObject.IsInstanceValid(_potionContainer)) return _potionContainer;
        _potionContainer = FindNodeOfType<NPotionContainer>(GetTree().Root);
        return _potionContainer;
    }

    private static T? FindNodeOfType<T>(Node root) where T : Node
    {
        if (root is T t) return t;
        foreach (var c in root.GetChildren())
        {
            var r = FindNodeOfType<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    // ▲ just below the recommended potion's bottom-centre (tip points up at it).
    private void PositionPotionMarker()
    {
        if (_potionMarker == null) return;
        try
        {
            var holder = _potionTarget != null ? FindPotionHolder(_potionTarget) : null;
            if (holder == null || !GodotObject.IsInstanceValid(holder)) { if (_potionMarker.Visible) _potionMarker.Visible = false; return; }
            Vector2 bottomCenter = holder.GlobalPosition + new Vector2(holder.Size.X / 2f, holder.Size.Y);
            _potionMarker.Position = bottomCenter + new Vector2(-ArrowSize / 2f, PotionGap);
            _potionMarker.Visible = true;
        }
        catch { if (_potionMarker.Visible) _potionMarker.Visible = false; }
    }
}
