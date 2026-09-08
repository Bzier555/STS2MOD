# STS2 Mod Specification — Rotating Decks Multiplayer Modifier

## Overview

Create a **Slay the Spire 2 multiplayer custom-run modifier** whose only gameplay effect is to permanently rotate each player's deck between players at the start of every combat.

The modifier should appear alongside the other modifiers available in the game's **Custom Run** setup.

The central rule is:

> At the beginning of every combat, before starting hands or combat draw piles are generated, every player permanently passes their current deck to the next player in a fixed cyclic order.

For three players:

- Player A gives their deck to Player B.
- Player B gives their deck to Player C.
- Player C gives their deck to Player A.

After the rotation, each player uses the deck they received for the entire combat and continues owning that deck afterward. On the next combat, the decks rotate again.

This creates a multiplayer run where every player eventually has to play every deck, while the decks gradually become mixtures of cards selected, upgraded, removed, and modified by different players and potentially different character classes.

---

# Working Mod Name

Suggested project/internal name:

`RotatingDecks`

Suggested displayed modifier name:

**Rotating Decks**

Suggested description:

> At the start of each combat, every player's current deck is permanently passed to the next player.

---

# Core Design Goals

The modifier should create a multiplayer drafting and deckbuilding experience where:

1. Players do not permanently control one deck.
2. Decks rotate predictably between players.
3. Card rewards and upgrades made while holding a deck stay with that deck.
4. Every deck gradually reflects decisions made by multiple players.
5. Players eventually need to pilot cards from multiple character classes.
6. Players can plan ahead for which deck each person will hold during important encounters, especially Act bosses.
7. The system is deterministic and synchronized for every multiplayer client.
8. The mod otherwise changes as little of the base game as possible.

---

# Core Rotation Rule

At the beginning of each combat:

1. Determine the current ordered list of active multiplayer players.
2. Capture each player's current persistent/master deck.
3. Rotate those decks forward by exactly one player.
4. Assign the rotated decks back to the players.
5. Only after the rotation is complete should the game construct combat card piles and draw starting hands.

For three players:

```text
Initial:
A -> Deck A
B -> Deck B
C -> Deck C

Combat 1:
A -> Deck C
B -> Deck A
C -> Deck B

Combat 2:
A -> Deck B
B -> Deck C
C -> Deck A

Combat 3:
A -> Deck A
B -> Deck B
C -> Deck C
```

Then the cycle repeats.

For `N` players, every deck returns to its previous relative position after `N` combat rotations.

---

# Rotation Direction

Use the convention:

> Each player gives their deck to the **next player** in the multiplayer/session ordering.

Therefore the player at index `i` receives the deck previously held by index `i - 1`, wrapping around at the beginning.

Conceptual pseudocode:

```pseudo
oldDecks = copy(player.deck for player in players)

for i in range(playerCount):
    sourceIndex = (i - 1 + playerCount) % playerCount
    players[i].deck = oldDecks[sourceIndex]
```

Do not randomize the direction.

---

# Critical Semantic Requirement: The Transfer Is Permanent

This is **not** a temporary combat-only deck swap.

After a rotation:

- The receiving player now holds that deck outside combat as well.
- Card rewards modify that deck.
- Card removals modify that deck.
- Card upgrades modify that deck.
- Card transformations modify that deck.
- Card duplications modify that deck.
- Curses or event cards added to the deck remain in it.
- Any other permanent deck mutation affects the currently held deck.

The deck does **not** return to its original owner after combat.

The next automatic reassignment happens only when the next combat begins.

---

# Rotation Timing

The rotation must occur:

> **After the game has committed to entering a combat, but before the persistent deck is copied or transformed into combat draw/discard/exhaust piles and before the starting hand is selected.**

Desired lifecycle:

```text
Encounter selected
    ↓
Combat initialization begins
    ↓
ROTATING DECKS MODIFIER RUNS HERE
    ↓
Persistent decks reassigned
    ↓
Combat card instances / piles created
    ↓
Opening hands generated
    ↓
Combat begins normally
```

Do not rotate after combat card piles have already been created.

