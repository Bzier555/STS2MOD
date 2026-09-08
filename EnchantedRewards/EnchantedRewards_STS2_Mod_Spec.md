# STS2 Mod Specification — Enchanted Rewards Custom Run Modifier

## Overview

Create a **Slay the Spire 2 custom-run modifier** (works in both single-player and multiplayer, like any other Custom Run modifier) whose gameplay effect is:

> Instead of choosing a new card after a normal monster fight, you choose one card already in your deck and permanently enchant it. Elite and Boss fights still offer the normal 3-card choice; Elites no longer also grant a Relic (added after initial playtesting - see Part 4 addenda).

Central rules, as given by the user:

1. Only **Elite** and **Boss** fights let you add a new card to your deck (the game's default `CardReward` behavior, untouched).
2. Every **normal monster fight** ("hallway" fight) that would have offered a card reward instead offers an **Enchant Reward**: pick a card you already own and apply a random enchantment to it.
3. A card can be enchanted an **unlimited number of times**, and can carry **multiple different enchantments simultaneously** (not just repeated stacks of the same one).
4. *(Added after initial playtesting)* Elite fights no longer grant a bonus Relic - only their normal card reward remains.

Working name: **EnchantedRewards** (project/internal), displayed modifier name **"Enchanted Rewards"**. Easy to rename later; nothing below depends on the name.

This document was produced by decompiling the installed `sts2.dll` (0.107.1) and `BaseLib.dll` (3.4.5) with `ilspycmd`, in the same spirit as `RotatingDecks_STS2_Mod_Spec.md`. All class/method names below are real, confirmed names from that decompile — this is not guesswork.

---

# Part 1 — What the base game already gives us for free

## 1.1 Reward generation is a clean hook point

`MegaCrit.Sts2.Core.Rewards.RewardsSet.GenerateRewardsFor(Player, AbstractRoom)` builds the default reward list per room type:

```csharp
switch (room.RoomType)
{
    case RoomType.Monster:
        // Gold, potion roll, CardReward(3 options)
    case RoomType.Elite:
        // Gold, potion roll, CardReward(3 options), RelicReward
    case RoomType.Boss:
        // Gold, potion roll, CardReward(3 options)
}
```

After building this list, `RewardsSet.GenerateWithoutOffering()` calls:

```csharp
Hook.ModifyRewards(Player.RunState, Player, Rewards, Room)
```

which iterates `runState.IterateHookListeners(null)` and calls `AbstractModel.TryModifyRewards(Player, List<Reward> rewards, AbstractRoom? room)` on every active listener — every relic, power, and **run modifier**. This is a mutable list; any listener can inspect `room.RoomType` and add/remove `Reward` instances.

**This means we don't need to touch `RewardsSet` at all.** Our `CustomModifierModel` (below) overrides `TryModifyRewards` and, when `room.RoomType == RoomType.Monster`, removes the `CardReward` instance from `rewards` and adds our own custom `EnchantReward` instead. Elite/Boss rooms are left completely alone.

This is the exact same extensibility point relics like Prayer Wheel/Molten Egg/Ruby Earrings use — a first-class, supported mechanism, not a Harmony patch.

## 1.2 The base game already has a full single-enchantment system

`MegaCrit.Sts2.Core.Models.EnchantmentModel` (abstract, `: AbstractModel`) is a real, shipped system with ~20 concrete enchantments already implemented: `Sharp`, `Swift`, `Goopy`, `RoyallyApproved`, `TezcatarasEmber`, `Vigorous`, `Nimble`, `Momentum`, `Steady`, `Adroit`, `Corrupted`, `Imbued`, `Inky`, `Instinct`, `PerfectFit`, `Slither`, `SlumberingEssence`, `SoulsPower`, `Sown`, `Spiral` (plus `Clone`, which is a marker enchantment used only by a rest-site duplication option, and `DeprecatedEnchantment`/`Mocks.MockFreeEnchantment`, which are not real content).

Key facts confirmed from the decompile:

- **`CardModel` has exactly one enchantment slot**: `public EnchantmentModel? Enchantment { get; private set; }`. There is no list.
- `MegaCrit.Sts2.Core.Commands.CardCmd.Enchant(EnchantmentModel enchantment, CardModel card, decimal amount)` is the safe application entrypoint:
  - If the card has no enchantment yet, it applies the new one via `card.EnchantInternal(...)` then `enchantment.ModifyCard()`.
  - If the card already has an enchantment of the **same type**, it just does `card.Enchantment.Amount += amount` (native same-type stacking).
  - If the card already has an enchantment of a **different type**, it **throws** `InvalidOperationException`.
- `EnchantmentModel.CanEnchant(CardModel card)` is the validity check (card type restrictions, `Unplayable` keyword, etc.) and each concrete enchantment overrides it to add its own restriction (e.g. `Goopy` requires the `Defend` tag, `Nimble` requires `card.GainsBlock`, `Spiral` requires Basic rarity Strike/Defend, `Slither` requires non-X cost).
- There is already a dedicated screen for exactly our use case: `MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckEnchantSelectScreen`, with
  `public static NDeckEnchantSelectScreen ShowScreen(IReadOnlyList<CardModel> cards, EnchantmentModel enchantment, int amount, CardSelectorPrefs prefs)`.
  `CardSelectorPrefs` even has a ready-made `EnchantSelectionPrompt` loc string ("TO_ENCHANT"). This screen is not currently wired to any shipped relic/event we found, but it is fully implemented, used by the dev console (`enchant` command) and the AutoSlay bot handler (`DeckEnchantScreenHandler`), and is exactly what we need: **we do not need to build any new UI**, just call `ShowScreen` with our own reward-selected enchantment and the player's current deck (filtered to valid targets) as candidates.

## 1.2.1 Pitfall confirmed in-game: pass `NDeckEnchantSelectScreen` a *canonical* enchantment, not a mutable one

`NDeckEnchantSelectScreen._Ready()` calls `.ToMutable()` on whatever `EnchantmentModel` it's handed, to build its own throwaway preview instance. `EnchantmentModel.ToMutable()` starts with `AssertCanonical()`, which throws if called on an instance that's already mutable (i.e. already the result of a prior `.ToMutable()` call). If the caller mutates the enchantment before handing it to `ShowScreen`, `_Ready()` throws partway through - after wiring up the card grid (so cards still show) but before it sets the enchantment icon/title/description text (so those show whatever placeholder art/text is baked into the scene file), and the later single-card preview step never gets its Confirm button enabled (an apparent hang, since input is disabled but nothing re-enables it).

Keep exactly one canonical reference (`ModelDb.Enchantment<T>()`) per rolled enchantment for its whole lifetime through the reward: pass that same canonical instance to both `CanEnchant` checks and `ShowScreen`, and only call `.ToMutable()` once, right before actually attaching it to a card via `CardCmd.Enchant`.

## 1.2.2 Pitfall confirmed in-game: `LinkedRewardSet` never marks itself `SuccessfullySelected`

Using `MegaCrit.Sts2.Core.Rewards.LinkedRewardSet` to offer the two enchantment choices (2.5) surfaced a second bug - this time in `LinkedRewardSet` itself, not our code. `Reward.SelectUnsynchronized()` is the method that normally sets `SuccessfullySelected = true` on a reward once its `OnSelect()` succeeds. When a *child* of a `LinkedRewardSet` is selected, `SelectUnsynchronized()` does this:

```csharp
if (ParentRewardSet != null)
{
    ParentRewardSet.RemoveReward(this);
    await ParentRewardSet.OnSelect();   // calls LinkedRewardSet.OnSelect() DIRECTLY - not SelectUnsynchronized()
}
```

`LinkedRewardSet.OnSelect()` is a one-line no-op (`return Task.FromResult(true);`), and because it's invoked directly rather than through `SelectUnsynchronized()`, the `LinkedRewardSet` itself never gets `SuccessfullySelected` set to `true` no matter what its children do. Two things depend on that flag being true on every *top-level* reward: `RewardsSet.AllRewardsSuccessfullySelected` (checked by `NRewardsScreen.UpdateScreenState()` before flipping "Skip" to "Proceed" and by `RewardsSetSynchronizer.CompleteRewardsSetIfNecessary` for backend/multiplayer completion tracking) and `RewardsSetSynchronizer.SelectLocalReward`'s `set.Rewards.IndexOf(reward)` (which returns `-1` for a reward nested inside a `LinkedRewardSet`, since only the `LinkedRewardSet` itself is in the top-level list - this is sent as the network message's reward index, so it's a real bug for multiplayer, but harmless for the local player's own selection since that path uses the reward object directly, not the index).

`LinkedRewardSet` has **no other callers anywhere in the decompiled assembly** (confirmed by grepping the full project dump for `new LinkedRewardSet(`), so this looks like a genuine, simply-never-exercised bug rather than something we're misusing. Fix applied in `EnchantReward.OnSelect()`: once a child succeeds, use reflection to set `ParentRewardSet.SuccessfullySelected = true` directly (its setter is `private`, so there's no public API for this). This fixes the "Skip"/"Proceed" UI transition and local backend completion tracking. The `set.Rewards.IndexOf` / network message issue is **not** fixed - that would need a patch into `RewardsSetSynchronizer` itself (netcode-critical, high blast radius if gotten wrong) and has been left as a known multiplayer-only gap until it's actually observed to cause a problem.

## 1.3 How enchantment effects actually run in combat (this is the crux of the whole plan)

An `EnchantmentModel` mixes two very different mechanisms:

**(A) One-time static modification**, done at apply time via `OnEnchant()` / `RecalculateValues()` (called together by `EnchantmentModel.ModifyCard()`), e.g. `RoyallyApproved` adds Innate+Retain keywords, `TezcatarasEmber` zeroes the cost and adds a DynamicVar, `Adroit` sets a DynamicVar's base value. This is a plain method call — trivial to reuse for extra enchantments, no hook plumbing needed.

**(B) Ongoing combat hooks**, which split into two totally different dispatch paths:

- **Generic `AbstractModel` hooks** (`AfterCardPlayed`, `ModifyShuffleOrder`, `AfterCardDrawn`, `BeforeFlush`, `AfterAutoPrePlayPhaseEntered`, `BeforeCardPlayed`, ...). These are dispatched by static methods on `MegaCrit.Sts2.Core.Hooks.Hook` that iterate `CombatState.IterateHookListeners()`. Decompiling `CombatState.IterateHookListeners()` shows it builds its listener list from creatures' powers, relics, potions, orbs, **every card's single `.Enchantment`**, run modifiers (`combatState.Modifiers`), badges, and then — critically — appends `ModHelper.IterateAllCombatStateSubscribers(combatState)`.
- **Enchantment-specific "Enchant*" hooks** (`EnchantDamageAdditive`, `EnchantDamageMultiplicative`, `EnchantBlockAdditive`, `EnchantBlockMultiplicative`, `EnchantPlayCount`). These are **not** part of the generic listener iteration at all. `Hook.ModifyDamage`/`Hook.ModifyBlock` hard-code `if (cardSource?.Enchantment != null) { ... cardSource.Enchantment.EnchantDamageAdditive(...) ... }`, and `CardModel.GetEnchantedReplayCount()` hard-codes `Enchantment?.EnchantPlayCount(...)`. These only ever look at the single slot.
- **`OnPlay`** is a third, separate case: `CardModel.OnPlayWrapper(...)` calls `Enchantment.OnPlay(choiceContext, cardPlay)` **directly, inline**, not through any `Hook.*` dispatch and not through the listener list.

`ModHelper.SubscribeForCombatStateHooks(string id, CombatHookSubscriptionDelegate del)` (and its run-state counterpart `SubscribeForRunStateHooks`) is a **documented, first-class mod extensibility point** ("Called by mods when they wish to provide custom model types to a CombatState when IterateHookListeners is called"). Any `AbstractModel` instance we hand back from our subscription delegate is folded into the exact same iteration used for every generic hook above.

**This is the key architectural insight the whole multi-enchant design rests on:** if we keep our "extra" `EnchantmentModel` instances as real, live objects and hand them to the game through `ModHelper.SubscribeForCombatStateHooks`, then `AfterCardPlayed`, `ModifyShuffleOrder`, `AfterCardDrawn`, `BeforeFlush`, and `AfterAutoPrePlayPhaseEntered` all work **automatically, with zero forwarding code**, because those enchantment classes already implement these overrides correctly. Only the three items above genuinely require custom plumbing:

1. `EnchantDamageAdditive/Multiplicative`, `EnchantBlockAdditive/Multiplicative`, `EnchantPlayCount` — need us to re-implement the combination logic ourselves, in our modifier's own (generic) `ModifyDamageAdditive/Multiplicative`, `ModifyBlockAdditive/Multiplicative`, `ModifyCardPlayCount` overrides.
2. `OnPlay` — needs a genuine Harmony patch on `CardModel.OnPlayWrapper`.
3. UI — only the single slot's icon/hover-tip is rendered anywhere today.

## 1.4 Persisting custom per-card data is a solved problem too

`CardModel` has no per-instance stable ID we can key a plain `Dictionary`/`ConditionalWeakTable` on across a save/load round trip (cards are fully reconstructed from `SerializableCard` on load). We do **not** need to invent anything here: BaseLib ships exactly this feature.

- `BaseLib.Patches.Saves.ExtendedSaveHandlers<DataType, SerializableType>` + `BaseLib.Patches.Saves.ExtendedSavePatches` already Harmony-patch `CardModel.ToSerializable`/`FromSerializable` and `SerializableCard.Serialize`/`Deserialize` to carry an arbitrary extra payload alongside every card, fully round-tripping through save files **and** multiplayer packets.
- The mod-facing surface for this is `BaseLib.Utils.SavedSpireField<TKey, TVal>` — a public utility for "attach a piece of custom, auto-saved, auto-networked data to any existing model instance" (`TKey` = `CardModel` in our case). This is precisely the extension point BaseLib provides for mods that need to remember things about existing (not mod-authored) cards.

So: `SavedSpireField<CardModel, ExtraEnchantmentData>` is our per-card storage for "the list of enchantments beyond slot 1," and it is save/load- and multiplayer-safe by construction.

---

# Part 2 — Design

## 2.1 High-level flow

```text
Monster fight ends
    ↓
RewardsSet.GenerateRewardsFor() builds [Gold, Potion?, CardReward(3)]
    ↓
Hook.ModifyRewards() calls EnchantedRewardsModifier.TryModifyRewards()
    ↓
Our modifier removes the CardReward and adds an EnchantReward instead
    ↓
Player selects the EnchantReward like any other reward
    ↓
EnchantReward.OnSelect():
    - roll a random enchantment from our curated pool
    - build the candidate card list: player's current deck, filtered to cards
      that CAN take this enchantment as an extra (see 2.3)
    - call NDeckEnchantSelectScreen.ShowScreen(candidates, enchantment, amount, prefs)
    - player picks a card
    - EnchantmentService.Apply(card, enchantment, amount) does the real work (2.4)
```

Elite and Boss fights are completely untouched: `TryModifyRewards` only acts on `RoomType.Monster`.

## 2.2 The "slot 1 first, extra layer second" rule (this is what makes the risk manageable)

To keep the amount of custom plumbing as small as possible, we deliberately use the game's real, native single-slot system for a card's **first ever** enchantment, and only fall back to our own multi-enchant layer starting from the **second** enchantment on a given card:

- If `card.Enchantment == null`: call the real `CardCmd.Enchant(enchantment, card, amount)`. Full native behavior — UI, hover tips, save/load, everything works exactly like a normal enchanted card, because it *is* one.
- If `card.Enchantment != null` and the rolled enchantment is the **same type**: also just call `CardCmd.Enchant` — this hits the native same-type stacking path (`Amount += amount`), regardless of whether the existing enchantment is in slot 1 or is itself one of our "extra" ones (our extra-enchantment records support stacking the same way; see 2.4).
- If `card.Enchantment != null` and the rolled enchantment is a **different type** than everything already on the card: this is where our custom "extra enchantment" layer kicks in (2.4).

Most cards in a typical run will only pick up 0–2 enchantments; the custom layer is a genuine "extra slots for cards that get farmed a lot" system, not something every card touches.

## 2.3 Validity checks without duplicating game logic (revised: the clone approach doesn't work)

To check whether enchantment `E` can be applied to `card` as an *extra* (2nd+) enchantment, we must not call `E.CanEnchant(card)` directly, because the base implementation unconditionally returns `false` whenever `card.Enchantment != null` and would block us even when the *only* problem is slot occupancy.

The original plan here was to reuse the game's own cloning support (`card.CreateClone()`, clear the clone's slot, call `CanEnchant` on the clone) to avoid duplicating each enchantment's restriction logic. **This doesn't work**: `CardModel.CreateClone()` explicitly throws `InvalidOperationException("Cannot create a clone of a card that is not in a combat pile.")` outside of combat - and reward selection (where this check needs to run) happens between fights, on persistent deck-pile cards, never during combat. A second option - temporarily `card.ClearEnchantmentInternal()`, check, then restore the original enchantment - was rejected too: it actually works (the field-level clear/restore round-trips cleanly), but it's real, if brief, mutation of a live card outside a controlled test context, of exactly the kind that already caused the Part 1.2.1 bug once; firing `EnchantmentChanged` twice per candidate checked, for every reroll attempt, felt like an unnecessary way to reintroduce that risk for what should be a pure query.

