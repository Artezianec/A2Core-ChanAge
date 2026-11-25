import json
import subprocess
import sys
import unittest
from pathlib import Path

from tools.packet_schema import PacketSchema, WireError
from tools.bind_opcodes import solve_prefix, _try_varint, width_kind
from tools import usmap_codegen, packet_structs

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / "research/local"
LEDGER = LOCAL / "tooling/opcodes.json"


class PacketSchemaOpcodeMapTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.schema = PacketSchema()

    def test_game_anchors(self):
        anchors = {
            0x3600: "DevPacketData_GameServer_MapFrame_NT",
            0x3603: "DevPacketData_GameServer_Ping_RS",
            0xCB02: "DevPacketData_GameServer_Handshake_RS",
            0x5E58: "DevPacketData_GameServer_Handshake_RQ",
            0x3615: "DevPacketData_GameServer_Login_RS",
            0x3619: "DevPacketData_GameServer_CharCheckName_RQ",
            0x361a: "DevPacketData_GameServer_CharCheckName_RS",
            0x361b: "DevPacketData_GameServer_CharCreate_RQ",
            0x361c: "DevPacketData_GameServer_CharCreate_RS",
            0x3C4F: "DevPacketData_GameServer_SelectCharacter_RQ",
            0xA8F9: "DevPacketData_GameServer_SelectCharacter_RS",
            0x3621: "DevPacketData_GameServer_PrepareEnterMap_NT",
            0x4EF8: "DevPacketData_GameServer_LoadComplete_RS",
            0x3633: "DevPacketData_GameServer_MyCharAppear_NT",
            0x3649: "DevPacketData_GameServer_CharAllStats_RS",
        }
        for opcode, name in anchors.items():
            self.assertEqual(self.schema.struct_for_opcode("game", opcode), name)

    def test_legacy_opcodes_are_not_in_the_dispatch_map(self):
        # 2.0.12 moved these; the dispatch map must not fall back to the old
        # usmap registration order (docs/TODO_TASK.md, C12-001).
        for opcode in (0x3610, 0x361F, 0x3620, 0x3622, 0x3623):
            self.assertIsNone(self.schema.struct_for_opcode("game", opcode))
        self.assertEqual(self.schema.struct_for_opcode("game", 0x5E58),
                         "DevPacketData_GameServer_Handshake_RQ")

    def test_login_anchors(self):
        self.assertEqual(self.schema.struct_for_opcode("login", 0x3909),
                         "DevPacketData_LoginServer_ListServer_RS")

    def test_select_character_request_varint_field(self):
        fields = self.schema.fields("DevPacketData_GameServer_SelectCharacter_RQ")
        kinds = {name: kind for name, kind, _ in fields}
        self.assertEqual(kinds["_key"], "varint")
        self.assertEqual(kinds["_map_id"], "i32")


class BindOpcodeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.schema = PacketSchema()

    def test_stat_type_wire_widths(self):
        # Ground truth (0x3649): _stat_type is u16, _stat_value is i32, i.e.
        # 6-byte StatType elements. A single element in isolation is ambiguous
        # (u8+i32 also fits), so assert on a full CharAllStats_RS body and pick
        # the candidate that consumes every byte (exactly what learn() does).
        body = (b"\x00\x00\x00\x00" + b"\x02" +
                b"\x1f\x00" + (21).to_bytes(4, "little") +
                b"\x21\x00" + (17).to_bytes(4, "little") +
                (119).to_bytes(8, "little"))
        cands = solve_prefix(self.schema,
                             "DevPacketData_GameServer_CharAllStats_RS", body, 0)
        full = [c for c in cands if c[3] == len(body)]
        self.assertTrue(full)
        score, widths, parsed, end = full[0]
        self.assertIn(("DevPacketData_Common_StatType._stat_type", "i32", 2), widths)
        self.assertIn(("DevPacketData_Common_StatType._stat_value", "i32", 4), widths)

    def test_ledger_exists_and_solves_ping(self):
        self.assertTrue(LEDGER.exists(), "run tools/bind_opcodes.py first")
        ledger = json.loads(LEDGER.read_text())
        ping = ledger.get("13827")  # 0x3603
        self.assertIsNotNone(ping)
        self.assertIsNotNone(ping.get("widths"))
        self.assertEqual(ping["widths"].get(
            "DevPacketData_GameServer_Ping_RS._result"), "u16")

    def test_width_kind(self):
        self.assertEqual(width_kind("f32", 4), "f32")
        self.assertEqual(width_kind("i32", 2), "u16")
        self.assertEqual(width_kind("i32", 4), "i32")
        self.assertEqual(width_kind("i32", "varint"), "varint")


class UsmapCodegenTests(unittest.TestCase):
    def test_take_achievement_reward_batch_rs_consumes_body(self):
        body = bytes.fromhex(
            "00000000" "01" "00000000" "00" "00" "01"
            "66a3e111" "00" "01" "0c6df211" "02")
        parsed, consumed = packet_structs.parse_GameServerTakeAchievementRewardBatchRS(body)
        self.assertEqual(consumed, len(body))
        self.assertEqual(parsed["_result"], 0)
        self.assertEqual(parsed["_category"], 1)
        self.assertEqual(parsed["_group_id"], 0)
        self.assertFalse(parsed["_only_character_achievement"])
        take = parsed["_take_result"]
        self.assertEqual(len(take), 1)
        self.assertEqual(take[0]["_group_id"], 0x11E1A366)
        self.assertEqual(take[0]["_taken_achievement_steps"], {0x11F26D0C: 2})

    def test_generated_module_roundtrip_ping(self):
        values = {"_result": 0, "_client_sent_time": 0x1122334455667788,
                  "_server_game_clock": 1234567890123}
        body = packet_structs.build_GameServerPingRS(values)
        parsed, consumed = packet_structs.parse_GameServerPingRS(bytes(body))
        self.assertEqual(consumed, len(body))
        self.assertEqual(parsed["_client_sent_time"], 0x1122334455667788)
        self.assertEqual(parsed["_server_game_clock"], 1234567890123)

    def test_codegen_check(self):
        # usmap_codegen --check must not report drift.
        result = subprocess.run(
            [sys.executable, "tools/usmap_codegen.py", "--check"],
            cwd=str(ROOT), capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