That could cause:

- the UI to show one deck while combat uses another,
- opening hands to come from the wrong deck,
- cards to be duplicated,
- cards to disappear,
- save-state corruption,
- multiplayer desynchronization.

Codex should inspect the actual STS2 combat initialization flow and use the earliest safe hook after an encounter is genuinely starting but before combat deck construction.

---

# Player Ordering

Rotation must use a stable deterministic player order.

Preferred order:

1. Use the multiplayer/session player ordering already maintained by the game.
2. If necessary, use stable network/player IDs as a fallback.
3. Never use non-deterministic dictionary/hash collection iteration.
4. Do not use screen position or local-client UI ordering unless the game explicitly guarantees it is authoritative.

For:

```text
players = [A, B, C, D]
```

rotation is always:

```text
A receives D
B receives A
C receives B
D receives C
```

---

# First Combat

The **first combat of the run should rotate the decks**.

Example:

```text
Run creation:
A -> A starter deck
B -> B starter deck
C -> C starter deck

First combat begins:
A -> C starter deck
B -> A starter deck
C -> B starter deck
```

Do not rotate immediately when the run is created. Allow normal starting deck construction to finish first.

---

# What Rotates

Only the player's persistent/master deck rotates.

Conceptually:

```text
PLAYER STATE = stays with player
DECK STATE   = rotates
```

The following should remain attached to the player unless the game's architecture absolutely requires otherwise:

- Character identity
- Current HP
- Max HP
- Gold
- Relics
- Potions
- Character-specific non-card resources
- Player cosmetics
- Network ownership
- Multiplayer identity
- Any player-level progression state unrelated to the deck

Do **not** swap whole player objects.

Do **not** swap characters.

Do **not** swap health, relics, gold, or potions.

---

# Mixed-Class Cards Are Intended

Decks should be allowed to contain cards from multiple character classes.

Do not filter, remove, convert, or reject a card because it belongs to another player's character class.

Example:

```text
Current character: Ironclad

Current rotated deck:
- Strike_R
- Defend_R
- Ball Lightning
- Acrobatics
- Shrug It Off
- Zap
```

That is intended behavior.

If the game already supports foreign-class cards when inserted into a deck, preserve the existing behavior.

If particular cards depend on character-specific mechanics, prefer the smallest compatibility fix necessary for stability rather than preventing mixed decks globally.

---

# Card Ownership Model

Treat cards as belonging to the **deck**, not to the player who originally selected them.

Example:

```text
Player B is currently holding Deck A.
Player B selects card X after combat.
```

Result:

```text
Deck A permanently gains card X.
```

When Deck A later rotates to Player C, Player C receives Deck A including card X.

---

# Card Rewards

Normal card reward behavior should remain unchanged.

The player making the reward selection modifies whichever deck they currently hold.

No special reward-sharing system should be added.

---

# Shops

All shop effects that modify a deck should affect the player's currently held deck.

Examples:

- Card purchases
- Card removal
- Card transformation
- Card duplication
- Card upgrades, if applicable

If deck ownership is reassigned correctly at the persistent deck level, this should ideally require no special shop code.

---

# Rest Sites and Events

Any permanent card/deck modification should affect the currently held deck.

Examples:

- Upgrade a card
- Remove a card
- Transform a card
- Duplicate a card
- Add a curse
- Add a special event card

Do not route the modification back to the deck's original player.

---

# Intended Boss Strategy

Predictability is a core part of the modifier.

Players should be able to reason about future ownership, for example:

```text
"There are two combats before the boss.
If we take this route, Player C will have the poison deck for the boss."
```

Therefore:

- Do not randomize rotation.
- Do not reshuffle deck ownership between Acts.
- Boss combats rotate normally.
- Players should be able to count upcoming fights and plan around the cycle.

---

# What Counts as a Combat?

By default, rotate at the beginning of every genuine combat encounter, including:

- Hallway combats
- Elite combats
- Boss combats
- Event-triggered combats
- Optional combats
- Special encounters that use the normal combat system

Do not rotate for:

