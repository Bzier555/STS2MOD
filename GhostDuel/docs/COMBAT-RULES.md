# Combat rules — the duel

**This file is the single source of truth for duel mechanics.** Where the first pass's documents
disagree (POSTMORTEM F3), the resolution is recorded here with a rationale. If a rule changes, it
changes here first, with a dated note.

> **2026-09-02 — queuing is M4, not M1.** M1–M3 used **immediate native damage resolution** (no
> queueing at all) because the engine's one damage chokepoint, `CreatureCmd.Damage`
> (`MegaCrit.Sts2.Core.Commands/CreatureCmd.cs:96-412`), fuses computation, hooks, VFX and HP
> application into one method with no seam for honest deferral — see `PLAN.md`'s M1 gate-correction
> note and `ARCHITECTURE.md` §7. Real queuing arrived at M4 alongside the honest `DamageResult` design
> that made it possible without fabrication.
>
> **2026-09-04 — combat-rules rework: queuing is one-directional, not a duel.** After live-testing M9,
> the user and their friend wanted the fight closer to normal Slay the Spire: the human's own damage
> now applies **immediately**, like any real STS fight — it no longer queues at all. Only the Ghost's
> own direct damage still queues, so it still has a visible, telegraphed intent like a normal boss.
> Everything below that describes symmetric, both-directions queuing is the **superseded** design;
> §2 and §3 have been updated in place to the current, one-directional rule. §6's decay rule was
> revisited in the same rework — see its own dated note.

---

## 1. Why the Ghost needs a queuing rule at all

The Ghost is a boss fight, and a boss fight needs visible intent: you see the threat, then you answer
it. The human plays like any normal Slay the Spire run — cards resolve the instant they're played,
full stop.

```text
the Ghost's direct damage    -> queued, shown as intent, resolves at the start of its next turn
everything else              -> resolves immediately, for both sides
```

This is intentionally asymmetric (2026-09-04): only the Ghost telegraphs. The human doesn't need an
"incoming threat" number computed for them — they see their own hand and plan directly, exactly like
any other STS fight. See `PLAN.md`'s dated note for what this asymmetry cost the Ghost's AI (it can no
longer read an exact incoming-damage figure for its own defensive planning — a known, accepted gap).

---

## 2. The loop

**Superseded 2026-09-04: the Ghost takes the opening turn**, not the human — matching a normal STS
fight, where the enemy's intent is already visible before you ever act. `CombatState`'s constructor
hardcodes the opening side (`CombatState.cs:157`: `CurrentSide = CombatSide.Player`, unconditionally,
no native per-encounter seam); P32 (`EnginePatches.cs`) flips it to `CombatSide.Enemy` for a Ghost Duel
session specifically. See `ENGINE-NOTES.md`'s citation chain for why this is safe (the one native
setup-time reader of `CurrentSide` is already bypassed for the Ghost by the existing P4 patch).

```text
GHOST TURN
  1  the attack the Ghost queued last turn resolves against the human (nothing, on the opening turn)
  2  the Ghost draws and plays real cards; Block, buffs and direct damage against the human all land
     immediately from the human's point of view, but the Ghost's own direct damage still accumulates
     into next turn's queued intent rather than landing now
  3  status expiry
  4  the Ghost's new intent is published

HUMAN TURN
  sees the Ghost's intent from the Ghost's last turn
  plays normally; every effect — including direct damage — lands immediately, like any STS fight

GHOST TURN
  ...
```

Because the Ghost always acts first, the human's very first look at the Ghost already shows a real
queued attack from the Ghost's own opening turn — the old "opening turn shows a neutral Powering Up
placeholder, human acts first" case no longer arises (there was never mod code building that
placeholder; it was just describing what an empty queue happens to look like, and the queue is never
empty by the time the human can see it now).

---

## 3. Immediate vs. queued

**Queued.** Only the Ghost's own direct damage — attack damage from cards, powers, orbs, relics and
pets it deals. Human-dealt direct damage is **not** queued (2026-09-04 rework; it was queued,
symmetrically, from M4 through M9 — see the file-level note above).

**Immediate (everything else, for both sides).** Block · Weak · Vulnerable · Frail · Poison and Doom
*application* · Strength, Dexterity and all other powers · energy and Stars · draw, discard, exhaust,
retain, card generation · orbs · pets and Summon · stances and class resources · healing · card and
relic counters · the human's own direct damage.

Rationale (as revised): only the Ghost's damage needs a delay, to give it a visible intent like a
normal boss. The human's damage landing immediately is what makes the fight feel like real STS again;
Weak applied by the Ghost must still actually reduce the Ghost's own already-queued attack (§5
unchanged for that direction), and a debuff the Ghost applies to the human must actually affect the
human's live state right away (state changes were never delayed, in either design).

---

## 4. Packets and grouping

Preserve individual damage packets internally even when the UI shows one number. `4 × 10` is
mechanically different from `1 × 40` for on-hit effects, damage caps, thorns, reactive powers and
relic counters.

- Damage from **separate attack cards** aggregates into **one** hit.
- Repeated hits produced by **a single card** stay a **multi-hit** (`7 × 3`, not `21`).

