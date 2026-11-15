namespace Aion.World;

// Ported from zaraza87/A2Core dell-merge-2026-11 (29934bea, docs/DELL-CHANGES-2026-11.md), adapted: the bag-room check before a grant
// (their GameSession.BagRoom.cs BagRoomFor). OURS: a new item needs a free bag slot unless a stack of the same item is already in the
// bag; the bag holds containerSlotExtends.BagSlotLimit slots (27 + the extensions); currencies (93xxxxxx: Kina and the like) take no
// slot. Used by the Black Cloud Merchants purchase and the Arcana creation (Item_Common_Fail_NotEnoughSlotCountContainer 264).
public sealed partial class GameSession
{
    public const int NotEnoughBagSlots = 264;   // EResult Item_Common_Fail_NotEnoughSlotCountContainer

    static bool TakesNoBagSlot(int templateId) => templateId / 1_000_000 == 930;

    /// <summary>Bag slots in use (the stacks in the bag, currencies aside).</summary>
    internal int BagSlotsUsed => storedItems.Values.Count(i => i.Location == "bag" && !TakesNoBagSlot(i.TemplateId));

    /// <summary>Whether the bag takes these items (new stacks need free slots; <paramref name="freed"/> stacks leave first).</summary>
    internal bool BagRoomFor(IEnumerable<int> templateIds, int freed = 0)
    {
        var needed = templateIds.Distinct().Count(id => !TakesNoBagSlot(id)
            && !(data.Item(id) is null && storedItems.Values.Any(i => i.Location == "bag" && i.TemplateId == id)));
        return needed == 0 || BagSlotsUsed - freed + needed <= containerSlotExtends.BagSlotLimit;
    }
}