- Entering a non-combat room
- Card reward screens
- Shops
- Rest sites
- Map transitions
- Act transitions by themselves
- Loading a save
- Reconnecting to a session
- UI previews
- Any lifecycle hook that fires without a real combat actually starting

The implementation must guarantee **one rotation per actual combat**.

---

# Combat Rotation Counter

Maintain a run-level counter if useful:

```text
combatRotationCount
```

Initial value:

```text
0
```

After each successful rotation:

```text
combatRotationCount += 1
```

Uses:

- Debugging
- Save/load validation
- Multiplayer synchronization checks
- UI messaging
- Detecting accidental duplicate rotations

The actual current deck assignments should remain the source of truth.

Do not reconstruct current ownership from only this counter unless there is no better option.

---

# Prevent Double Rotation

Combat initialization hooks may fire more than once.

The mod must ensure a combat rotates decks exactly once.

Preferred conceptual approach:

```pseudo
if currentEncounterId == lastRotatedEncounterId:
    return

rotateDecks()
lastRotatedEncounterId = currentEncounterId
combatRotationCount += 1
```

If STS2 does not expose a stable encounter ID, find another deterministic combat-instance identifier or lifecycle state.

Do not use frame timing as duplicate protection.

---

# Safe Rotation Implementation

Do not perform the rotation by overwriting deck references one at a time without first snapshotting them.

Incorrect:

```pseudo
A.deck = C.deck
B.deck = A.deck
C.deck = B.deck
```

The first assignment can destroy information required for the second.

Correct:

```pseudo
oldA = A.deck
oldB = B.deck
oldC = C.deck

A.deck = oldC
B.deck = oldA
C.deck = oldB
```

General version:

```pseudo
oldDecks = players.map(p => p.deck).toArray()

for i:
    source = (i - 1 + players.count) % players.count
    players[i].deck = oldDecks[source]
```

---

# Prefer Moving Deck State Over Rebuilding Cards

If STS2's architecture allows it, rotate the persistent deck object/reference itself.

Preferred:

```text
Player.deck = anotherPersistentDeck
```

rather than:

```text
clear Player.deck
recreate every card from IDs
```

Moving the actual persistent deck state is less likely to lose:

- upgrades,
- card UUIDs,
- card-specific state,
- enchantments/modifiers,
- event metadata,
- references from other systems,
- modded-card data.

If deck references cannot safely be reassigned, use the game's own serialization/deep-copy utilities rather than manually reconstructing cards whenever possible.

---

# Card Instance Integrity

Rotation itself must preserve all persistent card state, including where applicable:

- Card identity
- Upgrade level
- Transformation state
- Innate/bottled state
- Persistent counters
- Enchantments
- Modded card data
- Stable card identifiers
- Other persistent metadata

The rotation should change **ownership**, not the contents or state of the deck.

---

# Single-Player Behavior

This is fundamentally a multiplayer modifier.

If active with one player:

```text
A -> A
```

The operation is a no-op.

Do not crash.

If the Custom Run UI cleanly supports multiplayer-only availability, the modifier may be hidden or disabled for single-player runs. This is optional.

---

# Two-Player Behavior

With two players, every combat swaps the two decks.

```text
Initial:
A -> Deck A
B -> Deck B

Combat 1:
A -> Deck B
B -> Deck A

Combat 2:
A -> Deck A
B -> Deck B
```

---

# Act Transitions

Do not reset deck ownership when an Act ends.

Example:

```text
End of Act 1:
A -> Deck C
B -> Deck A
C -> Deck B

Beginning of Act 2:
A -> Deck C
B -> Deck A
C -> Deck B
```

The first actual combat in Act 2 then rotates them once normally.

The rotation cycle spans the entire run rather than restarting each Act.

---

# Boss Encounters

Boss combats use the exact same rotation logic as normal fights.

There should be no boss exception.

This is necessary for the intended strategic planning around who receives which deck for an Act boss.

---

# Multiplayer Authority

Deck rotation must be deterministic and authoritative.

Preferred conceptual architecture:

```text
Authoritative host/simulation:
    determine player ordering
    calculate rotation
    apply deck reassignment
    update run state
    synchronize resulting assignments
```