Port `GhostDamagePlan` from the first pass with its pure tests; the semantics are correct.

---

## 5. Modifier timing — the hard part

The rule that must hold: **an effect committed for a response window cannot be retroactively changed
by something that happens after the commitment.**

### Vulnerable — captured in card-effect order

Each damage packet snapshots Vulnerable at the moment its damage effect fires.

```text
Ghost plays Bash:    queue 8, then apply Vulnerable 2   (Bash's own damage is not vulnerable-boosted)
Ghost plays Strike:  queue 9                            (now sees Vulnerable)
Intent shows 17
```

### Weak — late-bound to the attacker

Weakening an attacker must reduce damage it has *already* queued, because otherwise Weak is worthless
against a telegraphed attack: by the time it would apply, the attack is already committed. Since
2026-09-04 only the Ghost's own damage queues, so this specifically means Weak the human applies to
the Ghost:

```text
Ghost queues 40.  Human applies Weak to the Ghost.  The queued 40 immediately displays and resolves
as reduced.
```

Implementation constraint (POSTMORTEM F14): **do not** store `modifiedAmount / weakMultiplier` to
recover a pre-Weak base. Store the engine's value plus the fact that Weak is re-evaluated, and
recompute from the attacker's live power state at display and at resolution.

### Resolution must not double-apply

A packet already contains the modifiers active when its card was played. At resolution, suppress the
second `Hook.ModifyDamage` pass — but **preserve `ValueProp` flags**, notably powered-attack status,
which native mechanics such as Osty's Soul Link depend on.

The first pass suppressed it by matching an ambient `(target, dealer, amount, props, cardSource)`
tuple, which can collide or miss (POSTMORTEM F14). Prefer an explicit token carried on the packet
and threaded through the resolution call.

---

## 6. Status expiry — **superseded 2026-09-04: decay at the holder's own turn end**