What `EnchantmentPool.MeetsRestrictionsIgnoringExistingEnchantment` does instead: re-implement each pool enchantment's restriction *other than* the slot-occupancy check directly, verified line-for-line against the decompiled `CanEnchant`/`CanEnchantCardType` overrides for every enchantment in the pool (Goopy needs the `Defend` tag, Nimble needs `GainsBlock`, Slither needs `!CostsX`, SoulsPower needs a local `Exhaust` keyword, Spiral needs Basic rarity + `Strike`/`Defend` tag; everything else has no restriction beyond `CanEnchantCardType`/not-Status-Curse-Quest). This does re-accept the "could drift from a future enchantment's real `CanEnchant`" risk the original plan wanted to avoid - acceptable here because the pool itself is already a hand-maintained, fixed list (see 2.5), so there's no *new* class of drift being introduced beyond what already existed.

## 2.4 The extra-enchantment layer

**Storage (revised: in-memory only for now).** The original plan called for `BaseLib.Utils.SavedSpireField<CardModel, ExtraEnchantmentRecord>` for save/multiplayer-safe persistence from day one. Implemented instead: a plain `ConditionalWeakTable<CardModel, List<EnchantmentModel>>` (see `ExtraEnchantments`), keyed on live `CardModel` reference, in-memory only. This was a deliberate scope cut, not an oversight: `SavedSpireField` only gets *automatic* save support for a fixed set of primitive types (`SavePatchUtils.IsStoreTypeBaseSupported`); anything else - including a custom `List<SerializableEnchantment>`-wrapping DTO - needs manually registering a whole custom JSON type with `ExtendedSaveTypes.RegisterObjectSaveType`/`RegisterListSaveType` (reflection-free `System.Text.Json` source-gen metadata, built up property-by-property via `ExtendedSaveTypes.PropertyFunc`). That's real, fiddly, easy-to-get-subtly-wrong surface area with no fast way to verify it round-trips correctly without a real save/quit/reload cycle in the actual game - worth doing, but as a follow-up once the in-memory mechanics are confirmed correct, not blocking the fix for "can't enchant a card with a second, different type" that prompted this phase. **Consequence**: extra enchantments currently do not survive a save/quit and reload (only a card's native slot-1 enchantment does); they persist correctly for the remainder of the current play session otherwise.