Do not have clients independently perform an unsynchronized mutation unless STS2 explicitly uses deterministic lockstep for this type of state change.

Critical invariant:

> After combat initialization, every connected client must agree on which persistent deck belongs to every player.

---

# Multiplayer Synchronization

If the game already synchronizes persistent player deck state, use that mechanism.

Avoid inventing a parallel networking system unless necessary.

If custom state synchronization is required, transmit only enough information to establish authoritative deck ownership.

Conceptual payload:

```json
{
  "rotationCount": 7,
  "assignments": [
    { "playerId": "A", "deckId": "deck_C" },
    { "playerId": "B", "deckId": "deck_A" },
    { "playerId": "C", "deckId": "deck_B" }
  ]
}
```

Do not maintain two competing sources of truth for deck ownership.

---

# Stable Deck Identity

If technically useful, assign each persistent deck a stable run-scoped ID such as:

```text
deck_0
deck_1
deck_2
```

The ID moves with the deck.

Possible uses:

- Logging
- Save/load verification
- Multiplayer desync detection
- Debug UI
- Ownership tracking

A deck's ID must not be tied to its current owner.

Example:

```text
Deck ID: deck_1
Original holder: Player A
Current holder: Player C
```

Original holder should have no gameplay effect.

---

# Save and Load

The modifier must support save/load correctly.

Persist whatever custom information is necessary to preserve:

- Modifier enabled state
- Current deck assignments
- Deck contents/state through the game's normal save system
- Stable deck IDs, if used
- `combatRotationCount`, if used
- Duplicate-rotation protection state, if necessary

Critical rule:

> Loading a save must **not** itself rotate the decks.

Example:

```text
Saved state:
A -> Deck C
B -> Deck A
C -> Deck B
rotationCount = 4
```

Immediately after loading:

```text
A -> Deck C
B -> Deck A
C -> Deck B
rotationCount = 4
```

The next rotation happens only when the next genuine combat begins.

---

# Reconnecting Players

A reconnecting client must receive the current authoritative deck assignments.

Never assume a player still owns their original starter deck.

The mod should follow STS2's existing reconnect/session restoration model wherever possible.

---

# Player Disconnects

Disconnect handling must avoid deleting or duplicating decks.

Preferred behavior:

1. Follow the base game's authoritative handling of disconnected players.
2. Do not change player ordering during an already initialized combat.
3. At the next combat, use the game's authoritative participating-player list.
4. Preserve every persistent deck that still belongs to the run.

Codex should inspect how STS2 handles temporary disconnects, reconnects, and permanent player removal rather than inventing a separate lifecycle.

---

# Custom Run Modifier Registration

Register exactly one gameplay modifier through the same mechanism used by other STS2 Custom Run modifiers.

Display name:

```text
Rotating Decks
```

Short description:

```text
At the start of each combat, every player's deck is permanently passed to the next player.
```

Long tooltip if supported:

```text
At the start of each combat, before starting hands are drawn, every player's current deck is permanently passed to the next player in multiplayer order. Card additions, upgrades, removals, and other permanent changes remain with the deck as it continues rotating throughout the run.
```

Do not add unrelated configuration options in the initial version.

---

# Optional Start-of-Combat Feedback

If straightforward, display a brief message after a successful rotation:

```text
Decks rotated!
```

or:

```text
Rotating Decks
You received Alex's previous deck.
```

This must not delay combat.

This UI is optional. Gameplay correctness has priority.

---

# Suggested Architecture

Keep the mod deliberately small.

Conceptual structure:

```text
RotatingDecksMod
│
├── Modifier registration
├── Combat-start hook
├── DeckRotationService
├── Multiplayer synchronization helper (only if required)
└── Save/load state (only what is required)
```

Avoid creating a large general-purpose framework.

---

# Suggested Internal Component: `RotatingDecksModifier`

Responsibilities:

- Register the Custom Run modifier.
- Provide name and description.
- Detect whether it is enabled for the current run.
- Subscribe to the appropriate combat lifecycle hook.

