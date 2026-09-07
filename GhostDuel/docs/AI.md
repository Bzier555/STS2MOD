# Ghost decision-making

> **2026-09-04 — rewritten.** M5 originally shipped a scored/planned "best line" chooser
> (`SemanticGhostChooser`/`CombatActionScorer`/`CombatTurnLinePlanner`, ported from `sts2-ai-teammate`
> and adapted for the duel's queued-damage design). Per the user's (and their friend's) explicit
> request, that has been replaced with a deliberately non-strategic, weighted-random chooser
> (`WeightedRandomGhostChooser`) — no scoring against a simulated "best line," no reading of incoming
> threat. This file describes the *current* design. Sections that were about the deleted scorer/config
> system have been rewritten or removed; §2's reuse story (the `Cards/` semantic-extraction layer) is
> unchanged and still the right thing to reuse for any future chooser.

The AI is not the hard part of this mod; making an enemy-side `Player` execute a native card is. The
current chooser is intentionally simple: "a semi-random imitation of a human player using the saved
deck," not an optimizer.

---

## 1. The boundary

The AI answers exactly one question and owns no combat timing:

```csharp
GhostChoice? Choose(GhostTurnView view);   // null == end turn
void OnTurnStart();                        // reset any per-turn state
```

The controller (`GhostTurnController`) owns turn start, draw, energy, phases, hooks, queueing, damage
resolution, status timing, intent creation and turn end. The AI is a decision maker, not a second rules
engine.

`GhostChoice` carries the card instance and the target (`null` for anything that is not `AnyEnemy` /
`AnyAlly`). Card-grid sub-prompts are answered by the scoped `ICardSelector`, not by the chooser.

`GhostTurnView` is a read-only snapshot: the Ghost `Player` and its live hand. The controller
re-derives this and calls `Choose` again after **every** play — a chooser never sees a stale hand, and
never decides more than one card at a time. This existing structure is what satisfies "recalculate
everything after every card": draw, card generation, cost changes, relic triggers, powers and class
mechanics are all handled for free by re-deriving the view instead of simulating them. `OnTurnStart`
exists only so a chooser can reset state that must persist *within* a turn but not across turns (the
current chooser's Attack/Skill drift, below) — it is not a hook for turn setup, which the controller
still owns entirely.

---

## 2. Reuse before writing

`src/Ai/Cards/` (`CardResolver`, `ResolvedCardView`, `ResolvedCardViewExtensions`,
`CardCatalogRepository`/`CardCatalogBuilder`) is the reusable part, independent of whichever chooser
consumes it: it turns a live `CardModel` into a `ResolvedCardView` with native `Type`
(`CardType.Attack`/`Skill`/`Power`/etc. — read from the engine, never computed), `EffectiveCost`
(base cost plus upgrade/run/combat-state overlays), and semantic effect amounts via extension methods —
`GetEstimatedDamage()`, `GetEstimatedBlock()`, `GetEnemyVulnerableAmount()`, `GetEnemyWeakAmount()`,
`GetSelfStrengthAmount()`/`GetSelfDexterityAmount()` (and `...Temporary...` variants),
`GetCardsDrawn()`, `GetEnergyGain()`, `GetAppliedPowerAmount(powerId, ...)` for anything else (e.g.
Poison). Playability itself stays 100% native (`CardModel.CanPlay`) — the resolver never determines
that.

Originally ported from `../STS2Ghostmod/.references/sts2-ai-teammate/` (MIT) via the first pass's
`src/AdaptedAi/Cards/` — this layer is unchanged by the 2026-09-04 rewrite and should stay the
reference implementation for "what does this card actually do" regardless of what future chooser
consumes it.

Deleted 2026-09-04 (only ever consumed by the scored/planned chooser, not this layer):
`CombatActionScorer`/`CombatTurnLinePlanner`/`DeterministicCombatContext`/
`DeterministicCombatContextBuilder` (`src/Ai/Combat/`), the per-character `.aiconfig` schema
(`GhostAiCombatConfig`/`GhostAiCombatConfigLoader`/`GhostAiRiskProfile`/`GhostAiStatusWeights`/
`GhostAiResourceWeights`, `src/Ai/Config/`), `AiLegalActionOption`/`AiTeammateActionKind`
(`src/Ai/Contracts/`), and `GhostScaling` (COMBAT-RULES.md §7's still-undecided scaling seam — it was
wired only into the deleted context builder; §7 itself is unaffected and can reintroduce a seam
whenever it's actually decided).

---

## 3. Co-op to adversarial

Upstream (`sts2-ai-teammate`) assumes `Player = friendly, Monster = hostile`. The duel violates that by
construction. `WeightedRandomGhostChooser`'s target resolution uses combat side, not type checks —
`c.Side != ghostSide` for an opponent, `c.Side == ghostSide` for an ally — mirroring
`LeftToRightChooser`'s own resolution exactly. The chosen target is revalidated by
`CardModel.IsValidTarget` before a card is ever added to the candidate pool.

---

## 4. The current design — `WeightedRandomGhostChooser`

Per call to `Choose`, in priority order:

1. **Powers first.** Any currently playable Power is preferred over anything else, chosen uniformly at
   random among playable Powers if more than one exists. Playing a Power does not touch the Attack/
   Skill drift (below).
2. **Spend Energy-costing cards before 0-cost ones.** While any playable card costs more than 0
   Energy, 0-cost cards are ignored entirely. The rule is "no *currently playable* Energy-costing
   card," not "Energy == 0" — leftover unspendable Energy correctly falls through to the 0-cost pool.
3. **Attack/Skill weighted drift.** One piece of state persists across a turn's `Choose` calls: an
   Attack-probability integer, starting at 50 (`OnTurnStart` resets it). Every Attack played decreases
   it by 15 (floor 20); every Skill played increases it by 15 (cap 80) — including a *forced* pick when
   only one category is playable, which still updates the drift as if it had been a free choice. This
   is what makes the Ghost drift toward variety without ever alternating strictly.
4. **Picking within a category.** Not "the mathematically optimal card" — a small score (damage/Block
   as the primary term, plus a bonus for useful secondary effects: Vulnerable/Weak/Poison/draw for
   Attacks; Strength/Dexterity/Weak/Vulnerable/draw/Energy for Skills — see the exact weights and
   rationale in `WeightedRandomGhostChooser`'s own doc comments, since they're tuning constants, not
   design) plus a small bounded random jitter, then the best *jittered* result wins. This produces
   "usually a good card, occasionally a close second," not a guaranteed-optimal pick.
5. **No incoming-threat reasoning at all.** The Ghost does not read or estimate what the human might do
   next turn, and does not react to a human's queued damage — since the 2026-09-04 combat-rules
   rework, the human's damage applies immediately anyway, so there is nothing left to queue-read by
   the time the Ghost acts (see `PLAN.md`'s dated note on that rework). This isn't a regression this
   rewrite introduces; it's the explicit design goal — the Ghost reacts only to its own hand and its
   own current state, never to a prediction of the human's future turn.

---

## 5. Failure behavior

1. **A deterministic native-legal fallback action is always ready.** `WeightedRandomGhostChooser.Choose`
   wraps its whole body in a try/catch that falls back to `LeftToRightChooser` on any exception — a
   resolver, catalog, or scoring failure must never turn a valid hand into an empty turn.
2. A modded card needing an unsupported non-card interaction: the card selector
   (`GhostCardSelector`) is the escape hatch already in place for prompts with no real UI/network
   answer for the Ghost.
3. Loop and wall-clock guards in `GhostTurnController` (`MaxPlaysPerTurn`/`TurnBudget`) are final;
   exhausting them ends the turn, never hangs.
4. `LeftToRightChooser` stays in the tree permanently as a regression mode. Swapping back to it is a
   one-line change at each of the (now five) construction sites.

---

## 6. Not yet confirmed live

This whole rewrite compiles clean but has not been exercised in a real fight yet. Worth specifically
watching for during the next live test: Powers actually played before Attacks/Skills; Energy spent down
before 0-cost cards appear; a 0-cost card that grants Energy correctly interrupting the 0-cost phase
and returning to Energy-costing plays (the one genuinely subtle case in the spec); Attack/Skill
selection visibly varying over several turns without strictly alternating; the turn still ending
cleanly once the hand is exhausted.