**Live instances.** Real, live `EnchantmentModel` instances (constructed via `enchantment.ToMutable()` from the pool's canonical reference, then `ApplyInternal(card, amount)` + `ModifyCard()` — the same two calls `CardCmd.Enchant` makes internally for the slot-1 case), stored directly in the `ConditionalWeakTable` list. No serialization round-trip needed while this stays in-memory-only.

**Wiring into hooks**, once per mod load / per combat:

1. `ModHelper.SubscribeForCombatStateHooks(ModId, ExtraEnchantments.AllListenersIn)` (called once, in `EnchantedRewardsMod.Initialize()`) — the delegate is re-invoked by the game every time hooks are dispatched, and returns every extra `EnchantmentModel` instance attached to any card any participating player currently owns. This alone makes `AfterCardPlayed`, `ModifyShuffleOrder`, `AfterCardDrawn`, `BeforeFlush`, `AfterAutoPrePlayPhaseEntered` work correctly for extra enchantments, with no further code, because those hooks are dispatched generically and the enchantment classes already implement them.
2. `EnchantedRewardsModifier` (already a hook listener via `state.Modifiers`) overrides the generic `AbstractModel.ModifyDamageAdditive`, `ModifyDamageMultiplicative`, `ModifyBlockAdditive`, `ModifyBlockMultiplicative`, `ModifyCardPlayCount`. Each override looks up `cardSource`'s extra-enchantment list and re-runs the same additive/multiplicative combination the base game applies for slot 1.
   **Decompiler gotcha hit here**: the C#-source-mode decompile of `ModifyDamageAdditive`/`ModifyDamageMultiplicative` (ilspycmd 8.2.0) silently dropped their trailing `CardPlay cardPlay` parameter - the real signatures take six parameters, confirmed only by re-decompiling with `-il` and reading the raw IL. Showed up as `CS0115: no suitable method found to override` despite the C#-mode signature looking right. Worth a decompile-with-`-il` sanity check if any *other* override in this codebase ever mysteriously fails the same way.
3. A Harmony patch on `CardModel.OnPlayWrapper` for `OnPlay`-based enchantments (`Swift`, `Sown`, `Adroit`, `Corrupted`, `Momentum`, `Inky`) is **still not implemented** - not needed yet, since none of those are in the current pool (see 2.5), so there was nothing to wire up. Still the plan for whenever that tier gets added (Phase 5): transpiler following the exact pattern BaseLib's own `ExtendedSavePatches.LoadExtendedCardData` already uses in this codebase (match the existing `if (Enchantment != null) { await Enchantment.OnPlay(...); Enchantment.InvokeExecutionFinished(); }` block and inject an equivalent loop over the card's extra enchantments right after it) - still the single highest-risk, most delicate patch anywhere in this plan.