**Decided 2026-09-04, by the user, superseding rule (a) below.** A single stack of Weak the Ghost
applies to the human mid-turn, under rule (a) (applier-side), ticked at the *same* Ghost turn's end —
the very first checkpoint after application — expiring before the human ever got to act under it. What
the user actually wants: a debuff is up for **its holder's** own next turn, then decays at that turn's
end — decided both ways (a debuff the human applies to the Ghost decays at the *Ghost's* own turn end
too, not the human's), matching how the engine's own `TemporaryStrengthPower` (Mangle-style Strength
reduction) already decays, unmodified: `AfterSideTurnEnd` checks `participants.Contains(base.Owner)` —
ticks whenever the *holder* is a participant in the side whose turn is ending, no side-check, no
applier-tracking at all (confirmed by reading `TemporaryStrengthPower.cs:173-181`; needed no patch of
its own). P26 (`EnginePatches.cs`) revised in place to mirror that same shape for `WeakPower`/
`VulnerablePower`/`FrailPower`: `participants.Contains(power.Owner)`, reusing the `participants`
parameter the hook already receives rather than tracking `Applier`. P18's grace-tick suppression is
still needed, unchanged — the native `SkipNextDurationTick` grace is orthogonal to whichever side-check
formula gates the tick, and would otherwise swallow the *first* opportunity here too, under either
rule.

The rest of this section (rule (a) and its history) is kept below for the record, not as current
behavior.

### Superseded: rule (a), decided 2026-09-03

The first-pass documents stated two mutually exclusive rules for Weak / Vulnerable / Frail:

| Rule | Source | Consequence |
| --- | --- | --- |
| **(a)** decay at the end of the **applying** side's turn | `Legacy_Ascension_STS2_Mod_Spec.md`, `Codex_Ghost_AI_Integration.md` | Symmetric with Poison/Doom; a 2-stack debuff you apply covers exactly your opponent's next turn. |
| **(b)** decay after the **afflicted** side's turn, *and* only after that turn's queued damage resolves | `README.md`, `PLAYER_VS_GHOST_MECHANICS.md`, `Ghost_Duel_Combat_Damage_Status_Timing.md` | Matches base-game intuition ("it lasts through my turn"); the extra "after queued damage" clause exists to stop an expiring debuff from retroactively changing a committed attack. |

**Decided 2026-09-03, by the user, overriding this file's earlier (b) recommendation: rule (a).**
Confirmed live and root-caused first: Red Mask's Weak on the human was observed persisting through
the human's *second* turn instead of decaying after the Ghost's own — a one-turn debuff having a
two-turn effect. Traced to `PowerModel.SkipNextDurationTick` (native, `PowerCmd.cs:144-147`), whose
own doc comment says it exists so a debuff survives "the *monster* side['s]" first decay check when "a
*monster* applied the power to the player" — an assumption that breaks once the enemy side is a real
Player who can apply a debuff at the very start of its own turn, immediately before that same turn's
decay checkpoint. Fixed by P18 (`EnginePatches.cs`): stop granting the human-only grace tick within a
Ghost Duel.

**Revisited 2026-09-03**, by the user, to close a gap P18 alone left open: `WeakPower`/
`VulnerablePower`/`FrailPower` (confirmed identical shape in all three) tick down at one *fixed*
native checkpoint — the Enemy (Ghost) side's own turn end — regardless of who holds or applied the
stack. For a Ghost-applied debuff, that fixed checkpoint already *is* "the applying side's turn end,"
so P18 alone was sufficient there. But a debuff the *human* applies to the Ghost (e.g. Vulnerable via
Bash) was still only ticking at the Ghost's *next* turn end — one full round later than rule (a)
calls for, since the fixed checkpoint never coincides with the human's own turn end. The user's
worked example: the Ghost applies Vulnerable 2 to the human late in its own turn; it should decay to
Vulnerable 1 immediately at that same turn's end (not skip a turn), persist unchanged through the
human's turn, and still be active for the Ghost's *next* turn's attacks before finally expiring at
that turn's end — decay keyed to **who applied it**, not a fixed side. Fixed by P26
(`EnginePatches.cs`): replaces the native `if (side == CombatSide.Enemy)` check with
`power.Applier?.Side` — a field `PowerCmd.Apply` already sets on every application
(`PowerCmd.cs:119`: `power.Applier = applier;`), no new tracking needed — so a Vulnerable/Weak/Frail
stack now ticks down at *its own applier's* side turn end, symmetric for both directions. This
applier-side rule was itself superseded 2026-09-04 (see the top of this section) before ever being
confirmed live.

Poison and Doom deliberately keep their existing **afflicted-side periodic timing** — a different
mechanic from Vulnerable/Weak/Frail's applier-side decay, confirmed by reading `PoisonPower.cs:17-21`:
it shares `Type`/`StackType` with the three duration debuffs but is not touched by P26 (which targets
those three classes specifically, not the shared `Type`/`StackType` shape — Poison/Doom would
otherwise be caught by a same-shape check). Source-owned timing with native automatic ticks
suppressed for tracked duel statuses is still not implemented or tested (M6 item 2, still open) — P26
does not implement this, it only confirms Poison/Doom must stay out of its own scope.

---

## 7. Scaling — **decision required**

Also contradictory across the first pass (POSTMORTEM F3):

| | Spec / AI brief | v0.9.4 README + verifier |
| --- | --- | --- |
| Ghost max HP | `saved × 5.0` | exact saved value |
| Ghost damage | `× 1.25` at the output layer | none |
| Ghost Block | `× 2.0` at the output layer | none |

Both positions are defensible. Unscaled is *mechanically* honest and keeps AI valuation, relic
interactions and combo evaluation on authentic player numbers. Scaled is what makes a
player-scale build survive as a boss: an 80 HP Ghost dies to one good turn from a late-game deck.

**Recommendation: build unscaled, keep the seam.** Route all Ghost outgoing damage, Block and max HP
through one `GhostScaling` object whose M1 values are `1.0 / 1.0 / 1.0`. Then the multipliers become
a tuning decision made against a working fight, not a design commitment made in advance — and the
AI can be told the effective values through one call rather than being retrofitted later.

Note that the first pass's verifier actively *asserts* that no multiplier field exists, so this seam
must be a deliberate re-decision, not an accident. If scaling is adopted, apply it at the output
layer only; never modify a saved card.

Not needed before M7 (the first snapshot fight). Confirm before then.

---

## 8. Intent display

Reuse native intent presentation. Show only the results of the turn the Ghost has already executed —
never its hand or draw order.

```text
⚔ 34            single aggregate attack
⚔ 9 × 4         preserved multi-hit
⚔ 21  Buff      attack plus a buff marker
Debuff          non-damage only
⚡ Powering Up   opening turn, nothing queued
```

Because buffs and debuffs resolve immediately, the intent does **not** advertise them after the
opening turn; the human sees the actual power icons on the Ghost instead.

**Superseded 2026-09-04**: the human's own damage no longer queues at all, so there is no longer a
human-side indicator to show — only the Ghost's intent is displayed, exactly like a normal STS boss.

---

## 9. Death

**Ghost at 0 HP:** its `Creature` is a primary enemy (verified — `Creature.cs:252`), so the native
enemy-side victory check ends combat with no patch. A Ghost-owned living pet must be included in the
same death cleanup, or a non-attack lethal (poison, forced death) can strand combat with an
unkillable secondary enemy.

**Human at 0 HP:** ordinary run loss. Under the ladder (M7+) the level is not completed and the
previous Ghost is **not** overwritten.

---

## 10. Loop protection

Player decks loop. The controller must bound: max card plays per turn, max repeated
(hand, energy, Block) signature, and a wall-clock budget for the whole turn. Exhausting any bound
ends the Ghost turn cleanly and logs why.

Presentation may compress — accelerate animations, group repeated generated cards — but must never
skip a logical card play.

---

## 11. Determinism

All Ghost randomness comes from the run's deterministic RNG. No local client randomness, no
non-deterministic collection iteration in any gameplay decision. Under multiplayer (M9) the host is
authoritative and clients only animate synchronized events.

M1's debug controller deliberately uses stable pile order with no shuffle, so repeated tests are
reproducible. That is a debug affordance, not the shipping rule — the shipping path shuffles through
the native RNG.