Use actual API/class names discovered in the project rather than the placeholder name above.

---

# Suggested Internal Component: `DeckRotationService`

Responsibilities:

- Get deterministic player order.
- Snapshot every current deck.
- Calculate the complete cyclic reassignment.
- Validate it.
- Apply it atomically where practical.
- Increment the rotation counter.
- Log the resulting assignments.
- Prevent duplicate rotation.

Conceptual API:

```pseudo
RotatePersistentDecks(players)
```

---

# Atomicity and Validation

Never leave the run in a partial rotation state.

Recommended flow:

```pseudo
players = getAuthoritativeOrderedPlayers()
oldDecks = snapshot(players)
newAssignments = calculateRotation(oldDecks)

validate(newAssignments)
applyAll(newAssignments)
synchronizeIfNeeded()
```

If validation fails before application:

```text
leave every deck unchanged
```

and write a useful error log.

---

# Rotation Invariants

After every successful rotation:

```text
number of decks before == number of decks after
```

```text
every old deck exists exactly once afterward
```

```text
no deck is duplicated
```

```text
no deck is lost
```

```text
every participating player has exactly one deck
```

```text
deck card contents are unchanged by the rotation itself
```

```text
player non-deck state is unchanged
```

Use debug assertions for these invariants if practical.

---

# Determinism Requirements

The rotation must not depend on:

- Random numbers
- Hash-map iteration order
- Local client ordering
- Frame timing
- UI animation timing
- Client-specific player display ordering

Given identical run state, every multiplayer peer must agree on the result.

---

# Relic and Card References

Some systems may hold references to particular cards or decks.

Test interactions such as:

- Bottled/innate cards
- Relics that modify or identify a particular card
- Persistent card modifications
- Event-created cards
- Modded cards with custom persistent data

Follow the base game's existing reference/ownership semantics whenever possible.

Do not add special-case redesigns unless a concrete crash or state-corruption issue requires one.

---

# Character-Specific Mechanics

Some cards may rely on mechanics usually associated with another character.

Desired philosophy:

> A player should be able to play foreign-class cards whenever the base game can reasonably support them.

Do not globally strip or replace foreign cards.

If certain cards cannot function because their required character resource does not exist, document those cases and make the smallest stability fix necessary.

Do not redesign entire characters for the first version of this mod.

---

# Compatibility Goals

Aim for compatibility with:

- Base-game multiplayer
- Existing Custom Run modifiers
- Modded cards
- Modded characters
- Modded relics
- Save/load
- Multiplayer reconnects

Do not hard-code the game's character roster.

Do not assume a fixed player count.

Use the multiplayer session's actual supported player list.

---

# Modifier Interaction Philosophy

Rotating Decks should **only rotate decks**.

Do not add:

- Bonus energy
- Bonus draw
- Bonus rewards
- Enemy scaling
- Special card rewards
- Card rarity changes
- Deck-size rules
- Automatic foreign-card conversion
- Relic swapping
- Potion swapping
- Health swapping
- Gold swapping

Other modifiers should continue to operate normally.

---

# Hook Ordering With Other Effects

Where hook ordering can be controlled, prefer:

```text
1. Persistent run state is ready for combat.
2. Rotating Decks reassigns persistent decks.
3. Combat draw/discard/exhaust piles are generated.
4. Combat-only start effects modify combat state.
5. Starting hands are drawn.
```

If exact ordering cannot be controlled, document the chosen lifecycle hook and verify it against the game's built-in Custom Run modifiers.

---

# Logging

Add useful debug logging, for example:

```text
[RotatingDecks] Combat rotation #5
[RotatingDecks] Player A receives deck_1
[RotatingDecks] Player B receives deck_2
[RotatingDecks] Player C receives deck_0
```

Duplicate protection:

```text
[RotatingDecks] Rotation already processed for encounter <id>; skipping.
```

Save/load:

```text
[RotatingDecks] Restored rotationCount=5 and current deck assignments.
```

Avoid per-frame logging.

---

# Development Instructions for Codex

**Do not invent STS2 API names or lifecycle hooks.**

