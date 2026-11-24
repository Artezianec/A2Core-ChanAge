using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
  static readonly Dictionary<string, int> DaevanionCrystalPointTypes = new(StringComparer.OrdinalIgnoreCase)
  {
    ["daevanion_crystal"] = 1, ["ConquestCrystal"] = 5, ["DaevanionPointConquest"] = 5, ["battle_crystal"] = 5,
  };

  /// <summary>CANDIDATE exp per template when Item.dat does not export a second UseEffectValue (see tools/data/boxes.json).</summary>
  static readonly Dictionary<int, int> BattlePassExpByItem = new()
  {
    [503700042] = 500, [503700043] = 500, [503700048] = 500, [503700049] = 500, [582000001] = 500,
  };

  const int DefaultBattlePassExpPerItem = 500;

  static string ItemUseRefusalReason(string? effect)
  {
    if (effect is null || !ItemUseEffects.ReportsAsImplemented(effect))
      return "unimplemented_use_effect";
    return ItemUseEffects.IsNotDirectUse(effect) ? "not_direct_use" : "handler_failed";
  }

  async Task<List<byte[]>?> DispatchItemUseEffectAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use,
    bool reply, long now, CancellationToken ct, int count)
  {
    if (ItemUseEffects.IsNotDirectUse(use.Effect)) return null;
    if (ItemUseHandlers.TryGetValue(use.Effect, out var handler))
      return await handler(store, character, row, use, reply, now, ct, count);
    if (AgitDecoTables.Embedded.TryDecoId(row.TemplateId, out var decoId))
      return await UseAgitDecoItemAsync(store, character, row, decoId, reply, ct);
    return null;
  }

  Dictionary<string, Func<INativeCharacterStore, Guid, ItemState, ItemUse, bool, long, CancellationToken, int, Task<List<byte[]>?>>>? itemUseHandlers;
  Dictionary<string, Func<INativeCharacterStore, Guid, ItemState, ItemUse, bool, long, CancellationToken, int, Task<List<byte[]>?>>> ItemUseHandlers =>
    itemUseHandlers ??= BuildItemUseHandlers();

  Dictionary<string, Func<INativeCharacterStore, Guid, ItemState, ItemUse, bool, long, CancellationToken, int, Task<List<byte[]>?>>> BuildItemUseHandlers() =>
    new(StringComparer.Ordinal)
    {
      ["GetVehicle"] = (s, c, r, u, reply, _, ct, _) => UsePetItemAsync(s, c, r, u, reply, ct),
      ["GetVehicleSoul"] = (s, c, r, u, reply, _, ct, n) => UseSoulItemAsync(s, c, r, u, reply, ct, n),
      ["GetVehicleSoulCrystal"] = (s, c, r, u, reply, _, ct, n) => UseCrystalItemAsync(s, c, r, u, reply, ct, n),
      ["Fix_VehicleCreatureLevel"] = (s, c, r, u, reply, _, ct, _) => UseBoardLevelItemAsync(s, c, r, u, reply, ct),
      ["Skill"] = (s, c, r, u, reply, now, ct, _) => UseSkillItemAsync(s, c, r, u, reply, now, ct),
      ["Telpo_Home"] = (s, c, r, _, reply, now, _, _) => Task.FromResult(
        itemCast is null ? StartItemCast(r, Boxes.Cooldown(r.TemplateId), reply, now) : null),
      ["Telpo_Agit"] = (s, c, r, _, reply, now, _, _) => Task.FromResult(
        guild?.AgitId is > 0 && itemCast is null ? StartItemCast(r, Boxes.Cooldown(r.TemplateId), reply, now) : null),
      ["GetKina"] = async (s, c, r, u, reply, _, ct, n) =>
        u.Values is [var amount, ..] && long.TryParse(amount, out var kina) && kina > 0
          ? await UseKinaItemAsync(s, c, r, kina, Math.Clamp(n, 1, (int)Math.Min(int.MaxValue, r.Quantity)), reply, ct) : null,
      ["GetNonBindingKina"] = async (s, c, r, u, reply, _, ct, n) =>
        u.Values is [var amount, ..] && long.TryParse(amount, out var kina) && kina > 0
          ? await UseKinaItemAsync(s, c, r, kina, Math.Clamp(n, 1, (int)Math.Min(int.MaxValue, r.Quantity)), reply, ct) : null,
      ["GetTitle"] = async (s, c, r, u, reply, _, ct, _) =>
        u.Values is [var titleText, ..] && int.TryParse(titleText, out var titleId)
          ? await UseTitleItemAsync(s, c, r, titleId, reply, ct) : null,
      ["GetSkin"] = (s, c, r, u, reply, _, ct, _) => UseSkinItemAsync(s, c, r, u, reply, ct),
      ["GetSkinSet"] = (s, c, r, u, reply, _, ct, _) => UseSkinItemAsync(s, c, r, u, reply, ct),
      ["GetSocialAction"] = async (s, c, r, u, reply, _, ct, _) =>
        u.Values is [var emoteText, ..] && int.TryParse(emoteText, out var emote)
          ? await UseSocialActionItemAsync(s, c, r, emote, reply, ct) : null,
      ["GetDaevanionPoint"] = (s, c, r, u, reply, _, ct, n) => UseDaevanionPointItemAsync(s, c, r, u, reply, ct, n),
      ["Fix_DaevanionPoint"] = UseFixDaevanionPointItemAsync,
      ["GetStigmaShard"] = (s, c, r, u, reply, _, ct, n) => UseStigmaShardItemAsync(s, c, r, u, reply, ct, n),
      ["GetStigmaShardSuperior"] = UseStigmaShardSuperiorItemAsync,
      ["GetGodstone"] = (s, c, r, _, reply, _, ct, n) => UseGodstoneItemAsync(s, c, r, reply, n, ct),
      ["GetWing"] = async (s, c, r, u, reply, _, ct, _) =>
        u.Values is [var wingText, ..] && int.TryParse(wingText, out var wing) && wing > 0
          ? await UseWingItemAsync(s, c, r, wing, reply, ct) : null,
      ["AscensionGrade"] = async (s, c, r, u, reply, _, ct, _) =>
        u.Values is [var gradeText, ..] && int.TryParse(gradeText.Split('_')[^1], out var grade) && grade is >= 2 and <= 6
          ? await UseAscensionItemAsync(s, c, r, grade, reply, ct) : null,
      ["BattlePassExp"] = UseBattlePassExpItemAsync,
      ["BattlePassUnlock"] = UseBattlePassUnlockItemAsync,
      ["AddContentsTicket"] = UseAddContentsTicketItemAsync,
      ["GetDeco"] = UseGetDecoItemAsync,
      ["GetQuest"] = (_, _, _, _, _, _, _, _) => Task.FromResult<List<byte[]>?>(null),
    };

  async Task<List<byte[]>?> UseFixDaevanionPointItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, long _, CancellationToken ct, int useCount)
  {
    useCount = (int)Math.Clamp((long)useCount, 1L, Math.Min(row.Quantity, int.MaxValue));
    (int PointType, int Amount)? grant = Daevanion.ItemGrant(row.TemplateId);
    if (grant is null && use.Values is [var crystal, var amountText, ..]
        && DaevanionCrystalPointTypes.TryGetValue(crystal, out var pointType) && int.TryParse(amountText, out var amount) && amount > 0)
      grant = (pointType, amount);
  if (grant is not { } g) return null;
    var total = g.Amount * useCount;
    var consumed = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, useCount, ct) ? [[]] : []);
    if (consumed.Count == 0) return null;
    usedCountApplied = true;
    daevanionBonus[g.PointType] = daevanionBonus.GetValueOrDefault(g.PointType) + total;
    if (!await PersistDaevanionAsync(ct)) return null;
    return
    [
      .. Used(row, reply),
      PacketWriter.Opcode(UpdateItemDaevanionPointNt).U8(1).U8((byte)g.PointType).I32(total).ToArray(),
      DaevanionPointUpdate(),
      .. await InventoryChangesAsync(ct, 0, DecreaseItemUse),
    ];
  }

  async Task<List<byte[]>?> UseStigmaShardSuperiorItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, long _, CancellationToken ct, int useCount)
  {
    useCount = (int)Math.Clamp((long)useCount, 1L, Math.Min(row.Quantity, int.MaxValue));
    var perItem = 1;
    if (use.Values is [var amountText, ..] && int.TryParse(amountText, out var parsed) && parsed > 0) perItem = parsed;
    var grant = perItem * useCount;
    if (level < StigmaData.UnlockLevel) return null;
    var consumed = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, useCount, ct) ? [[]] : []);
    if (consumed.Count == 0) return null;
    var total = await store.AddStigmaBonusShardsAsync(character, grant, ct);
    if (total is null) return null;
    stigmaBonusShards = total.Value;
    usedCountApplied = true;
    return [.. Used(row, reply), .. await InventoryChangesAsync(ct, 0, DecreaseItemUse), .. StigmaShardSync()];
  }

  async Task<List<byte[]>?> UseBattlePassExpItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, long now, CancellationToken ct, int useCount)
  {
    if (use.Values is not [{ Length: > 0 } groupName, ..]) return null;
    var group = PassGroupByItemKey(groupName);
    if (group is null || group.FromMs > now || now > group.ToMs) return null;
    useCount = (int)Math.Clamp((long)useCount, 1L, Math.Min(row.Quantity, int.MaxValue));
    var perItem = BattlePassExpByItem.GetValueOrDefault(row.TemplateId, DefaultBattlePassExpPerItem);
    foreach (var value in use.Values.Skip(1))
      if (int.TryParse(value, out var parsed) && parsed > 0) { perItem = parsed; break; }
    var exp = perItem * useCount;
    var consumed = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, useCount, ct) ? [[]] : []);
    if (consumed.Count == 0) return null;
    usedCountApplied = true;
    return [.. Used(row, reply), .. AddPassExp(group, exp, now), .. await InventoryChangesAsync(ct, 0, DecreaseItemUse)];
  }

  async Task<List<byte[]>?> UseBattlePassUnlockItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, long now, CancellationToken ct, int useCount)
  {
    if (use.Values is not [{ Length: > 0 } key, ..]) return null;
    if (!key.EndsWith("_Pay", StringComparison.OrdinalIgnoreCase) && !key.Contains("_Pay", StringComparison.OrdinalIgnoreCase)) return null;
    var group = PassGroupByItemKey(key);
    if (group is null || group.FromMs > now || now > group.ToMs) return null;
    var passes = group.Passes.Where(IsPaid).Where(p => !PassUnlocked(p)).ToList();
    if (passes.Count == 0) return null;
    var consumed = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
    if (consumed.Count == 0) return null;
    var changes = await UnlockBattlePassPassesAsync(passes, now, ct);
    return [.. Used(row, reply), .. changes, .. await InventoryChangesAsync(ct, 0, DecreaseItemUse)];
  }

  async Task<List<byte[]>?> UseAddContentsTicketItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, long now, CancellationToken ct, int useCount)
  {
    if (use.Values is not [var ticketName, var countText, ..] || ContentTickets.ByName(ticketName) is not { Counted: true } ticket
        || !int.TryParse(countText, out var perItem) || perItem <= 0) return null;
    useCount = (int)Math.Clamp((long)useCount, 1L, Math.Min(row.Quantity, int.MaxValue));
    var add = perItem * useCount;
    var consumed = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, useCount, ct) ? [[]] : []);
    if (consumed.Count == 0) return null;
    var newCount = Math.Min(ticket.Max, ContentTicketCount(ticket.Id, now) + add);
    contentTickets[ticket.Id] = (newCount, now);
    contentTicketsDirty.Add(ticket.Id);
    await FlushContentTicketsAsync(ct);
    usedCountApplied = useCount > 1;
    var updates = ContentTicketUpdates();
    return [.. Used(row, reply), .. updates, .. await InventoryChangesAsync(ct, 0, DecreaseItemUse)];
  }

  async Task<List<byte[]>?> UseGetDecoItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, long now, CancellationToken ct, int useCount)
  {
    if (AgitDecoTables.Embedded.TryDecoId(row.TemplateId, out var decoId))
      return await UseAgitDecoItemAsync(store, character, row, decoId, reply, ct);
    if (use.Values is [{ Length: > 0 }, ..]) return null;
    return null;
  }

  AchievementTables.PassGroup? PassGroupByItemKey(string key)
  {
    var norm = key;
    foreach (var suffix in new[] { "_Pay1", "_Pay2", "_Pay", "_Free1", "_Free2", "_Free" })
      if (norm.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
      {
        norm = norm[..^suffix.Length];
        break;
      }
    return Achievements.PassGroups.FirstOrDefault(g => string.Equals(g.Name, norm, StringComparison.OrdinalIgnoreCase)
      || string.Equals(g.Name, key, StringComparison.OrdinalIgnoreCase));
  }

  async Task<List<byte[]>> UnlockBattlePassPassesAsync(List<long[]> passes, long now, CancellationToken ct)
  {
    foreach (var pass in passes)
    {
      achievements[(int)pass[0]] = new AchievementState { Count = 1, CompletedMs = now, UpdatedMs = now };
      achievementDirty.Add((int)pass[0]);
    }
    var changes = new List<byte[]>();
    var expIds = passes.Where(p => p.Length >= 2).Select(p => (int)p[1]).ToHashSet();
    if (Store is { } store && CharacterId is { } character)
      foreach (var expId in expIds)
      {
        if (Achievements.Of(expId) is not { } entry) continue;
        var state = achievements.GetValueOrDefault(expId) ?? new AchievementState();
        if (state.RewardedStep >= 1) continue;
        var items = entry.StepRewards(1);
        if (items.Count == 0) continue;
        var rewards = items.Select(i => (i.ItemId, i.Count)).ToList();
        var ok = await Stored(async () => await store.ShopBuyAsync(character, [], rewards, [], ct) ? [[]] : []);
        if (ok.Count == 0) continue;
        state.RewardedStep = 1;
        achievements[expId] = state;
        achievementDirty.Add(expId);
        changes.AddRange(await InventoryChangesAsync(ct, IncreaseBattlePassReward, 0));
      }
    changes.Add(BattlePassInfoList(now));
    return changes;
  }

  (int MapId, Vector3d Position) TelpoAgitDestination()
  {
    if (guild?.AgitId is not { } agitId || GuildTables.Embedded.Agit(agitId) is not { } agit) return (0, Vector3d.Zero);
    var at = data.MapPoints(agit.MapId).Where(p => p.Key.Contains("start", StringComparison.OrdinalIgnoreCase)
        || p.Key.Contains("agit", StringComparison.OrdinalIgnoreCase) || p.Key.Contains("home", StringComparison.OrdinalIgnoreCase))
      .Select(p => (Vector3d?)new Vector3d(p.Value[0], p.Value[1], p.Value[2])).FirstOrDefault()
      ?? data.MapStart(agit.MapId)?.Position ?? GamePackets.SpawnPosition;
    return (agit.MapId, at);
  }
}