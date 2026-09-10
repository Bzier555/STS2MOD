using BaseLib.Patches.Saves;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace EnchantedRewards;

/// <summary>
/// Persists extras (see ExtraEnchantments) across save/quit/reload, which they previously didn't
/// survive at all: ExtraEnchantments is an in-memory-only ConditionalWeakTable, and the native
/// CardModel.ToSerializable/FromSerializable pair only ever reads/writes the single native slot-1
/// Enchantment - a card's extras were silently dropped on every save and just... gone on reload.
///
/// BaseLib exposes exactly the extension point this needs: ExtendedSaveHandlers&lt;CardModel,
/// SerializableCard&gt;.RegisterSave&lt;T&gt; lets a mod attach an extra piece of save data to every
/// CardModel, keyed by a string id, with its own getter/setter - wired transparently into both the
/// plain-JSON single-player save path (via a source-generator JsonPropertyInfo patch) and the binary
/// PacketWriter/PacketReader path (multiplayer sync), so this doesn't need to touch either format
/// itself. See BaseLib.Patches.Saves.ExtendedSaveHandlers/ExtendedSavePatches for how CardModel's own
/// native Enchantment field already goes through the same general mechanism BaseLib itself patches
/// in for RelicModel/PotionModel/Player/Reward/RunState.
///
/// The payload (SerializableExtraEnchantments) is just a list of the game's own SerializableEnchantment
/// - the exact same type EnchantmentModel.ToSerializable/FromSerializable already produce/consume for
/// the native slot, reused as-is rather than inventing a parallel representation. Restoring an extra
/// deliberately mirrors EnchantmentService.Apply's fresh-application path exactly (ApplyInternal then
/// ModifyCard, never re-merged into anything): each save is loaded back as its own independent
/// instance, so N stacked copies of the same type before a save/reload come back as N independent
/// instances after, with the same per-instance-compounding math as if they'd just been (re-)applied.
/// </summary>
internal sealed class SerializableExtraEnchantments : IPacketSerializable
{
    public List<SerializableEnchantment> Enchantments { get; set; } = new();

    public void Serialize(PacketWriter writer)
    {
        writer.WriteList(Enchantments, 8);
    }

    public void Deserialize(PacketReader reader)
    {
        Enchantments = reader.ReadList<SerializableEnchantment>(8);
    }
}

internal static class ExtraEnchantmentSave
{
    private const string SaveId = "EnchantedRewards.Extras";

    public static void Register()
    {
        // MegaCritSerializerContext's JSON path (the plain single-player save file, as opposed to the
        // PacketWriter/PacketReader binary path used for multiplayer sync - RegisterSave alone covers
        // that one, via SerializableExtraEnchantments implementing IPacketSerializable) only knows how
        // to (de)serialize types it's been told about. SerializableEnchantment itself is already a
        // native type (used directly by SerializableCard's own single native-slot field), so it needs
        // no help - but List<SerializableEnchantment> and this mod's own SerializableExtraEnchantments
        // wrapper are both new instantiations/types the native source-generated context has never seen,
        // and ExtendedSaveHandlers.RegisterSave only ever registers the outer
        // Dictionary<string, SerializableExtraEnchantments> for us (see
        // ExtendedSaveTypes.RegisterDictionarySaveType's call site below) - not what's *inside* it.
        // Without explicitly registering these two first, the JSON path would throw trying to resolve
        // either type the first time a save actually contains one. Order matters: each registration
        // depends on the previous one already being resolvable.
        ExtendedSaveTypes.RegisterListSaveType<SerializableEnchantment>();
        ExtendedSaveTypes.RegisterObjectSaveType<SerializableExtraEnchantments>(
            ExtendedSaveTypes.PropertyFunc<SerializableExtraEnchantments, List<SerializableEnchantment>>(
                nameof(SerializableExtraEnchantments.Enchantments)));

        ExtendedSaveHandlers<CardModel, SerializableCard>.RegisterSave(SaveId, Get, Set);
    }

    private static SerializableExtraEnchantments? Get(CardModel card)
    {
        IReadOnlyList<EnchantmentModel> extras = ExtraEnchantments.GetDirect(card);
        if (extras.Count == 0)
        {
            return null;
        }

        return new SerializableExtraEnchantments
        {
            Enchantments = extras.Select(extra => extra.ToSerializable()).ToList(),
        };
    }

    private static void Set(CardModel card, SerializableExtraEnchantments? saved)
    {
        if (saved == null)
        {
            return;
        }

        foreach (SerializableEnchantment serialized in saved.Enchantments)
        {
            EnchantmentModel restored = EnchantmentModel.FromSerializable(serialized);

            // LegacyMergedStackFix: a save from the brief window where Sown/Swift were wrongly
            // mergeable (Round 18) may have one extra instance sitting at Amount > 1 - split it back
            // into that many independent Amount=1 instances instead of restoring it as-is forever.
            int count = LegacyMergedStackFix.SplitCountFor(restored);
            if (count <= 1)
            {
                restored.ApplyInternal(card, restored.Amount);
                restored.ModifyCard();
                ExtraEnchantments.Add(card, restored);
                continue;
            }

            restored.Amount = 1;
            restored.ApplyInternal(card, restored.Amount);
            restored.ModifyCard();
            ExtraEnchantments.Add(card, restored);

            foreach (EnchantmentModel extra in LegacyMergedStackFix.BuildAdditionalCopies(restored, count))
            {
                extra.ApplyInternal(card, extra.Amount);
                extra.ModifyCard();
                ExtraEnchantments.Add(card, extra);
            }
        }
    }
}