Before writing the implementation, inspect the existing project and available game/mod assemblies to identify the actual systems involved.

Codex should:

1. Inspect the current STS2 mod project structure.
2. Find examples of existing Custom Run modifier registration.
3. Find the multiplayer player/session representation.
4. Find the persistent/master deck representation.
5. Identify how players' decks are serialized.
6. Identify the combat initialization lifecycle.
7. Identify the exact point where combat piles/opening hands are created.
8. Find the existing save/load extension mechanism for mods.
9. Determine how persistent multiplayer state changes are synchronized.
10. Implement the smallest solution using established game/modding patterns.
11. Prefer copying patterns from working base-game/custom-run modifier code over introducing new infrastructure.

If exact API behavior is uncertain, inspect/decompile the relevant game types rather than guessing method names.

---

# Suggested Implementation Phases

## Phase 1 — Register Modifier

Implement:

- Mod entry point if needed
- Custom Run modifier registration
- Display name
- Description
- Enabled-state check

Success condition:

```text
Rotating Decks appears in multiplayer Custom Run setup.
```

---

## Phase 2 — Implement Pure Rotation Logic

Create a deterministic function equivalent to:

```pseudo
RotatePersistentDecks(players)
```

Test it separately if the project supports unit tests.

---

## Phase 3 — Hook Combat Initialization

Invoke the rotation exactly once at the correct combat-start lifecycle point.

Success condition:

```text
Combat 1 opening hands come from the newly rotated decks.
```

---

## Phase 4 — Verify Permanent Mutations

Confirm that normal systems automatically modify the currently held rotated deck:

- Rewards
- Shops
- Rest sites
- Events
- Upgrades
- Removals
- Transforms

Only add special integration code if the game's architecture requires it.

---

## Phase 5 — Multiplayer Synchronization

Verify all clients agree on:

```text
player -> persistent deck
```

immediately after each rotation.

Use the game's existing authoritative synchronization model.

---

## Phase 6 — Save/Load

Persist only necessary custom state.

Verify that loading restores the exact assignment and does not cause an extra rotation.

---

## Phase 7 — Edge Cases

Test:

- 1 player
- 2 players
- 3 players
- Maximum supported multiplayer count
- Hallway combat
- Elite combat
- Boss combat
- Event combat
- Save immediately before a fight
- Save immediately after a fight
- Act transition
- Reconnect
- Foreign-class cards
- Modded cards if available

---

# Acceptance Tests

## Test 1 — Three-Player Rotation

Start:

```text
A -> Deck A
B -> Deck B
C -> Deck C
```

Combat 1:

```text
A -> Deck C
B -> Deck A
C -> Deck B
```

Combat 2:

```text
A -> Deck B
B -> Deck C
C -> Deck A
```

Combat 3:

```text
A -> Deck A
B -> Deck B
C -> Deck C
```

PASS only if exact.

---

## Test 2 — Persistent Card Addition

During a state where:

```text
B holds Deck A
```

Player B adds card `X` after combat.

Expected:

```text
Deck A permanently contains X.
```

When Deck A later rotates to another player, card `X` travels with it.

---

## Test 3 — Upgrade Persistence

Upgrade a card in the currently held deck.

Rotate that deck to another player.

Expected:

```text
the card remains upgraded
```

---

## Test 4 — Player State Does Not Rotate

Before:

```text
Player A:
HP = 50
Gold = 100
Relics = [R1]
Deck = Deck A
```

After rotation:

```text
Player A:
HP = 50
Gold = 100
Relics = [R1]
Deck = previous player's deck
```

PASS if only the deck changes.

---

## Test 5 — Correct Starting Hand

At combat start:

1. Decks rotate.
2. Combat piles are created.
3. Starting hands are drawn.

PASS if each player's opening hand comes from the newly received deck.

---

## Test 6 — No Duplicate Rotation

Force or observe the combat-initialization callback firing more than once if possible.

Expected:

```text
rotation counter increases exactly once
```

and ownership advances exactly one position.

---

## Test 7 — Save/Load

Save with:

