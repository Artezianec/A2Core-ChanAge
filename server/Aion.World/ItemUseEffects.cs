namespace Aion.World;

/// <summary>Item.dat UseEffect coverage for 0x5612 / 0x5614 and boxes.json handled_use_effects (tools/box_data.py).</summary>
public static class ItemUseEffects
{
    /// <summary>Use through the item-use opcode; the client uses a dedicated UI or packet instead.</summary>
    public static readonly HashSet<string> NotDirectUse =
    [
        "Customize", "Succession", "EnchantScroll", "SoulAdd", "ImprintMagicStone", "SealStone",
    ];

    /// <summary>Exported in boxes.json but intentionally not implemented on the item-use path yet.</summary>
    public static readonly HashSet<string> KnownUnimplemented =
    [
        "AddMultiContentsTicket", "AlchemyMastery", "BlacksmithingMastery", "CharacterSlotUnlock", "CookingMastery",
        "EnchantScroll", "ExtendCharacterStorageSlot", "ExtendInventorySlot", "ExtendStorageSlot", "ExtractForceMastery",
        "Fix_StigmaSkillPoint", "Fix_VehicleLevel", "GetArcana", "GetGameBindingCash", "GetGameCash", "GetGamePendingCash", "GetSkillPoint",
        "JewelcraftingMastery", "MonolithAllClear", "OccupationTerritoryAllClear", "RegisterConversionRecipe",
        "SealAllClear", "SetupKisk", "ShowNote", "SkinExtraction", "SoulBind", "SoulBindReroll", "Subscribe", "Surpass",
        "TailoringMastery",
    ];

    /// <summary>UseEffect names with an item-use handler (includes duty scrolls and UI-only refusals).</summary>
    public static readonly HashSet<string> Handled =
    [
        "RewardBox", "GetVehicle", "GetVehicleSoul", "GetVehicleSoulCrystal", "Fix_VehicleCreatureLevel", "Skill",
        "Telpo_Home", "Telpo_Agit", "GetKina", "GetNonBindingKina", "GetTitle", "GetSkin", "GetSkinSet", "GetSocialAction",
        "GetDaevanionPoint", "Fix_DaevanionPoint", "GetGodstone", "GetStigmaShard", "GetStigmaShardSuperior", "GetQuest",
        "GetDutyQuest", "GetWing", "AscensionGrade", "BattlePassExp", "BattlePassUnlock", "AddContentsTicket", "GetDeco",
        .. NotDirectUse,
    ];

    public static bool IsNotDirectUse(string effect) => NotDirectUse.Contains(effect);
    public static bool IsKnownUnimplemented(string effect) => KnownUnimplemented.Contains(effect);
    public static bool IsHandled(string effect) => Handled.Contains(effect) && !IsKnownUnimplemented(effect);
    public static bool ReportsAsImplemented(string effect) => IsHandled(effect) || IsNotDirectUse(effect);
}