## 2.5 Enchantment pool & tuning

- **Pool**: hand-maintained list (`EnchantmentPool.SafeFactories`), not a reflection scan - `Sharp`, `Instinct`, `Nimble`, `Vigorous`, `Goopy`, `RoyallyApproved`, `Steady`, `TezcatarasEmber`, `PerfectFit`, `Slither`, `SlumberingEssence`, `SoulsPower`, `Imbued`, `Spiral`. Excludes `Clone` (not real content), `DeprecatedEnchantment`/`Mocks.*`, and every `OnPlay`-dependent enchantment (`Swift`, `Sown`, `Adroit`, `Corrupted`, `Momentum`, `Inky` - tracked separately in `OnPlayFactories`, unused until Phase 5). A reflection-based scan (`ModelDb.DebugEnchantments`-style, over `ModelDb.AllAbstractModelSubtypes`) was considered so future base-game enchantments get picked up automatically, but a hand-maintained list was chosen instead so the safe/`OnPlay`/excluded tiers stay an explicit, auditable decision per enchantment rather than something a future game update could silently change the behavior of.
- **Amount**: per-type defaults (`EnchantmentPool.DefaultAmounts`) matching what real relics/events actually grant, found by searching the decompiled assembly for every `CardCmd.Enchant<T>`/`CardCmd.Enchant(...)` call site and the `DynamicVar` default it passes: `Sharp` 3 (`GnarledHammer`), `Nimble` 2 (`FresnelLens`), `Vigorous` 8 (`StoneOfAllTime`). Everything else defaults to 1, matching every other call site found (and per `StacksMeaningfully`, doesn't mechanically depend on the amount anyway). A flat `1` for everything was the original (wrong) placeholder default; don't reintroduce it.
- **Choice count**: offers **two** rolled enchantments (each guaranteed to have at least one valid target in the current deck), wrapped in `MegaCrit.Sts2.Core.Rewards.LinkedRewardSet` - the base game's own "N alternative rewards, taking one discards the rest" mechanism (rendered natively via `NLinkedRewardSet`, no custom UI needed). Falls back to a single `EnchantReward` if only one distinct enchantment type currently has a valid target, and to a `GoldReward` if none do. Whichever one is picked then shows `NDeckEnchantSelectScreen` as before to choose the target card.
- **No valid card in deck**: if the candidate list is empty for every pool enchantment (e.g. very early deck), fall back to a `GoldReward` so the player is never given a reward they cannot take.

---

# Part 3 — Suggested project structure

Mirrors `RotatingDecks`' layout:

```text
EnchantedRewards/
├── EnchantedRewards.csproj        (copy of RotatingDecks.csproj, AssemblyName swapped)
├── EnchantedRewards.json          (BaseLib manifest, depends on BaseLib >= 3.4.5)
├── Sts2PathDiscovery.props        (identical, copied as-is)
├── README.md
└── src/
    ├── EnchantedRewardsMod.cs         (ModInitializer entry point, Harmony.PatchAll + ModHelper.SubscribeForCombatStateHooks)
    ├── EnchantedRewardsModifier.cs    (CustomModifierModel: registration, TryModifyRewards override,
    │                                   ModifyDamageAdditive/Multiplicative, ModifyBlockAdditive/Multiplicative,
    │                                   ModifyCardPlayCount overrides)
    ├── EnchantReward.cs               (Reward subclass: shows the enchant-then-card selection flow)
    ├── EnchantmentPool.cs             (hand-maintained pool + per-type default amounts + candidate validity)
    ├── ExtraEnchantments.cs           (ConditionalWeakTable<CardModel, List<EnchantmentModel>> - in-memory
    │                                   only for now, see spec Part 2.4/1.4 for the SavedSpireField follow-up)
    ├── EnchantmentService.cs         (Apply — the slot-1/same-type-stack/extra-slot logic from 2.2-2.4)
    ├── CardEnchantmentVisualsPatch.cs (Harmony postfix on NCard.UpdateEnchantmentVisuals — extra-
    │                                    enchantment icon/count "flags", see Phase 4 addenda)
    ├── OutOfCombatPreviewPatch.cs     (Harmony postfixes on DamageVar/BlockVar.UpdateCardPreview —
    │                                    extras' contribution in a card's printed text outside combat)
    ├── CombatCloneSyncPatch.cs        (Harmony postfix on Player.PopulateCombatState + prefix on
    │                                    Player.AfterCombatEnd — clones extras onto combat card
    │                                    instances and syncs state back, see Round 5 addenda)
    └── ReplayCountPatch.cs            (Harmony postfix on CardModel.GetEnchantedReplayCount —
                                         extra Spiral's contribution in "Replay N" text, Round 5)

    Not yet added: a Harmony transpiler on CardModel.OnPlayWrapper (Phase 5), needed only once
    OnPlayFactories' enchantments (Swift/Sown/Adroit/Corrupted/Momentum/Inky) join the active pool.
```

---

# Part 4 — Phased implementation plan

## Phase 1 — Modifier registration
Same shape as `RotatingDecksModifier`: register via BaseLib, `Alignment`, `SortOrder`, localization (name/description). Success condition: "Enchanted Rewards" appears in Custom Run setup, and enabling it changes nothing until a monster fight ends.

## Phase 2 — Reward replacement (no multi-enchant yet)
Implement `TryModifyRewards` to swap `CardReward` → `EnchantReward` for `RoomType.Monster` only. For this phase, **restrict application to cards with no existing enchantment** (`card.Enchantment == null`) using plain `CardCmd.Enchant` — i.e. ship "single enchant slot, native behavior" first. This alone is a complete, shippable, low-risk feature and validates the reward-replacement plumbing end-to-end (including multiplayer: each player independently gets their own `EnchantReward` the same way `CardReward` already works per-player).

Success condition: every normal fight offers "enchant a card" instead of a new card; Elite/Boss fights are unchanged; save/load and multiplayer both show correct, synced results (this should require **no extra work**, since we're using 100% native single-slot mechanics here).

## Phase 3 — Same-type stacking (implemented; revised from the original plan below)
Original assumption was that this is "just" `CardCmd.Enchant`'s native `Amount += amount` path. Turns out `EnchantmentModel.CanEnchant` only allows same-type re-application when the enchantment overrides `IsStackable => true` — and **none of the ~20 shipped enchantments currently do**, so `CardCmd.Enchant` throws `InvalidOperationException` on a same-type re-apply today, native stacking or not.

Since "unlimited enchants" is the whole point of this mod, `EnchantmentService.Apply` bypasses that gate on purpose: for an already-enchanted card being offered the same enchantment type again, it increments `card.Enchantment.Amount` directly (mirroring exactly what `CardCmd.Enchant`'s stacking branch does internally, including the `CardsEnchanted` history bookkeeping) rather than going through `CanEnchant`.

A second wrinkle: most enchantments don't actually read `Amount` for their effect. Of the Phase-1/2 pool, only `Sharp`, `Nimble`, `Vigorous`, and `Goopy` scale with `Amount`; `RoyallyApproved`/`Steady` are one-time keyword adds, `TezcatarasEmber`'s bonus damage is a fixed `DynamicVar` unrelated to `Amount`, and `PerfectFit`/`Slither`/`SlumberingEssence`/`SoulsPower`/`Imbued`/`Spiral`/`Instinct` have no numeric effect at all. `EnchantmentPool.StacksMeaningfully` tracks which types are worth re-offering onto an already-enchanted card for this reason — re-rolling a non-scaling type just treats the card as ineligible (same as before Phase 3), rather than handing out a reward with zero effect.

Success condition: rolling e.g. `Sharp` again onto a card that already has `Sharp 1` raises it to `Sharp 2` and the extra damage shows up in combat; rolling `Steady` onto a card that already has `Steady` never offers that card as a candidate (nothing to gain).

## Phase 4 — Extra-enchantment layer (implemented; in-memory only — see 2.4 for the revision)
Built `ExtraEnchantments` (a plain `ConditionalWeakTable<CardModel, List<EnchantmentModel>>`, not the originally-planned `SavedSpireField`), `EnchantmentService.Apply`'s third case for a card's 2nd+ *distinct* enchantment, and the `ModHelper.SubscribeForCombatStateHooks` registration + `EnchantedRewardsModifier`'s combat-math overrides (2.4, items 1–2). Uses the **safe pool** (excludes `OnPlay`-dependent enchantments) so every extra enchantment offered is fully correct without needing the `OnPlayWrapper` patch.

Success condition: a card can hold e.g. `Spiral` (slot 1) + `Vigorous` (extra) simultaneously, and both contribute correctly (extra replay from Spiral, extra damage from Vigorous) in combat; a card can hold the same extra enchantment applied twice and see its `Amount` grow. **Not yet met**: save/load and multiplayer round-trip preservation of extras - deferred, see 2.4's storage note. If this needs to be solved before continuing further, that's the next thing to build (`SavedSpireField`/`ExtendedSaveTypes` custom object registration), not a new design.

## Phase 5 — `OnPlayWrapper` patch, unlock full pool
Implement and thoroughly test the `CardModel.OnPlayWrapper` transpiler, then re-enable `Swift`, `Sown`, `Adroit`, `Corrupted`, `Momentum`, `Inky` in the pool for both slot-1 and extra-slot application.

## Phase 6 — UI polish
Slot-1 enchantment icon/hover-tip rendering already works natively (nothing to do there). Add a small patch to whatever card node renders `card.Enchantment`'s icon so it also renders icons/hover tips for the card's extra enchantments (e.g. a small stacked row of icons, or a "+N enchantments" badge with a combined tooltip listing them). This is the least-defined part of the plan and should be scoped visually in-editor/in-game once Phase 4 is working, rather than designed blind.

## Phase 7 — Balance pass & edge cases
- Unlimited application means a heavily-farmed starter card could accumulate a long tail of enchantments; verify there's no runaway performance issue in the hook loops (bounded by deck size in practice, should be fine).
- Verify Ascension/other run modifiers that also touch `TryModifyRewards` compose correctly (e.g. anything that changes reward count or reward types).
- Verify behavior when `RoomType.Monster` fights are skipped entirely by other content (e.g. certain events) — should simply mean no `EnchantReward` is generated, same as today for `CardReward`.
- Confirm the "no valid candidate card" fallback (2.5) triggers correctly on a very early, tiny/homogeneous deck.

## Phase 4 addenda (added after initial playtesting)

- **Elite relics removed**: `TryModifyRewards` now also handles `RoomType.Elite`, removing any `RelicReward` from the rewards list (`rewards.RemoveAll(r => r is RelicReward)`). The `CardReward` for Elites is untouched - they still add a new card, they just no longer also grant a relic. Not part of the original central rules; added directly per user request once the base mod was working.
- **`LinkedRewardSet` completion bugfix**: see 1.2.2. Needed once real playtesting actually exercised the two-option enchant choice UI, which is exactly the kind of thing that can't be found by reasoning about the decompiled source alone - worth remembering as a general lesson for any other unused-by-native-content system this mod leans on (`NDeckEnchantSelectScreen` itself being the other one; it happened to work correctly once the Part 1.2.1 pitfall was fixed, but had no such guarantee going in either).
- **Critical combat-math bugfix ("all Defends gain 100 block")**: the four `EnchantedRewardsModifier` overrides added in Phase 4 (`ModifyDamageAdditive`/`Multiplicative`, `ModifyBlockAdditive`/`Multiplicative`) were written under a wrong assumption: that they should return the *new total* (original amount plus/times the extra enchantments' contribution). The actual contract, confirmed against `AbstractModel`'s own defaults (`0m`/`1m`) and real listeners (`StrengthPower.ModifyDamageAdditive` returns only `base.Amount`; `VulnerablePower.ModifyDamageMultiplicative` returns only `1.5m`), is to return *only this listener's own contribution* - the caller (`Hook.ModifyDamage`/`ModifyBlock`) sums/multiplies every listener's contribution together itself. Getting this backwards meant the caller added our whole (already-correct-looking) return value on top of the real total again, for *every* card play in the game regardless of whether it had any extra enchantments, snowballing on every recomputation. `ModifyCardPlayCount` was unaffected - its own default (`return playCount;`, i.e. pass the running total through unchanged) genuinely is the "running total" shape, confirmed the same way.
- **Enchantment pool tuning**: removed `SlumberingEssence` (reported non-functional; root cause not confirmed - its `BeforeFlush` hook goes through the same generic dispatch path as its still-working `PerfectFit`/`Slither` siblings).
- **Extra-enchantment card UI ("flags")**: `CardEnchantmentVisualsPatch` (new, see Part 3 file list) postfixes `NCard.UpdateEnchantmentVisuals` and clones the native single-enchantment icon+amount "flag" Control via Godot's `Duplicate()` once per extra enchantment, so a multi-enchanted card visibly shows all of them, not just the native slot's. This was written without the ability to see it rendered in-game; known gaps to expect and iterate on: no hover tooltip, no "disabled this turn" shader treatment, no overflow handling for many-enchantment cards. This is the Part 2.4/Phase 6 "UI polish" work, done earlier than originally planned once it became clear multi-enchant cards were otherwise completely unverifiable in play.
- **Verify-signature-with-reflection lesson**: Slay the Spire 2 is actively patched, and a game update genuinely changed `ModifyDamageAdditive`/`ModifyDamageMultiplicative`'s parameter list (removing a trailing `CardPlay` parameter that a previous game version had) partway through this project, on top of an earlier decompiler-only version of the same confusion (Part 1.3's `-il` note). A quick reflection check (`typeof(AbstractModel).GetMethod("X").GetParameters()` in a throwaway console app referencing the live `sts2.dll`) resolved it in seconds and is more reliable than decompiling for this kind of "did a signature change" question - reach for it first.
- **Bugfix: extra enchantments invisible in card text outside of combat**: `CardModel.UpdateDynamicVarPreview` only sets `runGlobalHooks = true` (which makes `DamageVar`/`BlockVar.UpdateCardPreview` call `Hook.ModifyDamage`/`ModifyBlock` - the chain that reaches `EnchantedRewardsModifier`'s combat-math overrides) when the card has an active `CombatState` *and* is in the Hand or Play pile. Everywhere else, those two classes only ever apply `card.Enchantment` (slot-1) directly and skip the hook chain entirely - not a mod bug, just something vanilla content never needed to worry about since a card only ever has one enchantment. `OutOfCombatPreviewPatch` (new file) postfixes both `UpdateCardPreview` overrides to layer in extras' contributions (same additive-then-multiplicative order as the in-combat path) whenever the native code skipped the hook chain. Actual in-combat damage/block resolution was already correct before this fix - only the *displayed text* outside of combat was wrong.
- **Root cause found for "only the deck menu shows extras correctly" (both the flags *and* actual combat damage/block)**: `Player.PopulateCombatState` clones every persistent deck card into a brand new `CardModel` instance for the draw pile (`state.CloneCard(item)`) at the start of every combat. `ExtraEnchantments`'s `ConditionalWeakTable<CardModel, ...>` is keyed by object reference, so a lookup against the combat-instance card that's actually in hand/played/discarded always misses - only the deck menu, which shows the persistent instances directly, ever found anything. This explains why the user's playtest showed the same "one flag, wrong damage" result even when actually playing the card in combat, not just when previewing it. Confirmed and fixed once the user reported that actual played-card damage was wrong too, not just its printed text - which ruled out the `runGlobalHooks`/preview-only explanation above as the *complete* story. Fix: `ExtraEnchantments.Get` now falls back to `CardModel.DeckVersion` (the combat clone's own link back to its persistent original - the same field native code like `Goopy` already uses to write changes back to the deck copy) when the direct instance isn't found, walking the whole chain in case of a clone-of-a-clone. Lesson for next time: a "some screens work, others don't" report is a strong hint to look for an object-identity mismatch (multiple instances representing "the same" game entity) before reaching for screen-specific UI theories.

## Round 5 addenda (after a full playtested run)

- **Restrictions relaxed, validity checking unified**: `EnchantmentPool.MeetsRestrictions` (renamed) is now the *only* validity check, used for a card's first enchantment too, not just its second-or-later - previously the first enchantment still went through the real, more restrictive `EnchantmentModel.CanEnchant`, so the relaxed rules below silently didn't apply until a card's second enchantment. `EnchantmentService.Apply`'s slot-1 case correspondingly no longer calls `CardCmd.Enchant` (which re-validates with that same real `CanEnchant` and would throw for anything now-allowed that it wouldn't allow) and instead inlines the equivalent `EnchantInternal`/`ModifyCard`/history-tracking steps directly. Per explicit request, native category/flavor restrictions unrelated to whether the effect does anything were dropped entirely (Spiral: was Basic-rarity Strike/Defend only; RoyallyApproved: was Attack/Skill only) or replaced with an equivalent functional check (Goopy: was "must have the Defend tag", now `card.GainsBlock`, matching Nimble). Restrictions that are genuinely functional were kept: Sharp/Instinct/Vigorous need a "powered attack" to scale, Nimble/Goopy need `GainsBlock`, Slither needs a non-X cost, SoulsPower needs an actual Exhaust keyword to remove. One deliberate exception kept out of caution rather than because it's clearly functional: Imbued's native Skill-only restriction - auto-playing an Attack automatically raises a targeting question its own implementation doesn't appear to resolve. Flagged explicitly as a judgment call, revisit if it turns out to be overly conservative. Also relevant for whenever `OnPlayFactories` (Phase 5) joins the pool: Inky has the same kind of native category restriction (Shiv-only) the user already flagged as arbitrary for Spiral.
- **Bugfix: several extra enchantments' own logic silently never triggering (Goopy's block growth, Vigorous's disable-after-use, PerfectFit's shuffle reorder, Slither's cost randomization)**: all four compare the specific `CardModel` instance they're attached to against the card an event is actually happening to (e.g. Goopy's `AfterCardPlayed`: `if (cardPlay.Card != base.Card) return;`). Since `EnchantmentModel.Card` is set once, at apply time, to the *persistent* deck card, and the card actually being played during combat is the *combat clone* (see the root-cause entry above), the comparison was always false for an extra - this is the same object-identity class of bug, manifesting differently: not as an invisible lookup, but as an enchantment whose own comparison-based logic never fires. The native game avoids this for slot-1 only because cloning a card for combat *also* clones its `Enchantment` (`CardModel.DeepCloneFields()` calls `Enchantment.ClonePreservingMutability()` and re-points the clone's `Card`) - extras never got that treatment, since they live in a side table the native cloning code doesn't know exists. `CombatCloneSyncPatch` (new file) gives them it: postfixes `Player.PopulateCombatState` to clone each extra onto its card's combat instance (`ClonePreservingMutability()` + `ApplyInternal`, properly `Card`-linked, mirroring the native slot-1 clone exactly), and prefixes `Player.AfterCombatEnd` (a prefix, not postfix, since the method's own body may tear down piles before returning) to sync `Amount`/`Status` back to the persistent original before that happens, so progress carries into the next fight.
- **Bugfix: extra Spiral's replay bonus missing from "Replay N" hover tip/description text**: `CardModel.GetEnchantedReplayCount()` only reads a card's native slot-1 `Enchantment` directly, and its result is used two ways: real gameplay (feeds `Hook.ModifyCardPlayCount`, which does correctly reach extras through a hook chain) and display-only text (hover tip/description call it directly, no hook dispatch). Patching the shared method itself (`ReplayCountPatch`, new file) to add extras' contribution fixes both uses - but only because `EnchantedRewardsModifier`'s previous `ModifyCardPlayCount` override was removed at the same time, since otherwise extras' contribution would flow through *both* the patched `GetEnchantedReplayCount()` and the still-present override, double-counting for real gameplay. Unlike `OutOfCombatPreviewPatch`'s situation (`DamageVar`/`BlockVar.UpdateCardPreview` genuinely branch differently for real-vs-preview), `GetEnchantedReplayCount()` has no such branch to conflict with, so consolidating into one patched method was possible here where it wasn't there.
- **Bugfix: reward hover tooltip only showed the enchantment's bare name**: added `ExtraHoverTips => EnchantmentModel.HoverTips`, matching what the wiki-style "read the actual enchantment description when hovering" request asked for. Needed a throwaway mutable preview clone with `Amount` explicitly set first, since `_enchantment` is the shared canonical pool instance (see Part 1.2.1) and its `Amount` is never itself set - reading its description directly would print "0". Mirrors the exact pattern `NDeckEnchantSelectScreen._Ready()` already uses for its own preview.
- **Known, deliberately unaddressed limitation: native enchant-granting relics/events and extras**: things like `FresnelLens`/`WingCharm`/`GnarledHammer`/various events call the real, restrictive `CanEnchant` to filter their own candidate cards - since that only ever looks at a card's native slot, a card with anything in slot 1 will correctly (if conservatively) be excluded from their offer, same as any other already-enchanted card would be. That's expected, safe behavior, not a bug. Not confirmed: the user's report of an enchantment (Nimble) apparently disappearing after a card was also granted the `Clone` enchantment (used by a rest-site "duplicate this card" option) - plausible read is that whatever native code performs that duplication only carries over a card's slot-1 state, same underlying class of problem as the combat-cloning bug just fixed, but for a different (unidentified) native code path. Not fixed pending a clearer reproduction; patching an unconfirmed, arbitrary piece of native relic/event card-duplication code blind isn't a good trade.

## Round 6 addenda (stacking still broken; "am I seeing a subset of enchantments")

- **Stacking redesigned from the ground up**: Round 5's `StacksMeaningfully` + "merge into an existing instance's `Amount`" design was itself wrong, not just too narrow. Most enchantments don't read `Amount` at all for their actual effect - Instinct's multiplier and Spiral's replay bonus are both fixed constants - so "increment Amount" was a silent no-op for them, and the accompanying "already has this type, only offer again if it's in the stack-worthy set" gate meant Instinct (not in that set) was never offered again once present, exactly matching the user's literal report ("if a card already has instinct enchanted then it doesn't appear in the enchant target selection menu"). Correct model, now implemented: every application past the first is *always* a new, independent `EnchantmentModel` instance (`EnchantmentService.Apply` has no merge branch anymore) - each instance contributes its own share through the *existing* per-extra hook combination loop with zero per-type math. This is what makes "Instinct doubles again per stack" (2x * 2x = 4x, via the existing multiplicative loop) and "Spiral adds another +1 replay per stack" (via the existing `ReplayCountPatch` loop) fall out for free. `BenefitsFromRepeatApplication` (renamed from `StacksMeaningfully`, expanded) is now scoped correctly: "would a second copy of this type do anything more at all", not "does this type happen to read Amount".
- **Full pool enabled - Phase 5's OnPlayWrapper transpiler turned out to be unnecessary**: a fresh decompile of `CardModel.OnPlayWrapper` (this game version) shows `await Hook.AfterCardPlayed(combatState, choiceContext, cardPlay);` sitting *inside* the same per-replay `for` loop as the native slot-1 `Enchantment.OnPlay(...)` call, right after it, for the same `cardPlay`/iteration. Since `AfterCardPlayed` is already a generic, per-listener-dispatched hook (confirmed working since Round 5's Goopy fix), `EnchantedRewardsModifier` now overrides it and manually invokes each extra's own `OnPlay(choiceContext, cardPlay)` (then `InvokeExecutionFinished()`, matching the native post-OnPlay pattern) - reaching the same once-per-actual-play granularity the native path has, with no IL transpiler needed at all. Safe to call unconditionally on every extra since `EnchantmentModel.OnPlay`'s base implementation is a no-op for anything that doesn't override it. This unlocks `Swift`, `Sown`, `Adroit`, `Corrupted`, `Momentum`, `Inky` - the entire previously-excluded tier - joining the single unified `Factories` list (the "safe"/"OnPlay" split is gone). One accepted, minor ordering difference from native: an extra's `OnPlay` now runs *after* `Hook.AfterCardPlayed`'s other effects for that same play (including slot-1's own `OnPlay`), not inline before them - not expected to matter for anything currently in the pool, but worth remembering if a future addition's effect turns out to be order-sensitive.
- With the pool unified, `Corrupted`/`Momentum` were added to the Attack-only functional-restriction group (same reasoning as Sharp/Instinct/Vigorous - their damage effects need a "powered attack"). `Swift`/`Sown`/`Adroit`/`Inky` have no native `CanEnchantCardType` restriction in this game version's decompile - including Inky, which the user believed (likely from the wiki, or a different game version) had a native Shiv-only restriction; nothing in this build's `Inky.cs` shows one, so there was nothing to relax.
- This is the least-verified round yet in terms of raw surface area touched (a brand-new hook override, six newly-unlocked enchantments, and a rewritten stacking model, all at once) - flagged clearly to the user as needing thorough testing rather than presented as a sure thing.

---

# Part 5 — Open tuning questions (recommended defaults above; flag if you want different behavior)

1. **Enchantment `Amount` per application** — default 1 per roll, same enchantment can be rolled again later to stack. Confirm or specify per-enchantment values.
2. **Reroll support** — should `EnchantReward` support a Driftwood-style reroll like `CardReward` does? Not in Phase 1–5; easy to add later (`CardReward.Reroll()` is the reference pattern).
3. **Should Elites/Bosses ever *also* occasionally grant a bonus enchant** (in addition to their normal card reward), or should they stay 100% untouched? Spec above assumes 100% untouched, per "only bosses and elite fights let you add cards" (read as: elites/bosses = card adds only, monsters = enchants only).
4. **Full enchantment pool vs. a curated subset** — default is "everything except Clone/Deprecated/Mocks," growing over the phases above as `OnPlay` support lands. Confirm whether any specific enchantment should be excluded for balance reasons (e.g. `Spiral`, which is restricted to Basic-rarity Strike/Defend cards and may feel bad as a random monster-fight roll if the player has few such cards left).