```text
A -> Deck C
B -> Deck A
C -> Deck B
rotationCount = 4
```

Load the run.

Expected immediately after loading:

```text
A -> Deck C
B -> Deck A
C -> Deck B
rotationCount = 4
```

No new rotation until the next combat begins.

---

## Test 8 — Multiplayer Consistency

After a combat begins, inspect every client.

Expected:

```text
all clients report identical current deck assignments
all clients report identical card contents for each deck
```

---

## Test 9 — Act Boundary

Finish Act 1 with arbitrary deck ownership.

Expected:

```text
ownership persists into Act 2
```

The first combat in Act 2 then rotates once.

---

## Test 10 — Mixed-Class Deck

Construct a deck with cards from multiple classes and rotate it to another character.

Expected:

- Deck remains intact.
- Foreign-class cards are not deleted.
- Combat initializes normally.
- Playable foreign cards remain usable when their mechanics are available.

---

# Recommended Manual QA Scenario

Use three players and clearly identifiable starter decks.

```text
Players:
A
B
C
```

Optionally add a distinctive debug card or marker to each starting deck.

Play six combats.

Expected ownership sequence:

```text
Start:
A=A, B=B, C=C

Fight 1:
A=C, B=A, C=B

Fight 2:
A=B, B=C, C=A

Fight 3:
A=A, B=B, C=C

Fight 4:
A=C, B=A, C=B

Fight 5:
A=B, B=C, C=A

Fight 6:
A=A, B=B, C=C
```

After each fight, permanently modify at least one deck and verify that the modification travels with that deck on subsequent rotations.

---

# Non-Goals for Version 1

Do not implement:

- Random rotation
- Manual deck trading
- Player voting
- Temporary swaps
- Reverse rotation settings
- Rotation every turn
- Rotation every room
- Rotate every N combats
- Deck merging
- Shared master deck
- Relic rotation
- Potion rotation
- Gold rotation
- HP rotation
- Character rotation
- Card-class conversion
- Special rewards
- Boss-specific exceptions
- Future-deck prediction UI
- Deck history UI

Keep version 1 focused entirely on reliable permanent combat-by-combat deck rotation.

---

# Possible Future Extensions

These are explicitly out of scope, but the implementation should not unnecessarily prevent them later:

- Reverse rotation
- Randomized direction
- Rotation every N fights
- Upcoming-owner preview
- Deck travel history
- Boss ownership planner
- Configurable rotation direction
- Full-build rotation including relics

Do not implement these now.

---

# Definition of Done

The mod is complete when:

- [ ] `Rotating Decks` appears in multiplayer Custom Run setup.
- [ ] Enabling it does not alter normal run initialization beyond registering the modifier.
- [ ] The first combat rotates all decks exactly once.
- [ ] Rotation happens before combat piles and starting hands are generated.
- [ ] Each player permanently receives the previous player's deck in cyclic order.
- [ ] Every subsequent combat rotates once again.
- [ ] Card rewards remain with the currently held deck.
- [ ] Upgrades, removals, transforms, curses, and other permanent card changes remain with the deck.
- [ ] Only decks rotate; HP, gold, relics, potions, characters, and player identity do not.
- [ ] Mixed-class decks are preserved.
- [ ] Boss fights rotate normally.
- [ ] Act transitions do not reset ownership.
- [ ] Save/load preserves current ownership.
- [ ] Loading does not trigger an extra rotation.
- [ ] Multiplayer clients remain synchronized.
- [ ] No deck is duplicated or lost.
- [ ] The mod works correctly for at least 2- and 3-player games.
- [ ] Duplicate combat-start hooks cannot cause multiple rotations.
- [ ] Useful debug logging exists.
- [ ] No unrelated gameplay systems are added.

---

# Final Implementation Principle

Keep the actual gameplay rule extremely simple:

```text
EVERY REAL COMBAT:
    rotate persistent decks by one player
    then let STS2 initialize combat normally
```

Everything else in the implementation exists only to make that rule:

- deterministic,
- permanent,
- save-safe,
- multiplayer-safe,
- and compatible with normal STS2 gameplay.
