using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

// Ported from zaraza87/A2Core dell-merge-2026-11 (29934bea, docs/DELL-CHANGES-2026-11.md).
// Inspect another player (ViewChar): layout and evidence in Aion.Protocol/Native/InspectPackets.cs.
public sealed partial class GameSession
{
    /// <summary>
    /// ViewChar_RQ: the inspect window of a player online on this host (their worn items, guild, titles, daevanion boards, combat
    /// power). An offline or unknown character is refused (Community_Fail_NotFoundCharacter; CANDIDATE: the official reply also
    /// shows offline characters - the store keeps no other account's worn items for us to read).
    /// </summary>
    List<byte[]> ViewChar(byte[] plain)
    {
        if (InspectPackets.ParseViewChar(plain) is not { } target) return [];
        var session = target.DbId == DbId ? this
            : World?.Online.Values.Select(p => p.Session).FirstOrDefault(s => s.resources is not null && s.DbId == target.DbId);
        if (session is null) return [InspectPackets.ViewChar(InspectPackets.NotFoundCharacter, new ViewCharInfo())];
        return [InspectPackets.ViewChar(0, session.ViewCharInfo())];
    }

    /// <summary>This character as the inspect window shows it.</summary>
    internal ViewCharInfo ViewCharInfo()
    {
        var profile = Profile();
        var skins = VisualEquips(all: true).GroupBy(e => e.VisualSlot).ToDictionary(g => g.Key, g => g.First());
        var items = inventory.Values.Where(i => i.Container == InventoryItem.Equipped && i.Detail is not null).OrderBy(i => i.Slot).Select(i =>
        {
            var visual = (byte)VisualSlot.GetValueOrDefault(i.Slot);
            var shown = visual != 0 ? skins.GetValueOrDefault(visual) : null;
            return new ViewCharItem(i.ItemId, i.Detail!, visual, shown?.SkinId ?? 0, i.Maker, i.ExpireTime);
        }).ToList();
        var boards = daevanionNodes.Select(id => Daevanion.NodeOf(id)?.BoardId ?? 0).Where(b => b != 0).GroupBy(b => b).OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Count())).ToList();
        return new ViewCharInfo
        {
            Name = CharacterNames.Display(CharacterName ?? character?.Name ?? "", StaffRole), PcId = profile?.PcId ?? 1, Race = (byte)(profile?.Race ?? 1),
            Gender = (byte)(character?.Gender ?? 1), Level = level, EquipItemLevel = EquipItemLevel(), BornServerId = ServerId, CurrentServerId = ServerId,
            Online = true, TitleId = ShownTitle, GuildId = guild?.Id ?? 0, GuildName = guild?.Name ?? "", GuildEmblem = guild?.Emblem ?? 0,
            GuildGrade = MyGuildGrade(), Items = items, CustomizingData = character?.CustomizingData ?? [], Boards = boards,
            Titles = titleEquips.Where(t => t.Key is >= 1 and <= 3 && t.Value != 0).OrderBy(t => t.Key).Select(t => ((byte)t.Key, t.Value)).ToList(),
            CombatPower = CombatPower,
        };
    }

    internal long WireDbIdForTests => DbId;
}
