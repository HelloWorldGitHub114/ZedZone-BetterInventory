using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace ZedZone.BetterInventory
{
    /// <summary>
    /// 更好的物品栏（Better Inventory）插件入口：提供物品收藏保护、容器筛选与三档拾取优先级路由、以及框选批量拖进背包图标。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "zedzone.betterinventory";
        public const string PluginName = "Better Inventory";
        public const string PluginVersion = "1.1.2";

        internal static ManualLogSource Logs;

        // 通用设置
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ShowNotifications;
        internal static ConfigEntry<bool> VerboseLog;

        // 物品收藏设置
        internal static ConfigEntry<bool> EnableFavorites;
        internal static ConfigEntry<KeyCode> FavoriteModifierKey;
        internal static ConfigEntry<bool> ProtectFromQuickStack;
        internal static ConfigEntry<bool> ProtectFromDepositAll;
        internal static ConfigEntry<bool> ProtectFromTakeAll;
        internal static ConfigEntry<bool> ProtectFromBulkGrind;
        internal static ConfigEntry<bool> ProtectFromDrop;
        internal static ConfigEntry<bool> ProtectFromDisassemble;
        internal static ConfigEntry<bool> ProtectFromSell;
        internal static ConfigEntry<bool> ProtectFromQuickTransfer;

        // 拾取优先级与容器过滤设置
        internal static ConfigEntry<bool> EnablePickupRouting;
        internal static ConfigEntry<bool> RouteIntoNestedBags;
        internal static ConfigEntry<bool> RouteDisassembleProducts;
        internal static ConfigEntry<bool> PreferExistingStack;
        internal static ConfigEntry<bool> ApplyFilterOnQuickStack;
        internal static ConfigEntry<bool> ApplyFilterOnDepositAll;
        internal static ConfigEntry<bool> StrictFilterOnManualDrop;
        internal static ConfigEntry<bool> ShowContainerHeaderButton;

        // 框选批量拖进背包设置
        internal static ConfigEntry<bool> EnableMultiDragIntoBag;

        private static readonly HashSet<string> LoggedErrorTags = new HashSet<string>(StringComparer.Ordinal);

        public override void Load()
        {
            Logs = Log;

            Enabled = Config.Bind("General", "Enabled", true,
                "启用更好的物品栏（Better Inventory）模组。");
            ShowNotifications = Config.Bind("General", "ShowNotifications", true,
                "收藏拦截或规则变更时在右下角显示系统提示。");
            VerboseLog = Config.Bind("General", "VerboseLog", false,
                "输出详细调试日志到 BepInEx\\LogOutput.log。");

            EnableFavorites = Config.Bind("Favorite", "EnableFavorites", true,
                "启用物品收藏（锁定）功能与金色星标显示。");
            FavoriteModifierKey = Config.Bind("Favorite", "FavoriteModifierKey", KeyCode.LeftAlt,
                "配合左键快速切换收藏状态的修饰键（默认 LeftAlt；设为 None 则仅通过右键菜单切换）。");
            ProtectFromQuickStack = Config.Bind("Favorite", "ProtectFromQuickStack", true,
                "收藏物品免受一键堆叠与存入同类影响（含联机客户端一键堆叠）。");
            ProtectFromDepositAll = Config.Bind("Favorite", "ProtectFromDepositAll", true,
                "收藏物品免受容器“全部存入”影响。");
            ProtectFromTakeAll = Config.Bind("Favorite", "ProtectFromTakeAll", true,
                "收藏物品免受容器与随身套包“全部拾取 [F]”移出原位（不影响地面“附近的物品”全部拾取）。");
            ProtectFromBulkGrind = Config.Bind("Favorite", "ProtectFromBulkGrind", true,
                "收藏物品免受粉碎机批量粉碎影响。");
            ProtectFromDrop = Config.Bind("Favorite", "ProtectFromDrop", true,
                "阻止丢弃已收藏物品（包括右键丢弃与拖出丢弃）。");
            ProtectFromDisassemble = Config.Bind("Favorite", "ProtectFromDisassemble", true,
                "阻止拆解已收藏物品（包括右键拆解与附近批量拆解）。");
            ProtectFromSell = Config.Bind("Favorite", "ProtectFromSell", true,
                "在商人出售列表、批量出售扣减与交易拖拽中隐藏并保护已收藏物品。");
            ProtectFromQuickTransfer = Config.Bind("Favorite", "ProtectFromQuickTransfer", false,
                "阻止 Shift/Ctrl 快速转移已收藏物品到打开的容器（默认关闭以便自由整理）。");

            EnablePickupRouting = Config.Bind("FilterAndPriority", "EnablePickupRouting", true,
                "启用按容器优先级与过滤器自动分配拾取物品。");
            RouteIntoNestedBags = Config.Bind("FilterAndPriority", "RouteIntoNestedBags", true,
                "拾取物品时也将身上套包内的子背包纳入筛选与优先级路由。");
            RouteDisassembleProducts = Config.Bind("FilterAndPriority", "RouteDisassembleProducts", true,
                "在主背包或随身子包内拆解物品时，产生的材料也自动按容器筛选与优先级分拣进对应背包或口袋。");
            PreferExistingStack = Config.Bind("FilterAndPriority", "PreferExistingStack", true,
                "同一优先级与匹配级别下，优先堆叠到已有同款物品的容器。");
            ApplyFilterOnQuickStack = Config.Bind("FilterAndPriority", "ApplyFilterOnQuickStack", true,
                "一键堆叠/存入同类时同时遵守目标存储箱设置的容器过滤规则。");
            ApplyFilterOnDepositAll = Config.Bind("FilterAndPriority", "ApplyFilterOnDepositAll", true,
                "向目标容器“全部存入”时遵守目标容器的过滤规则。");
            StrictFilterOnManualDrop = Config.Bind("FilterAndPriority", "StrictFilterOnManualDrop", false,
                "手动拖拽物品放入容器时也强制校验过滤规则（按住 LeftShift 可临时绕过）。");
            ShowContainerHeaderButton = Config.Bind("FilterAndPriority", "ShowContainerHeaderButton", true,
                "在背包与容器窗口标题栏显示快捷“筛选/优先级”按钮。");

            EnableMultiDragIntoBag = Config.Bind("MultiDrag", "EnableMultiDragIntoBag", true,
                "允许将框选的多个物品直接拖放到未打开的背包图标上批量收入包内（装不下的物品安全保留原位）。");

            if (!Enabled.Value)
            {
                Log.LogInfo($"{PluginName} is disabled in config.");
                return;
            }

            ContainerRuleStore.LoadSidecar();

            var harmony = new Harmony(PluginGuid);
            int applied = 0;
            int skipped = 0;

            Type[] patchTypes =
            {
                // 物品收藏 UI 与防误操作
                typeof(Patch_BasicItemUI_RefreshItemUI_Fav),
                typeof(Patch_BasicItemUI_ObjectPoolInit_Fav),
                typeof(Patch_BasicItemUI_OnDisable_Fav),
                typeof(Patch_BasicItemUI_OnPointerClick_Fav),
                typeof(Patch_BasicItemUI_ShowItemOptions_Context),
                typeof(Patch_ButtonsPanel_ShowButtons_Inject),
                typeof(Patch_InventoryData_CanMergeStack_Fav),
                typeof(Patch_InventoryData_CanGroupFood_Fav),
                typeof(Patch_NearbyStorageLocator_QuickStack_Guard),
                typeof(Patch_NetContainerSync_ClientRequestQuickStack_Guard),
                typeof(Patch_InventoryPanel_DepositAllItemsTo_Guard),
                typeof(Patch_InventoryPanel_CollectAllItemsToPlayer_Fav),
                typeof(Patch_Grinder_IsBulkGrindCandidate_Fav),
                typeof(Patch_BasicItemUI_DropItem_Fav),
                typeof(Patch_BasicItemUI_DropDraggingGroup_Fav),
                typeof(Patch_BasicItemUI_DropMulti_Fav),
                typeof(Patch_BasicItemUI_DropOn_MultiOnPanel_Fav),
                typeof(Patch_BasicItemUI_Disassemble_Fav),
                typeof(Patch_NearbyItemInventoryPanel_DisassembleAll_Fav),
                typeof(Patch_BasicItemUI_DropOn_Trade_Fav),
                typeof(Patch_InventoryData_RestoreOrDrop_Guard),
                typeof(Patch_BasicItemUI_QuickTransfer_Fav),
                typeof(Patch_NPCInteractController_GetAllCharacterItems_Fav),
                typeof(Patch_NPCInteractController_GetTotalItemNumber_Fav),
                typeof(Patch_NPCInteractController_RemoveStackable_Fav),
                typeof(Patch_RangedWeapon_SwapMagazine_Fav),
                typeof(Patch_ItemData_StoreMagazinePaintFrom_Fav),
                typeof(Patch_ItemData_RestoreMagazinePaintTo_Fav),
                typeof(Patch_RangedWeapon_SwapBattery_Fav),
                typeof(Patch_RangedWeapon_StoreBatteryDurability_Fav),
                typeof(Patch_RangedWeapon_RestoreBatteryDurability_Fav),
                typeof(Patch_RangedWeapon_ChangeGunPart_Fav),
                typeof(Patch_RangedWeapon_ReturnEquippedGunPart_Fav),
                typeof(Patch_BasicItemUI_InstallMeleeAttachment_Fav),
                typeof(Patch_BasicItemUI_RemoveMeleeAttachment_Fav),
                typeof(Patch_GameController_TryAddItemToCharacter_RestoreSlotFav),
                typeof(Patch_BatteryBox_TryAddBattery_Fav),
                typeof(Patch_BatteryBox_RemoveBattery_Fav),

                // 拾取优先级与容器过滤
                typeof(Patch_BasicCharacterController_AddItemToCharacter_Routing),
                typeof(Patch_InventoryPanel_CollectAllItemsToPlayer_Scope),
                typeof(Patch_BasicItemUI_Disassemble_Internal_Scope),
                typeof(Patch_InventoryData_TryAddItem_DisassembleRouting),
                typeof(Patch_InventoryData_TryAddItem_QuickStackFilter),
                typeof(Patch_InventoryData_PassesFeatureLimit_Strict),

                // 框选多物品直接拖进闭合背包图标
                typeof(Patch_BasicItemUI_DropOn_Multi_IntoBag),

                // 容器规则入口与弹窗生命周期
                typeof(Patch_PlayerInventoryPanel_LayoutChrome_UI),
                typeof(Patch_PlayerInventoryPanel_Refresh_UI),
                typeof(Patch_LootInventoryPanel_Refresh_UI),
                typeof(Patch_LootInventoryPanel_SetTitle_UI),
                typeof(Patch_LootInventoryPanel_LayoutBottomButtons_UI),
                typeof(Patch_CommonInventoryPanel_Refresh_UI),
                typeof(Patch_CommonInventoryPanel_RefreshTitle_UI),
                typeof(Patch_InventoryButtonRow_Layout_UI),
                typeof(Patch_PlayerInventoryPanel_Update_PopupEsc),
                typeof(Patch_PlayerPanel_ClosePanel_ClosePopup),
            };

            foreach (var patchType in patchTypes)
            {
                if (PatchOne(harmony, patchType))
                    applied++;
                else
                    skipped++;
            }

            int hooked = harmony.GetPatchedMethods().Count();
            Log.LogInfo($"{PluginName} {PluginVersion}: {applied} patch group(s) applied, {skipped} skipped, {hooked} method(s) hooked.");
        }

        private static bool PatchOne(Harmony harmony, Type patchType)
        {
            try
            {
                harmony.PatchAll(patchType);
                return true;
            }
            catch (Exception ex)
            {
                Logs?.LogWarning($"Patch {patchType.Name} skipped: {ex.Message}");
                return false;
            }
        }

        internal static bool IsChinese()
        {
            try
            {
                var settings = GameController.instance?.gameSettings;
                if (settings != null)
                    return settings.gameLanguage == GameLanguage.SimplifiedChinese;
            }
            catch
            {
                // 游戏尚未完成初始化时回退到系统语言
            }

            return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }

        internal static string L(string zh, string en) => IsChinese() ? zh : en;

        internal static void Notify(string zh, string en, bool playSound = false)
        {
            if (ShowNotifications == null || !ShowNotifications.Value) return;
            try
            {
                if (SystemNotificationPanel.instance == null) return;
                SystemNotificationPanel.ShowNotificationSafe(L(zh, en), playSound);
            }
            catch (Exception ex)
            {
                LogOnce("notify", ex);
            }
        }

        internal static void LogVerbose(string message)
        {
            if (VerboseLog != null && VerboseLog.Value)
                Logs?.LogInfo("[Verbose] " + message);
        }

        internal static void LogOnce(string tag, Exception ex)
        {
            if (!LoggedErrorTags.Add(tag)) return;
            Logs?.LogWarning($"[{tag}] {ex.GetType().Name}: {ex.Message}");
        }
    }
}
