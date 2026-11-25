using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    readonly HashSet<int> playtestLoggedEntities = [];
    string? pendingSkillCastRefusalReason;

    void LogWorldEntities()
    {
        if (targets is null) return;
        foreach (var t in targets.Values)
            PlaytestDebug.EntityOnce(playtestLoggedEntities, t.Key, t.NpcId, t.Name);
    }

    void LogSkillCastFromReplies(byte[] plain, List<byte[]> replies)
    {
        if (ParseCast(plain) is not { } request) return;
        if (pendingSkillCastRefusalReason is { } loggedReason)
        {
            pendingSkillCastRefusalReason = null;
            return;
        }
        var outcome = CastOutcomeFromReplies(replies);
        var reason = outcome.Accepted == false ? "cast_rejected" : null;
        LogSkillCastOutcome(request.SkillId, request.TargetKey, outcome, reason: reason);
        LogCastChainWireTrace(request.SkillId, replies);
    }

    void LogCastChainWireTrace(int castSkillId, List<byte[]> replies)
    {
        foreach (var packet in replies)
        {
            if (packet.Length < 2) continue;
            var op = Opcode(packet);
            if (op is 0x3801)
            {
                PlaytestDebug.Action("chain_wire",
                    ("packet", "0x3801"),
                    ("castSkillId", castSkillId),
                    ("accepted", CastResultCode(packet) == 0),
                    ("hex", Convert.ToHexString(packet)));
            }
            else if (op is 0x3818 or 0x3819)
                EmitChainWireAction(ChainWireFields(op, packet, castSkillId));
            else if (op is 0x382a or 0x382c)
                PlaytestDebug.Action("chain_wire",
                    ("packet", op == 0x382a ? "0x382a" : "0x382c"),
                    ("castSkillId", castSkillId),
                    ("hex", Convert.ToHexString(packet, 0, Math.Min(packet.Length, 64))));
        }
    }

    static Dictionary<string, object?> ChainWireFields(ushort op, byte[] packet, int castSkillId)
    {
        var fields = new Dictionary<string, object?>
        {
            ["packet"] = $"0x{op:x4}",
            ["castSkillId"] = castSkillId,
            ["hex"] = Convert.ToHexString(packet),
        };
        if (op == 0x3818 && packet.Length >= 7)
        {
            fields["prevSkillId"] = BitConverter.ToInt32(packet, 2);
            var count = packet[6];
            if (count > 0 && packet.Length >= 7 + count * 4)
                fields["chainSkills"] = string.Join(',', Enumerable.Range(0, count).Select(i => BitConverter.ToInt32(packet, 7 + i * 4)));
        }
        else if (op == 0x3819 && packet.Length >= 3)
        {
            var count = packet[2];
            if (count > 0 && packet.Length >= 3 + count * 4)
                fields["chainSkills"] = string.Join(',', Enumerable.Range(0, count).Select(i => BitConverter.ToInt32(packet, 3 + i * 4)));
        }
        return fields;
    }

    void LogChainWirePacket(string kind, byte[] packet, int prevSkillId, IReadOnlyList<int> chainSkills) =>
        PlaytestDebug.Action("chain_wire",
            ("kind", kind),
            ("packet", packet.Length >= 2 ? $"0x{Opcode(packet):x4}" : ""),
            ("prevSkillId", prevSkillId),
            ("chainSkills", chainSkills.Count == 0 ? "" : string.Join(',', chainSkills)),
            ("hex", Convert.ToHexString(packet)));

    static void EmitChainWireAction(Dictionary<string, object?> fields)
    {
        if (PlaytestDebug.Sink is not { Actions: true }) return;
        PlaytestDebug.Sink.Action("chain_wire", fields);
    }

    void LogBasicAtkCast(int skillId, int targetKey, List<byte[]> replies)
    {
        if (pendingSkillCastRefusalReason is not null)
        {
            pendingSkillCastRefusalReason = null;
            return;
        }
        LogSkillCastOutcome(skillId, targetKey, BasicAtkOutcomeFromReplies(replies), wire: "basic_atk");
        LogCastChainWireTrace(skillId, replies);
    }

    void LogQaCastRefusalDetail(int targetKey, string reason, string? skillLabel = null)
    {
        var detail = reason;
        if (targets?.TryGetValue(targetKey, out var npc) == true && position is { } caster)
        {
            var horiz = HorizontalDistance(caster, npc.Position);
            var dz = caster.Z - npc.Position.Z;
            detail = $"{reason} bot=({caster.X:F0},{caster.Y:F0},{caster.Z:F0}) tgt=({npc.Position.X:F0},{npc.Position.Y:F0},{npc.Position.Z:F0}) dist={(int)horiz} dz={(int)dz}";
        }
        var skillPart = skillLabel is { Length: > 0 } ? $"skill={skillLabel} " : "";
        Note($"qa_cast_refused: {skillPart}key={targetKey} {detail}");
        NoteQaCastRefusal(targetKey, detail);
    }

    void LogSkillCastOutcome(int skillId, int targetKey, (bool? Accepted, bool ResendAck) outcome, string wire = "skill_start", string? reason = null)
    {
        var skillName = data.Skill(skillId)?.Name ?? skillId.ToString();
        if (outcome.ResendAck)
        {
            PlaytestDebug.Action("skill_cast",
                ("wire", wire),
                ("skillId", skillId),
                ("skill", skillName),
                ("targetKey", targetKey),
                ("accepted", true),
                ("resendAck", true));
            return;
        }
        var accepted = outcome.Accepted ?? false;
        if (!accepted)
        {
        PlaytestDebug.Action("skill_cast",
            ("wire", wire),
            ("skillId", skillId),
            ("skill", skillName),
            ("targetKey", targetKey),
            ("accepted", false),
            ("reason", reason ?? "cast_rejected"));
            if (QaBot)
                LogQaCastRefusalDetail(targetKey, reason ?? "cast_rejected", skillName);
            return;
        }
        PlaytestDebug.Action("skill_cast",
            ("wire", wire),
            ("skillId", skillId),
            ("skill", skillName),
            ("targetKey", targetKey),
            ("accepted", true));
        if (QaBot) NoteQaCastAccepted(targetKey);
    }

    string CastTargetRefusalReason(SkillDefinition definition, int skillId, CastRequest request)
    {
        if (DestructibleEnvObjAt(request.TargetKey) is not null) return "out_of_range";
        if (request.TargetKey != playerKey && request.TargetKey < SharedWorld.FirstPlayerKey)
        {
            if (targets?.TryGetValue(request.TargetKey, out var npc) != true || npc is null) return "no_target";
            if (npc.Dead) return "target_dead";
            if (npc.Immune) return "target_invulnerable";
            if (!npc.Hostile) return "target_not_hostile";
        }
        var range = data.SkillUseRangeCm(skillId);
        if (range > 0)
        {
            var caster = position ?? request.Location;
            if (request.TargetKey == playerKey) return "out_of_range";
            if (request.TargetKey >= SharedWorld.FirstPlayerKey)
            {
                if (worldPlayer?.Map?.Players.GetValueOrDefault(request.TargetKey) is not { } victim) return "no_target";
                if (HorizontalDistance(caster, victim.Position) > range + CastRangeToleranceCm) return "out_of_range";
                if (Math.Abs(caster.Z - victim.Position.Z) > CastHeightToleranceCm) return "height";
                return "out_of_range";
            }
            if (targets?.TryGetValue(request.TargetKey, out var npc) != true || npc is null) return "no_target";
            var max = range + CastRangeToleranceCm + npc.Radius;
            if (HorizontalDistance(caster, npc.Position) > max) return "out_of_range";
            if (Math.Abs(caster.Z - npc.Position.Z) > CastHeightToleranceCm) return "height";
        }
        return "out_of_range";
    }

    (bool? Accepted, bool ResendAck) CastOutcomeFromReplies(List<byte[]> replies)
    {
        bool? accepted = null;
        var hasStart = false;
        foreach (var packet in replies)
        {
            if (packet.Length >= 2 && Opcode(packet) == 0x3802) hasStart = true;
            if (packet.Length < 6 || Opcode(packet) != 0x3801) continue;
            try
            {
                var reader = new PacketReader(packet) { Offset = 2 };
                if (packet.Length != 9 && packet.Length > 3 && (packet[2] & 3) != 0) reader.U8();
                accepted = reader.U16() == 0;
            }
            catch (ProtocolException) { break; }
        }
        return (accepted, accepted == true && !hasStart);
    }

    (bool? Accepted, bool ResendAck) BasicAtkOutcomeFromReplies(List<byte[]> replies)
    {
        bool? accepted = null;
        var hasStart = replies.Any(p => p.Length >= 2 && Opcode(p) == 0x3802);
        foreach (var packet in replies)
        {
            if (packet.Length < 4 || Opcode(packet) != BasicAtkRs) continue;
            accepted = BitConverter.ToUInt16(packet, 2) == 0;
            break;
        }
        return (accepted, accepted == true && !hasStart);
    }

    string DetermineCastRefusalReason(CastRequest request, int skillId, SkillDefinition? definition, bool meetsCastRules,
        bool chainAllowed, bool cooldownReady, bool castGapOk, SkillCost cost, IReadOnlyList<string> weapons, long now)
    {
        if (definition is null) return "unknown_skill";
        if (resources is null) return "no_resources";
        if (dead) return "dead";
        if (!chainAllowed) return "chain_window_closed";
        if (CombatCastBlocked(skillId)) return "combat_blocked";
        if (resources.Mp < cost.Mp) return "need_mp";
        if (resources.Sp < cost.Sp) return "need_sp";
        if (resources.Hp <= cost.Hp) return "need_hp";
        if (resources.ApMax > 0 && cost.Ap > 0 && resources.Ap < cost.Ap) return "need_ap";
        if (!learned.Contains(skillId) && !IsDodgeId(skillId) && definition.ChainPrev == 0 && CarriedSkill != skillId)
            return "not_learned";
        if (weapons.Count > 0 && !weapons.Contains(data.WeaponType(equippedWeapon) ?? "")) return "wrong_weapon";
        if (!SpiritConditionMet(definition, skillId)) return "spirit_required";
        if (!PeaceConditionMet(definition)) return "peace_required";
        if (!ResourceConditionMet(definition)) return "resource_threshold";
        if (!SkillCondMet(definition, now, request.TargetKey)) return "skill_cond_required";
        if (!cooldownReady) return "cooldown";
        if (!castGapOk) return "cast_gap";
        if (!CastTargetReachValid(definition, skillId, request)) return CastTargetRefusalReason(definition, skillId, request);
        if (!SpecializedCastAllowed(skillId)) return "specialization";
        if (!UseConditionMet(definition, now, request.TargetKey, skillId)) return "use_condition";
        return meetsCastRules ? "cast_gap" : "cast_rules";
    }

    void LogSkillCastRefusal(CastRequest request, int skillId, SkillDefinition? definition, bool meetsCastRules,
        bool chainAllowed, bool cooldownReady, bool castGapOk, SkillCost cost, IReadOnlyList<string> weapons, long now)
    {
        var reason = DetermineCastRefusalReason(request, skillId, definition, meetsCastRules, chainAllowed, cooldownReady, castGapOk, cost, weapons, now);
        pendingSkillCastRefusalReason = reason;
        if (QaBot) LogQaCastRefusalDetail(request.TargetKey, reason, definition?.Name ?? skillId.ToString());
        PlaytestDebug.Action("skill_cast",
            ("skillId", skillId),
            ("skill", definition?.Name ?? skillId.ToString()),
            ("targetKey", request.TargetKey),
            ("accepted", false),
            ("reason", reason));
    }

    void LogCombatHit(int skillId, int targetKey, int amount, bool kill)
    {
        PlaytestDebug.Action(kill ? "kill" : "damage",
            ("skillId", skillId),
            ("targetKey", targetKey),
            ("amount", amount));
    }

    void LogTeleport(int mapId, Vector3d at, string source)
    {
        PlaytestDebug.Action("teleport",
            ("mapId", mapId),
            ("x", at.X),
            ("y", at.Y),
            ("z", at.Z),
            ("source", source));
    }

    void LogQuestEvent(string kind, string code, int step, int status, int[]? goals = null)
    {
        PlaytestDebug.Action(kind,
            ("quest", code),
            ("step", step),
            ("status", status),
            ("goals", goals is null ? "" : string.Join(',', goals)));
    }

    void LogNpcTalk(int npcKey, int npcId, string? name, string context)
    {
        PlaytestDebug.Action("npc_talk",
            ("npcKey", npcKey),
            ("npcId", npcId),
            ("name", name ?? ""),
            ("context", context));
    }

    void LogNpcTalkRefused(int npcKey, int npcId, string? name, string reason)
    {
        PlaytestDebug.Action("npc_talk_refused",
            ("npcKey", npcKey),
            ("npcId", npcId),
            ("name", name ?? ""),
            ("reason", reason));
        Note($"Npc talk refused: {name} ({npcId}) key={npcKey}: {reason}");
    }

    void LogMapEventBeat(string text) => PlaytestDebug.Action("map_event", ("text", text));

    void LogDeath(int killerKey) =>
        PlaytestDebug.Action("death", ("characterKey", playerKey), ("killerKey", killerKey));

    void LogResurrect(string source) =>
        PlaytestDebug.Action("resurrect", ("characterKey", playerKey), ("source", source));

    void LogLevelUp(int from, int to) =>
        PlaytestDebug.Action("level_up", ("from", from), ("to", to), ("characterKey", playerKey));

    void LogSkillPoints(int skillId, int level, string context) =>
        PlaytestDebug.Action("skill_point", ("skillId", skillId), ("level", level), ("context", context));

    void LogContainerSlotExtend(byte container, int costIdx, int result, int extensionCount) =>
        PlaytestDebug.Action("container_slot_extend",
            ("container", container), ("costIdx", costIdx), ("result", result), ("extensionCount", extensionCount));

    void LogContainerSlotExtend(byte container, int costIdx, int result, int extensionCount, string reason) =>
        PlaytestDebug.Action("container_slot_extend",
            ("container", container), ("costIdx", costIdx), ("result", result), ("extensionCount", extensionCount), ("reason", reason));

    readonly HashSet<(ushort Opcode, int Length)> loggedUnhandledPackets = [];

    /// <summary>Logs an unhandled client packet once per (opcode, length): opcode, length and the first 48 bytes in hex.</summary>
    void LogUnhandledPacket(ushort opcode, byte[] plain)
    {
        if (!loggedUnhandledPackets.Add((opcode, plain.Length))) return;
        PlaytestDebug.Action("unhandled_packet",
            ("opcode", $"0x{opcode:x4}"), ("length", plain.Length),
            ("hex", Convert.ToHexString(plain, 0, Math.Min(plain.Length, 48))));
    }

    void LogItemUse(int templateId, string? name) =>
        PlaytestDebug.Action("item_use", ("templateId", templateId), ("name", name ?? ""));

    void LogItemUseRefused(int templateId, string? effect, string reason) =>
        PlaytestDebug.Action("item_use_refused", ("templateId", templateId), ("effect", effect ?? ""), ("reason", reason));

    void LogQuickSlotAutoUse(int templateId, int slot, string source) =>
        PlaytestDebug.Action("quickslot_auto_use", ("templateId", templateId), ("slot", slot), ("source", source));

    void LogItemGain(int templateId, int count, string source) =>
        PlaytestDebug.Action("item_gain", ("templateId", templateId), ("count", count), ("source", source));

    void LogAchievementReward(string source, int step, IReadOnlyList<(int ItemId, long Count)> items, int? achievementId = null, int? groupId = null) =>
        PlaytestDebug.Action("achievement_reward",
            ("source", source),
            ("achievementId", achievementId),
            ("groupId", groupId),
            ("step", step),
            ("grants", items.Count == 0 ? "" : string.Join(';', items.Select(i => $"{i.ItemId}x{i.Count}"))));

    void LogAttendanceCollect(bool checkedIn, int rewardsClaimed, int rewardsFailed, int result) =>
        PlaytestDebug.Action("attendance_collect",
            ("checkedIn", checkedIn),
            ("rewardsClaimed", rewardsClaimed),
            ("rewardsFailed", rewardsFailed),
            ("result", result));

    void LogMountRefusal(string reason, int requestedRide, int resolvedRide = 0, int soulPoints = 0, bool contentsUnlocked = false) =>
        PlaytestDebug.Action("mount_refused",
            ("reason", reason),
            ("requested", requestedRide),
            ("resolved", resolvedRide),
            ("soulPoints", soulPoints),
            ("rideContentsUnlock", contentsUnlocked));

    void LogCharacterCreate(CreateRequestData request, bool accepted, int? accountRace = null, int? race = null, int resultCode = 0)
    {
        var profile = data.Profile(request.CreationId, request.Gender, accountRace);
        race ??= profile?.Race;
        var kind = accepted ? "character_create" : "character_create_refused";
        PlaytestDebug.Action(kind,
            ("creationId", request.CreationId),
            ("classPcId", profile?.PcId),
            ("race", race),
            ("slot", request.SlotNo),
            ("nameLength", request.Name.Length),
            ("gender", request.Gender),
            ("result", resultCode));
    }
}
