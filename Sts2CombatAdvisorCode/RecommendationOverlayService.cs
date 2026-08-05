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
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
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
    // 2026-06-04 — also follows the card while it is GRABBED/being played (not just on hover), so
    // the target hint stays on screen exactly when you are dragging the card onto an enemy.
    private Control? _marker;
    private Creature? _markTarget;
    private string _lastHoverCard = "";
    private FieldInfo? _currentCardPlayField;   // NPlayerHand._currentCardPlay (dragged holder source)
    private PropertyInfo? _cardPlayHolderProp;  // NCardPlay.Holder (inherited; cached for the per-frame drag path)

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
    // 2026-06-04 — when the recommended potion targets a single enemy (damage potion / planner
    // potion play with a target), this marks WHICH enemy to throw it at — previously the potion
    // arrow told you to use a potion but not at whom. Coloured to match the potion arrow.
    private Control? _potionEnemyMarker;
    private Creature? _potionEnemyTarget;
    private int _plannerPotionTargetIdx = -1;
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
        _potionEnemyMarker = MakeMarker(arrow, "▼", new Color(1f, 1f, 1f));  // OVER the potion's target enemy (colour set per advice)
        _layer.AddChild(_marker);
        _layer.AddChild(_badge);
        _layer.AddChild(_potionMarker);
        _layer.AddChild(_potionEnemyMarker);

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
        // considerPotions:true so the potion evaluation (LastPotionCandidates / LastBestCardTotal)
        // is populated alongside the card candidates — the overlay's actual recommendation path.
        var step = Sts2CombatAI.Planner.ActionPlanner.PlanNextStep(sim, considerPotions: true);
        var cands = Sts2CombatAI.Planner.ActionPlanner.LastCandidates;
        var emptyReason = Sts2CombatAI.Planner.ActionPlanner.LastEmptyReason;
        var sb = new System.Text.StringBuilder();
        sb.Append($"[CombatAdvisor] F9 dump — energy={sim.PlayerEnergy} hp={sim.PlayerHp}/{sim.PlayerMaxHp} block={sim.PlayerBlock} aliveEnemies={sim.Enemies.Count(e => e.IsAlive)}\n");
        // Threat context — total incoming damage (PredictPlayerDmg), each alive enemy's CURRENT
        // intent, and the player's block-affecting debuffs. A screenshot then shows whether the
        // planner sees the real threat (Mode B: intent already switched away → a defend pick is
        // stale) and whether Frail/Weak is shrinking the block it can raise (Mode A: under-block →
        // takes the hit it should have covered). EffectiveBlock ≈ rawBlock×(frail?0.75:1)+dex.
        int incoming = 0; try { incoming = Sts2CombatAI.Sim.EnemyTurnSimulator.PredictPlayerDmg(sim); } catch { }
        sb.Append($"  THREAT incoming={incoming} frail={sim.PlayerFrail} weak={sim.PlayerWeak} vuln={sim.PlayerVulnerable} dex={sim.PlayerDexterity}\n");
        for (int ei = 0; ei < sim.Enemies.Count; ei++)
        {
            var e = sim.Enemies[ei];
            if (!e.IsAlive) continue;
            sb.Append($"    ENEMY[{ei}] hp={e.Hp} intent={e.TotalIntentDamage}{(e.HasAttackIntent ? "(atk)" : "(non-atk)")}\n");
        }
        // Relics the sim recognizes (class-name → counter). Lets us confirm which relics are
        // captured and modeled (e.g. Orichalcum affects the RAGE play-order decision).
        if (sim.PlayerRelics is { Count: > 0 })
            sb.Append($"  RELICS = {string.Join(", ", sim.PlayerRelics.Select(kv => kv.Value > 1 ? $"{kv.Key}:{kv.Value}" : kv.Key))}\n");
        // Per-hand-card facts, independent of the planner trace (so stale traces can't mislead).
        // playOrderBias = the same nudge/hold the planner folds into each candidate's firstScore
        // (CAND.first = score + playOrderBias). It's what settles play ORDER: e.g. RAGE (격노) gets a
        // positive nudge to precede its attacks, OR a large negative HOLD (−3000) when Orichalcum is
        // worn and RAGE's block ≤ the relic's blockless +6 — making "play attacks first, hold RAGE"
        // the intended (mutual-exclusion) recommendation. Surfacing it explains why the order looks
        // backwards in screenshots.
        var dumpWeights = Sts2CombatAI.Planner.PlanScorerWeights.For(
            Sts2CombatAI.Planner.PlaystyleResolver.Resolve(sim));
        foreach (var c in sim.Hand)
        {
            int sc = 0; try { sc = Sts2CombatAI.Planner.PlanScorer.Score(c, -1, sim); } catch { }
            int pob = 0; try { pob = Sts2CombatAI.Planner.PlanScorer.PlayOrderBias(c, sim, dumpWeights); } catch { }
            sb.Append($"  HAND {c.Id,-20} cost={c.Cost} playable={c.IsPlayable} eGain={c.EnergyGain} afford={(c.Cost <= sim.PlayerEnergy)} score(-1)={sc} playOrderBias={pob}\n");
        }
        sb.Append($"  RECOMMENDED first = {(step is { } ps && ps.Card != null ? ps.Card.Id : "<none>")}\n");
        if (emptyReason != null) sb.Append($"  emptyReason = {emptyReason}\n");
        var handIds = new System.Collections.Generic.HashSet<string>(sim.Hand.Select(c => c.Id));
        foreach (var c in cands.OrderByDescending(c => c.total))
            sb.Append($"    CAND {c.id,-20} tgt={c.targetIdx,2} first={c.firstScore,7} second={c.secondScore,7} total={c.total,7} next={c.bestNextId ?? "-"}{(handIds.Contains(c.id) ? "" : "  [STALE: not in hand]")}\n");

        // Potion evaluation — why a potion was / wasn't recommended over the best card play.
        // A potion is only chosen when its lookahead total beats bestCardTotal by PotionWinMargin.
        // NOTE: potions whose effect isn't a flat amount (e.g. Fortifier = current block ×2) are
        // classified PotionKind.Other and skipped here entirely — they never appear below.
        int bestCard = Sts2CombatAI.Planner.ActionPlanner.LastBestCardTotal;
        int margin = Sts2CombatAI.Planner.ActionPlanner.PotionWinMargin;
        var pots = Sts2CombatAI.Planner.ActionPlanner.LastPotionCandidates;
        sb.Append($"  POTIONS — bestCardTotal={bestCard} bar(=bestCard+{margin})={bestCard + margin} | held={sim.PlayerPotions.Count} evaluated={pots.Count}\n");
        foreach (var pc in pots.OrderByDescending(p => p.total))
            sb.Append($"    POT  {pc.id,-22} kind={pc.kind,-9} tgt={pc.targetIdx,2} first={pc.firstScore,7} total={pc.total,7} {(pc.total > bestCard + margin ? "[BEATS BAR → would recommend]" : "[below bar → hold]")}\n");
        // Surface held potions the planner never scored (PotionKind.Other), so an unmodeled potion
        // (the reason it's "never used") is obvious instead of silently missing.
        var scored = new System.Collections.Generic.HashSet<string>(pots.Select(p => p.id));
        foreach (var p in sim.PlayerPotions)
            if (!scored.Contains(p.Id))
                sb.Append($"    POT  {p.Id,-22} kind=Other     UNMODELED → never recommended (effect not a flat amount)\n");
        MainFile.Logger.Info(sb.ToString());
    }

    /// Hover marker every frame; the recommended first card + potion advice recompute on a
    /// throttle, and only when a cheap combat-state signature actually changes.
    public override void _Process(double delta)
    {
        // Only draw the combat overlay while the combat room is the FOREGROUND screen. Opening the
        // map, a card/relic inspect, the rewards screen, or any modal pushes a different
        // IScreenContext on top while the combat hand/enemies stay in the tree behind it — so
        // without this gate the Layer-128 markers bleed through onto those screens. Hide the whole
        // CanvasLayer (and skip the per-frame marker work) until combat is the active screen again.
        bool combatForeground = false;
        try { combatForeground = ActiveScreenContext.Instance?.GetCurrentScreen() is NCombatRoom; }
        catch { combatForeground = false; }
        if (_layer != null && _layer.Visible != combatForeground) _layer.Visible = combatForeground;
        if (!combatForeground) return;

        // Hover marker (target enemy of the hovered card) — every frame, cheap.
        try { HoverUpdate(); } catch (Exception ex) { MainFile.Logger.Warn($"[CombatAdvisor] hover: {ex.Message}"); }
        // "Play this first" badge — recompute the top card only when the state changes (throttled).
        _pollAccum += delta;
        if (_pollAccum >= 0.3) { _pollAccum = 0; try { FirstCardUpdate(); } catch (Exception ex) { MainFile.Logger.Warn($"[CombatAdvisor] firstcard: {ex.Message}"); } }
        PositionEnemyMarker(_marker, _markTarget, 0f);                 // ▼ over target enemy (hover/grab)
        PositionBadge();         // ▶ over the recommended first card
        PositionPotionMarker();  // ▲ below the recommended potion
        PositionEnemyMarker(_potionEnemyMarker, _potionEnemyTarget, ArrowSize + 4f); // ▼ over the potion's target enemy (raised above the hover ▼)
    }

    private void FirstCardUpdate()
    {
        var sim = CaptureCombat();
        if (sim == null) { _firstCard = ""; _badgeHolder = null; _lastSig = ""; _potionTarget = null; _plannerPotionId = ""; _plannerPotionTargetIdx = -1; _potionEnemyTarget = null; return; }

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
                _plannerPotionTargetIdx = ps.TargetIdx;   // enemy target for damage potions (-1 = self/all)
                _firstCard = "";
            }
            else
            {
                _plannerPotionId = "";
                _plannerPotionTargetIdx = -1;
                _firstCard = (step is { } ps2 && ps2.Card != null) ? ps2.Card.Id : "";
            }
        }

        // 2026-06-04 — single recommendation: only the planner's chosen next step is shown. If it
        // picked a card, the card badge (▶) shows and NO potion marker appears; if it picked a
        // potion, the gold ▲ shows and no card badge. (The old always-on heuristic potion advice
        // was removed so a card and a potion never light up at the same time.)
        if (!string.IsNullOrEmpty(_plannerPotionId))
        {
            _potionTarget = FindPotionById(_plannerPotionId);
            // Enemy marker (▼) only for single-enemy "throw at" potions (TargetType.AnyEnemy).
            // Self/AOE potions (block, heal, buff, all-enemy damage) have no single throw target —
            // PickPotionTarget still returns an enemy index for them, so gate on the live TargetType.
            bool singleEnemy = _potionTarget != null
                && _potionTarget.TargetType == MegaCrit.Sts2.Core.Entities.Cards.TargetType.AnyEnemy;
            _potionEnemyTarget = (singleEnemy && _plannerPotionTargetIdx >= 0 && _plannerPotionTargetIdx < sim.Enemies.Count)
                ? sim.Enemies[_plannerPotionTargetIdx].SourceRef : null;
            if (_potionMarker != null && _potionTarget != null) _potionMarker.Modulate = PotionPlanner;
            if (_potionEnemyMarker != null) _potionEnemyMarker.Modulate = PotionPlanner;
        }
        else
        {
            _potionTarget = null;
            _potionEnemyTarget = null;
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
        // 2026-06-04 — include player HP/block and the potion inventory so a NON-damage action
        // registers as a state change. Bug: throwing a self/buff/block potion (e.g. FORTIFIER)
        // leaves hand + energy + enemy-HP untouched, so the old signature was identical → the
        // overlay skipped its recompute (stateChanged=false) and stayed stuck on the stale potion
        // recommendation, never surfacing the follow-up card. The potion count/ids change the
        // moment a potion is consumed; HP/block cover heal/block potions and self-block skills.
        sb.Append('|').Append(sim.PlayerHp).Append('/').Append(sim.PlayerBlock);
        sb.Append('|').Append(sim.PlayerPotions.Count);
        foreach (var p in sim.PlayerPotions) sb.Append(',').Append(p.Id);
        // 2026-06-04 — include each enemy's CURRENT intent damage, not just HP. Without it, an
        // enemy switching intent WITHOUT changing HP (attack → buff/debuff, or a different attack
        // value) left the signature identical → the overlay never recomputed → it kept recommending
        // the defend card chosen when the enemy was still attacking (and never switched to offense
        // once the incoming threat vanished). TotalIntentDamage flips the moment the threat changes.
        foreach (var e in sim.Enemies) sb.Append('|').Append(e.Hp).Append(':').Append(e.TotalIntentDamage);
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

    // Place a ▼ marker over the given enemy (above its head). Shared by the hover/grab card-target
    // marker and the potion's target-enemy marker. extraUp raises it (so two markers on the same
    // enemy stack instead of overlapping).
    private static void PositionEnemyMarker(Control? marker, Creature? target, float extraUp)
    {
        if (marker == null) return;
        try
        {
            var node = target != null ? NCombatRoom.Instance?.GetCreatureNode(target) : null;
            var vis = node?.Visuals;
            if (vis == null || !GodotObject.IsInstanceValid(vis)) { if (marker.Visible) marker.Visible = false; return; }
            // Anchor at the creature's INTENT marker (above its head, on the sprite), world→screen.
            // Place the down-arrow so its tip sits just above the head, centred horizontally.
            Vector2 screen = vis.IntentPosition.GetGlobalTransformWithCanvas().Origin;
            marker.Position = screen + new Vector2(-ArrowSize / 2f, -ArrowSize - MarkerGap - extraUp);
            marker.Visible = true;
        }
        catch { if (marker.Visible) marker.Visible = false; }
    }

    private void HoverUpdate()
    {
        // The game already tracks the hovered/focused card holder; read its CardModel via reflection
        // (robust to exact member names). Only recompute the target when the hovered card changes.
        // 2026-06-04 — when a card is GRABBED to be played the game clears FocusedHolder (OnHolderUnfocused),
        // so the hint used to vanish exactly when you start dragging onto an enemy. Fall back to the
        // dragged holder so the ▼ target marker stays up through the whole pick-up → aim → play motion.
        object? holder = NCombatRoom.Instance?.Ui?.Hand?.FocusedHolder ?? GetDraggedHolder();
        if (holder == null) { _markTarget = null; _lastHoverCard = ""; return; }
        string cardId = GetHolderCardId(holder);
        if (cardId == _lastHoverCard) return;
        _lastHoverCard = cardId;
        _markTarget = string.IsNullOrEmpty(cardId) ? null : ComputeTargetFor(cardId);
    }

    // The card holder currently being played/dragged (NPlayerHand._currentCardPlay.Holder), or null
    // when not in card play. Loosely typed (returns the NHandCardHolder as object) so GetHolderCardId
    // can read its CardModel by reflection — no extra type usings needed.
    private object? GetDraggedHolder()
    {
        try
        {
            var hand = NCombatRoom.Instance?.Ui?.Hand;
            if (hand == null || !hand.InCardPlay) return null;
            _currentCardPlayField ??= hand.GetType().GetField("_currentCardPlay",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var play = _currentCardPlayField?.GetValue(hand);
            if (play == null) return null;
            // Holder is declared on the NCardPlay base, so the same PropertyInfo serves both the
            // mouse and controller play subtypes — cache it once (rule: no per-frame reflection).
            _cardPlayHolderProp ??= play.GetType().GetProperty("Holder", BindingFlags.Public | BindingFlags.Instance);
            return _cardPlayHolderProp?.GetValue(play);
        }
        catch { return null; }
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

    // Arrow colour for a planner potion recommendation (gold = "use this now").
    private static readonly Color PotionPlanner = new(1f, 0.85f, 0.2f);

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
