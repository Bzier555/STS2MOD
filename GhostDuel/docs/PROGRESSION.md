# Legacy Ascension progression

**Deferred to M7.** No file under `src/Progression/` may exist before M6's exit gate is signed off.

This is the product the fight is for; it is not the hard part. Condensed from
`../STS2Ghostmod/Legacy_Ascension_STS2_Mod_Spec.md`.

---

## 1. The core fantasy

> Each successful run creates the boss of the next run.

```text
WIN ASCENSION N  ->  save the exact winning character/party  ->  it becomes the final boss of N+1
```

The player is always building two things at once: the deck that wins this run, and the opponent they
may have to beat on the next level.

---

## 2. The ladder

```text
A0        plays exactly like base-game Ascension 0. No Ghost fight.
          On victory: save the winning build as Ghost A0, unlock A1.

A(N>0)    normal Ascension N rules through Act 3.
          Act 3 boss dies -> party healed to full -> fight Ghost A(N-1).
          The level counts as complete only if the Ghost dies.
          On Ghost victory: save the current build as Ghost N, unlock A(N+1).
```

### Progression is shared across characters — a core rule

One ladder for the whole mode. **Not** per character.

```text
A0 completed as Silent   -> Ghost A0 = Silent
A1 played as Ironclad, final fight = Silent A0.  Won -> Ghost A1 = Ironclad
A2 played as Defect,   final fight = Ironclad A1
```

The Ghost is whatever character actually earned the previous victory. Never convert the Ghost to the
current character, never generate an equivalent deck for a different class, never pick the Ghost
based on who is playing.

### Loss does not overwrite

A snapshot is written **only** on successful completion of that level.

```text
A4 Ghost exists.  Player attempts A5 and loses to it.
  -> A4 Ghost unchanged.  No A5 Ghost.
```

Replaying an already-completed level does **not** overwrite its historical Ghost.

---

## 3. Snapshots

Use the game's **native run serialization**. Do not invent a card format; instance state would be
lost.

```text
GhostSnapshot
  ascensionLevel
  characterId, characterVisualId
  finalMaxHp
  deck[]     -> cardId, upgradeLevel, enchantments[], permanent modifications,
                persistent counters, custom/modded card data
  relics[]   -> relicId, persistent state, counters, custom data
  characterPersistentState   (class resources, starting powers)
  metadata
```

Requirements:

- Exact card upgrades, enchantments, permanent modifications and per-instance state. **Never
  reconstruct a card from its base id** if that loses instance state.
- Exact relic set with persistent state. A relic that normally resets between combats follows its
  ordinary combat-start behavior after restore.
- The saved character's identity, sprite, animation set, class mechanics and class resources.
- The winner's final max HP (see `COMBAT-RULES.md` §7 on whether it is scaled).

Historical Ghosts are **immutable** once written.

### Storage

The first pass serialized an entire `SerializableRun` per level into one JSON file, and on any
schema mismatch silently replaced the in-memory store with an empty one — so the next save destroyed
every historical Ghost (POSTMORTEM F20). For the rewrite:

- Store only what a Ghost needs (the player/party build), not a whole run.
- One file per Ghost, so one corrupt entry cannot take the ladder with it.
- On a schema mismatch: **refuse to write**, keep the file, tell the user. Never silently reset.
- Write with a temp-file-plus-rename (the first pass did this correctly) and keep a backup.
- Separate single-player and multiplayer tracks; a single-player A5 Ghost and a multiplayer A5 Ghost
  party are different encounter types and must not overwrite each other.

### Missing modded content

Keep the first pass's policy — it is correct. On restore, resolve every character, card and relic id:

1. Never silently substitute unrelated content.
2. Report the exact missing ids to the user and the log.
3. Block the encounter / mark the snapshot incompatible rather than starting a wrong fight.

---

## 4. Save safety

Loading a save must never regenerate a Ghost, overwrite a Ghost, count the run as completed, or
duplicate the post-boss encounter. Persist enough state to distinguish:

```text
before Act 3 boss | after Act 3 boss | inside Ghost encounter | Ghost defeated
```

Saving *inside* the Ghost fight should restore through the game's normal combat serialization
wherever possible: Ghost HP/Block/hand/draw/discard/exhaust/powers/energy/relic combat state, plus
the pending attack on each side and the AI turn phase.

---

## 5. Encounter entry

The first pass's approach, which is sound and worth reusing in shape:

- Intercept the transition after the final act's victory room (`RunManager.EnterNextAct`).
- `CombatManager.Reset(graceful: true)` first — the finished boss room still owns an inactive
  `CombatState` and a nested combat cannot be set up until it is released.
- Heal the party to full.
- Enter a `CombatRoom` for a custom encounter that generates no monsters, via
  `EnterRoomWithoutExitingCurrentRoom(room, fadeToBlack: true)`, with
  `ShouldResumeParentEventAfterCombat = false` and `ShouldGiveRewards = false`.
- On completion: mark the level complete, `Reset(graceful: true)`, pop the room, end the session,
  continue to the next act.

Two things to fix versus the first pass:

1. The custom encounter must provide **enemy slots** so the Ghost creature can be positioned
   (`ENGINE-NOTES.md` §4).
