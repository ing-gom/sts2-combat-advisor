# StS2 Combat Advisor

A **Slay the Spire 2** mod that, *during combat*, draws the game's own targeting arrows to show you the AI-recommended play — which card to play first, which enemy to hit, and which potion is worth drinking right now.

> No hotkeys, no menus, no win-rate spinner. It reads the live fight every frame and just points at the answer.

[한국어 README](README.ko.md)

---

## What it does

Three on-screen markers, all using the game's built-in targeting-arrow art:

- **▶ Play this first** — a green arrow above the card the planner would play next this turn.
- **▼ Hit this enemy** — when you hover a card that targets a single enemy (an attack *or* an enemy-targeting skill like Vulnerable), a red arrow points at the enemy the planner would aim it at. Same-name enemies are handled positionally, so it always points at the *right* one.
- **▲ Use this potion** — a colour-coded arrow under the potion worth using:
  - 🔴 **red** — a damage potion that would *outright kill* an enemy this turn.
  - 🔵 **blue** — a block/heal potion when you're about to take lethal damage, or drop below 30% max HP.
  - 🟢 **green** — a buff/utility potion, suggested only in **elite/boss** rooms.

Everything updates in real time as the board changes; only one potion (the highest-priority one) is flagged at a time.

## How it works

- The mod links the [**sts2-combat-ai**](https://github.com/ing-gom/sts2-combat-ai) planner (simulator + scorer) **in-process** and runs it directly on the live combat — no external helper, no inter-process calls.
- Each frame it snapshots the active `CombatState` (deck, hand, energy, enemies, powers, potions), asks the planner for the next best play, and positions the arrows over the matching game UI nodes (`NCreature` / `NHandCardHolder` / `NPotionHolder`).
- Potion advice is a lightweight heuristic layer on top of the planner's damage prediction: it reads each live potion's real effect amount (`DynamicVars`) and the encounter's room type to decide kill / defend / buff.

## Multiplayer

Client-side and read-only. The mod never writes to game state or sends network messages — it only *reads* the combat and draws an overlay — so other players see an unmodified game. The manifest declares `"affects_gameplay": false`.

## Installation

1. Download the latest `Sts2CombatAdvisor-vX.Y.Z.zip` from [GitHub Releases](../../releases).
2. Extract the `Sts2CombatAdvisor/` folder into:
   ```
   <Slay the Spire 2 install>/mods/
   ```
   so you end up with:
   ```
   <Slay the Spire 2>/mods/Sts2CombatAdvisor/Sts2CombatAdvisor.dll
   <Slay the Spire 2>/mods/Sts2CombatAdvisor/Sts2CombatAdvisor.json
   ```
3. Launch the game and start a fight — the arrows appear automatically.

## Building from source

Requirements:
- .NET SDK 9.0
- Godot.NET.Sdk 4.5.1 (resolved automatically)
- A local Slay the Spire 2 install (auto-detected by `Sts2PathDiscovery.props`)
- The [**sts2-combat-ai**](https://github.com/ing-gom/sts2-combat-ai) repository **cloned next to this one** — the planner source is linked from `../Sts2CombatAI/Sts2CombatAICode/Core` at build time:
  ```
  <parent>/
  ├─ Sts2CombatAI/         (clone of sts2-combat-ai)
  └─ Sts2CombatAdvisor/    (this repo)
  ```

```sh
dotnet build Sts2CombatAdvisor.csproj -c Release
```

The build automatically copies `Sts2CombatAdvisor.dll` and `Sts2CombatAdvisor.json` into `<sts2>/mods/Sts2CombatAdvisor/`.

## Notes & limits

- The recommendation is the same planner used by the sister **sts2-combat-ai** auto-play mod; its quality is bounded by that planner's simulation fidelity.
- AOE / random-target / self / untargeted cards intentionally get no enemy arrow (there's nothing to aim).
- Potion advice is heuristic — it flags clear kills and clear danger, not every situational use. Buff potions are only suggested in elite/boss fights to avoid noise.
- The overlay anchors to the game's combat UI nodes; a future game patch that renames those nodes or the targeting-arrow asset may require a source update.

## Credits

- **MegaCrit** — for Slay the Spire 2.
- **[sts2-combat-ai](https://github.com/ing-gom/sts2-combat-ai)** — the planner / simulator / scorer this overlay drives.
- **HarmonyX** — runtime patching library (bundled with the game; not redistributed here).

## License

[MIT](LICENSE).