2. Session teardown must cover every abort path, not two happy-path callbacks
   (`ARCHITECTURE.md` §10).

---

## 6. Mode entry — replace the first pass's approach

The first pass duplicated `NSubmenuButton` nodes, nulled a private loc-key field by reflection,
hand-laid-out five buttons on hardcoded pixel offsets, and routed mode selection through a static
`_pendingMode` flag consumed in a `StartNewSingleplayerRun` prefix (POSTMORTEM F16).

For the rewrite, in preference order:

1. Whatever BaseLib 3.4.5+ offers natively for registering a game mode or a run modifier. **Check
   this first** — most of that code may be unnecessary.
2. A single run modifier surfaced through the existing Custom Run list, with no menu surgery at all.
3. Only if neither works, custom menu nodes — and then via the game's own layout containers, never
   hardcoded offsets, and never `buttons.Single(b => b.Name == "StandardButton")`.

Debug mode must be a **separate, non-persistent** path with no `[SavedProperty]` state, no
`GameMode.Custom` coupling, and no need to patch `Neow.GenerateInitialOptions`.

---

## 7. Multiplayer — M9

**Resolved 2026-09-04, by the user, superseding this section's original draft** (which assumed one
shared party snapshot and left the ladder/party-size questions open — see PLAN.md's M9 note for the
full build record). The actual design:

- **One Ghost per human, always.** Not a shared party build — each human's own Ghost is built from
  *that specific human's* own previous-level save. This makes Ghost count equal human count by
  construction, so the "party size must match" concern below never arises: there is no such thing as a
  mismatch to detect.
- **A second, fully separate ladder from singleplayer Legacy Ascension.** Every human has two
  independent progressions — their existing singleplayer Ghost-ladder and a new multiplayer one,
  tracked per human (not per party) in its own directory
  (`MultiplayerGhostSnapshotStore`/`ghostduel_legacy_ascension_multiplayer/`), so neither ladder can
  read, overwrite, or cap the other. Whichever subset of players group up next each bring their own
  independent multiplayer-ladder progress.
- **The party's selectable ascension level is the minimum of everyone's own multiplayer-ladder
  progress currently in the lobby** — a live cross-client `Min()`, the same shape the base game's own
  multiplayer ascension cap already uses (`StartRunLobby.UpdateMaxMultiplayerAscension`), just keyed to
  this new counter instead of `ProgressState.MaxMultiplayerAscension`.
- **A0**: every human fights a fresh stock Ghost of their own selected character. **A1+**: every human
  fights their own previous-level Ghost, loaded from their own multiplayer-ladder save.
- **One combat, whole party vs. whole Ghost party** — the entire live human party and the entire Ghost
  party fight simultaneously in one `CombatState`, the same shape as a base-game encounter against
  several monsters at once. Ghost order is the human party's own stable order (`RunState.Players`'
  order), which falls out for free rather than needing its own tracking.
- **Ghost target selection among multiple humans is uniform random**, an explicit v1 simplification —
  not scored. A future milestone could reintroduce per-target scoring here if wanted.
- **Per-viewer damage indicators**: each client's own screen shows, above each Ghost, only the amount
  *that client's own human* is about to take from that Ghost — not an aggregate. A human's own
  outgoing queued attack stays visible to everyone, unfiltered.
- **Host authority, implemented as a small purpose-built broadcast** (`GhostCardPlayEvent` —
  `sequenceNumber, ghostNetId, handIndex, cardIdForValidation, hasTarget, targetNetId`), not a reuse of
  the native per-real-player `ActionQueueSet` (built around a real network-identified client owning its
  own queue — a poor fit for a Ghost, which has none). Only the host ever runs a chooser; every other
  client executes the named card/target instead of deciding for itself.
- Pending attacks are already stored per packet with their own explicit dealer/target creature
  references (`GhostDamageQueue`/`QueuedDamagePacket`), which already generalizes to N humans/M Ghosts
  without a redesign — see `GhostDamageQueue.ResolvePendingAgainst`/`TotalPendingFor(dealer, target)`.

Multiplayer enemy-side `Player` synchronization is the highest-risk area in the whole design and was
never validated in the first pass, nor in this build — see PLAN.md's M9 note for exactly what was
built versus what remains unverified without a real two-client session.

---

## 8. Non-goals for v1

Human PvP · Ghost deck editing · a Ghost selection menu · random historical Ghost selection ·
overwriting Ghosts on replay · cross-save/online Ghost sharing · leaderboards · downloading other
players' Ghosts · potions for the Ghost · AI personalities · AI difficulty modes · scored (rather than
random) Ghost targeting among multiple humans · full process-restart-style reconnect (PLAN.md's M9f
note) · card-selection prompts (e.g. Armaments) being host-broadcast rather than decided locally.

"Multiple simultaneous Ghosts" and "mismatched party sizes" are no longer non-goals to avoid — M9's
actual design (§7) makes multiple Ghosts the normal case and mismatched sizes structurally impossible.

Later possibilities: replay old Ghosts, a Ghost history viewer, exporting/importing snapshots, a
daily Ghost challenge, party-size scaling, Ghost-vs-Ghost simulation.
