using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ZedZone.Net;

namespace ZedZone.BetterInventory
{
    /// <summary>
    /// 物品收藏保护共享工具：通过“作用域临时隐藏 + Finalizer 必然归位”隔离批量操作，
    /// 无需重写原版容器转移或联机协议逻辑，既保留原格位坐标又兼容第三方模组。
    /// </summary>
    internal static class FavoriteGuardHelper
    {
        internal readonly struct StashedItem
        {
            internal readonly InventoryData Inventory;
            internal readonly ItemData Item;

            internal StashedItem(InventoryData inventory, ItemData item)
            {
                Inventory = inventory;
                Item = item;
            }
        }

        [ThreadStatic]
        internal static BasicItemUI ActiveContextMenuUI;

        // 拆下/替换枪械配件或近战配件时，由原版通过 TryAddItemToCharacter 归还新 ItemData；
        // 用线程局部深度标记将待恢复的收藏状态精准传递到紧接着的归还调用中。
        [ThreadStatic]
        internal static bool PendingRestoreFavoriteOnAdd;

        [ThreadStatic]
        internal static int ChangeGunPartScopeDepth;

        internal static bool FavoritesEnabled =>
            Plugin.EnableFavorites != null && Plugin.EnableFavorites.Value;

        internal static List<StashedItem> StashMatching(
            InventoryData inv,
            Func<ItemData, bool> predicate,
            List<StashedItem> sink = null)
        {
            if (inv == null || inv.itemList == null || predicate == null) return sink;
            var list = inv.itemList;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                var item = list[i];
                if (item == null || !predicate(item)) continue;

                if (sink == null) sink = new List<StashedItem>();
                sink.Add(new StashedItem(inv, item));
                list.RemoveAt(i);
            }

            return sink;
        }

        internal static List<StashedItem> StashFavoritesInInventories(
            IList<InventoryData> inventories,
            int filterItemId = -1,
            bool allowCashInProtectedContainer = false)
        {
            if (inventories == null || inventories.Count == 0) return null;
            List<StashedItem> stashed = null;
            var seenInvs = new HashSet<IntPtr>();
            int cashId = allowCashInProtectedContainer ? GetCashItemIdSafe() : -1;

            for (int i = 0; i < inventories.Count; i++)
            {
                var inv = inventories[i];
                IntPtr ptr = InventoryHelper.Ptr(inv);
                if (ptr == IntPtr.Zero || !seenInvs.Add(ptr)) continue;

                bool invProtected = ContainerRuleStore.IsContainerProtected(inv);
                stashed = StashMatching(
                    inv,
                    it =>
                    {
                        if (filterItemId >= 0 && it.itemId != filterItemId) return false;
                        if (ItemFavoriteManager.IsFavorite(it)) return true;
                        if (!invProtected) return false;
                        if (allowCashInProtectedContainer && cashId >= 0 && it.itemId == cashId)
                            return false;
                        return true;
                    },
                    stashed);
            }

            return stashed;
        }

        internal static bool IsProtectedItem(InventoryData inv, ItemData item)
        {
            return ItemFavoriteManager.IsFavorite(item) || ContainerRuleStore.IsContainerProtected(inv);
        }

        internal static bool IsProtectedUI(BasicItemUI ui, out bool fromContainerOnly)
        {
            fromContainerOnly = false;
            if (ui == null) return false;

            if (ItemFavoriteManager.IsUIFavorited(ui) || ItemFavoriteManager.HasAnyFavoritedMultiDragMember(ui))
                return true;

            if (ContainerRuleStore.IsUIInProtectedContainer(ui))
            {
                fromContainerOnly = true;
                return true;
            }

            return false;
        }

        internal static bool IsInProtectedCarriedContainer(ItemData item)
        {
            if (item == null) return false;
            var cd = InventoryHelper.LocalPlayer?.characterData;
            if (cd == null) return false;

            IntPtr targetPtr = InventoryHelper.Ptr(item);
            if (targetPtr == IntPtr.Zero) return false;

            var allInvs = InventoryHelper.CollectPlayerCarriedInventories(cd, includeNested: true);
            for (int i = 0; i < allInvs.Count; i++)
            {
                var inv = allInvs[i];
                if (!ContainerRuleStore.IsContainerProtected(inv)) continue;
                var list = inv.itemList;
                if (list != null && ContainsByPtr(list, item))
                    return true;
            }
            return false;
        }

        internal static HashSet<IntPtr> CollectProtectedCarriedItemPtrs(
            CharacterData cd,
            bool excludeNonFavoriteCash = false)
        {
            if (cd == null) return null;
            var allInvs = InventoryHelper.CollectPlayerCarriedInventories(cd, includeNested: true);
            if (allInvs.Count == 0) return null;

            HashSet<IntPtr> set = null;
            int cashId = excludeNonFavoriteCash ? GetCashItemIdSafe() : -1;

            for (int i = 0; i < allInvs.Count; i++)
            {
                var inv = allInvs[i];
                if (!ContainerRuleStore.IsContainerProtected(inv)) continue;
                var list = inv.itemList;
                if (list == null || list.Count == 0) continue;

                for (int j = 0; j < list.Count; j++)
                {
                    var it = list[j];
                    if (it == null) continue;
                    if (excludeNonFavoriteCash && cashId >= 0 && it.itemId == cashId && !ItemFavoriteManager.IsFavorite(it))
                        continue;
                    IntPtr ptr = InventoryHelper.Ptr(it);
                    if (ptr != IntPtr.Zero)
                    {
                        set ??= new HashSet<IntPtr>();
                        set.Add(ptr);
                    }
                }
            }
            return set;
        }

        private static int GetCashItemIdSafe()
        {
            try
            {
                return CharacterData.cashItemId;
            }
            catch
            {
                return -1;
            }
        }

        internal static void RestoreStashed(List<StashedItem> stashed)
        {
            if (stashed == null || stashed.Count == 0) return;
            var dirtyInvs = new HashSet<IntPtr>();
            var dirtyList = new List<InventoryData>();

            // 逆序归位，尽量还原原先列表顺序
            for (int i = stashed.Count - 1; i >= 0; i--)
            {
                var entry = stashed[i];
                var inv = entry.Inventory;
                var item = entry.Item;
                if (inv == null || inv.itemList == null || item == null) continue;

                if (!ContainsByPtr(inv.itemList, item))
                    inv.itemList.Add(item);

                IntPtr ptr = InventoryHelper.Ptr(inv);
                if (ptr != IntPtr.Zero && dirtyInvs.Add(ptr))
                    dirtyList.Add(inv);
            }

            for (int i = 0; i < dirtyList.Count; i++)
            {
                try
                {
                    dirtyList[i].RefreshOccupyData();
                    dirtyList[i].RefreshTotalItemWeight();
                }
                catch (Exception ex)
                {
                    Plugin.LogOnce("restore_occupy", ex);
                }
            }
        }

        /// <summary>
        /// 取消当前正在进行的拖拽并安全还原物品回源容器。
        /// 原版 BasicItemUI.OnBeginDrag 会提前将拖动物品从非地面源容器列表中移除并暂存至 itemdataTemp，
        /// 因此在拖拽丢弃或交易路径拦截时必须执行完整取消归位，防止物品从背包凭空消失。
        /// </summary>
        internal static void CancelActiveDragAndRestore(BasicItemUI ui)
        {
            if (ui == null) return;
            bool prevInCustom = PickupRoutingState.InCustomAdd;
            PickupRoutingState.InCustomAdd = true;
            try
            {
                // 1. 若处于拖拽状态（itemdataTemp 非空），通过原版 CancelDragInPlace 合并拆分余量并归位
                if (ui.itemdataTemp != null)
                {
                    ui.CancelDragInPlace();
                }

                // 2. 防御性兜底：若 CancelDragInPlace 后物品仍不在非地面源容器中，直接调用 RestoreOrDrop 归位
                var panel = ui.inventoryPanel;
                var inv = panel != null ? panel.inventoryData : null;
                bool isGroundPanel = panel != null && panel.TryCast<NearbyItemInventoryPanel>() != null;
                if (!isGroundPanel && inv != null && ui.itemdata != null && !ui.IsItemStillInSource())
                {
                    inv.RestoreOrDrop(ui.itemdata);
                }

                ui.splitDragFlag = false;

                // 3. 归位食物/工具分组成员与框选多拖成员
                if (ui.draggingGroupMembers != null && ui.draggingGroupMembers.Count > 0)
                    ui.RestoreDraggingGroupMembers();

                if (ui.multiDragMembers != null && ui.multiDragMembers.Count > 0)
                {
                    ui.RestoreMultiMembers();
                    ui.EndMultiDrag();
                }

                if (!isGroundPanel && inv != null)
                {
                    inv.RefreshOccupyData();
                    inv.RefreshTotalItemWeight();
                }

                panel?.Refresh();
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("cancel_drag_restore", ex);
            }
            finally
            {
                PickupRoutingState.InCustomAdd = prevInCustom;
            }
        }

        private static bool ContainsByPtr(Il2CppSystem.Collections.Generic.List<ItemData> list, ItemData target)
        {
            IntPtr targetPtr = InventoryHelper.Ptr(target);
            if (targetPtr == IntPtr.Zero) return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (InventoryHelper.Ptr(list[i]) == targetPtr)
                    return true;
            }
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // 1. 物品收藏边框渲染与对象池清理
    // -------------------------------------------------------------------------

    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.RefreshItemUI))]
    internal static class Patch_BasicItemUI_RefreshItemUI_Fav
    {
        [HarmonyPostfix]
        private static void Postfix(BasicItemUI __instance)
        {
            ItemFavoriteManager.RefreshBadge(__instance);
        }
    }

    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.ObjectPoolInit))]
    internal static class Patch_BasicItemUI_ObjectPoolInit_Fav
    {
        [HarmonyPostfix]
        private static void Postfix(BasicItemUI __instance)
        {
            ItemFavoriteManager.HideBadge(__instance);
        }
    }

    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.OnDisable))]
    internal static class Patch_BasicItemUI_OnDisable_Fav
    {
        [HarmonyPostfix]
        private static void Postfix(BasicItemUI __instance)
        {
            ItemFavoriteManager.HideBadge(__instance);
        }
    }

    // -------------------------------------------------------------------------
    // 2. Alt+左键快捷切换收藏 & 右键菜单注入（收藏 + 容器筛选）
    // -------------------------------------------------------------------------

    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.OnPointerClick), typeof(PointerEventData))]
    internal static class Patch_BasicItemUI_OnPointerClick_Fav
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(BasicItemUI __instance, PointerEventData pointerEventData)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled) return true;
            if (__instance == null || __instance.itemdata == null || pointerEventData == null) return true;

            try
            {
                if (pointerEventData.button != PointerEventData.InputButton.Left) return true;

                KeyCode modKey = Plugin.FavoriteModifierKey != null ? Plugin.FavoriteModifierKey.Value : KeyCode.LeftAlt;
                if (modKey == KeyCode.None || !GameInputManager.GetKey(modKey)) return true;

                ItemFavoriteManager.ToggleFavorite(__instance, notify: true);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("click_toggle_fav", ex);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.ShowItemOptions))]
    internal static class Patch_BasicItemUI_ShowItemOptions_Context
    {
        [HarmonyPrefix]
        private static void Prefix(BasicItemUI __instance)
        {
            FavoriteGuardHelper.ActiveContextMenuUI = __instance;
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            FavoriteGuardHelper.ActiveContextMenuUI = null;
        }
    }

    [HarmonyPatch(typeof(ButtonsPanel), nameof(ButtonsPanel.ShowButtons),
        typeof(RectTransform), typeof(Il2CppSystem.Collections.Generic.List<Button>))]
    internal static class Patch_ButtonsPanel_ShowButtons_Inject
    {
        [HarmonyPrefix]
        private static void Prefix(Il2CppSystem.Collections.Generic.List<Button> m_buttonList)
        {
            var itemUI = FavoriteGuardHelper.ActiveContextMenuUI;
            if (itemUI == null || itemUI.itemdata == null || m_buttonList == null || m_buttonList.Count == 0)
                return;

            try
            {
                Button template = FindNormalTemplateButton(m_buttonList);
                if (template == null) return;

                // 注入“收藏 / 取消收藏”菜单项
                if (FavoriteGuardHelper.FavoritesEnabled)
                {
                    bool isFav = ItemFavoriteManager.IsUIFavorited(itemUI);
                    string label = isFav
                        ? Plugin.L("取消收藏", "Unfavorite")
                        : Plugin.L("收藏物品", "Favorite");

                    BasicItemUI capturedUI = itemUI;
                    var favBtn = CloneContextButton(template, "SmartInv_CtxFav", label, () =>
                    {
                        if (capturedUI != null && capturedUI.itemdata != null)
                        {
                            ItemFavoriteManager.ToggleFavorite(capturedUI, notify: true);
                            capturedUI.CloseItemOptions();
                        }
                    });
                    if (favBtn != null)
                        m_buttonList.Add(favBtn);
                }

                // 若该物品本身带有子容器（套包/战术包/衣服口袋），额外提供“筛选与优先级”入口
                var subInv = itemUI.itemdata.inventoryData;
                if (subInv != null && ((Plugin.EnablePickupRouting != null && Plugin.EnablePickupRouting.Value) || FavoriteGuardHelper.FavoritesEnabled))
                {
                    string ruleLabel = Plugin.L("⚙ 筛选与优先级", "⚙ Filter & Priority");
                    ItemData capturedOwner = itemUI.itemdata;
                    InventoryData capturedInv = subInv;
                    BasicItemUI capturedUI = itemUI;

                    var ruleBtn = CloneContextButton(template, "SmartInv_CtxRule", ruleLabel, () =>
                    {
                        if (capturedUI != null) capturedUI.CloseItemOptions();
                        ContainerFilterUI.OpenPopup(
                            capturedInv,
                            capturedOwner,
                            InventoryHelper.GetItemDisplayName(capturedOwner));
                    });
                    if (ruleBtn != null)
                        m_buttonList.Add(ruleBtn);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("ctx_menu_inject", ex);
            }
        }

        private static Button FindNormalTemplateButton(Il2CppSystem.Collections.Generic.List<Button> list)
        {
            // 优先取首个可交互按钮（通常是使用/装备/打开），避免复制到末尾红色危险按钮配色
            for (int i = 0; i < list.Count; i++)
            {
                var btn = list[i];
                if (btn != null && btn.interactable)
                    return btn;
            }
            return null;
        }

        private static Button CloneContextButton(Button template, string name, string label, Action onClick)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject);
            if (go == null) return null;
            go.name = name;
            go.SetActive(true);

            var btn = go.GetComponent<Button>();
            if (btn == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }

            btn.interactable = true;
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener((UnityAction)(() => onClick?.Invoke()));

            var txt = go.GetComponentInChildren<Text>(true);
            if (txt != null)
                txt.text = label;

            return btn;
        }
    }

    // -------------------------------------------------------------------------
    // 3. 堆叠与食物分组合并保护（防止收藏物品并入未收藏堆叠丢失标记）
    // -------------------------------------------------------------------------

    [HarmonyPatch(typeof(InventoryData), nameof(InventoryData.CanMergeStack),
        typeof(ItemData), typeof(ItemData))]
    internal static class Patch_InventoryData_CanMergeStack_Fav
    {
        [HarmonyPostfix]
        private static void Postfix(ItemData a, ItemData b, ref bool __result)
        {
            if (!__result || !FavoriteGuardHelper.FavoritesEnabled) return;
            if (a == null || b == null) return;

            // a 为目标容器已有堆叠，b 为待并入物品：
            // 仅禁止“已收藏的 b”并入“未收藏的 a”，避免被未收藏堆叠覆盖；反向（新捡的 b 补满已收藏的 a）允许合并。
            if (!ItemFavoriteManager.IsFavorite(a) && ItemFavoriteManager.IsFavorite(b))
                __result = false;
        }
    }

    [HarmonyPatch(typeof(InventoryData), nameof(InventoryData.CanGroupFood),
        typeof(ItemData), typeof(ItemData))]
    internal static class Patch_InventoryData_CanGroupFood_Fav
    {
        [HarmonyPostfix]
        private static void Postfix(ItemData a, ItemData b, ref bool __result)
        {
            if (!__result || !FavoriteGuardHelper.FavoritesEnabled) return;
            if (a == null || b == null) return;

            if (ItemFavoriteManager.IsFavorite(a) != ItemFavoriteManager.IsFavorite(b))
                __result = false;
        }
    }

    // -------------------------------------------------------------------------
    // 4. 一键堆叠 / 存入同类 / 全部存入 / 批量粉碎保护（单机 + 联机客户端）
    // -------------------------------------------------------------------------

    [HarmonyPatch(typeof(NearbyStorageLocator), nameof(NearbyStorageLocator.QuickStack),
        typeof(InventoryData), typeof(Il2CppSystem.Collections.Generic.List<InventoryData>))]
    internal static class Patch_NearbyStorageLocator_QuickStack_Guard
    {
        internal sealed class State
        {
            internal List<FavoriteGuardHelper.StashedItem> Stashed;
            internal bool PushedFilterDepth;
        }

        [HarmonyPrefix]
        private static void Prefix(InventoryData m_sourceInventory, ref State __state)
        {
            __state = new State();
            try
            {
                if (Plugin.ApplyFilterOnQuickStack != null && Plugin.ApplyFilterOnQuickStack.Value)
                {
                    PickupRoutingState.QuickStackFilterDepth++;
                    __state.PushedFilterDepth = true;
                }

                if (FavoriteGuardHelper.FavoritesEnabled
                    && Plugin.ProtectFromQuickStack != null
                    && Plugin.ProtectFromQuickStack.Value
                    && m_sourceInventory != null)
                {
                    __state.Stashed = FavoriteGuardHelper.StashMatching(
                        m_sourceInventory,
                        it => FavoriteGuardHelper.IsProtectedItem(m_sourceInventory, it));
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("quickstack_guard_pre", ex);
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer(State __state)
        {
            if (__state == null) return;
            if (__state.PushedFilterDepth && PickupRoutingState.QuickStackFilterDepth > 0)
                PickupRoutingState.QuickStackFilterDepth--;
            FavoriteGuardHelper.RestoreStashed(__state.Stashed);
        }
    }

    [HarmonyPatch(typeof(NetContainerSync), nameof(NetContainerSync.ClientRequestQuickStack),
        typeof(Il2CppSystem.Collections.Generic.List<InventoryData>))]
    internal static class Patch_NetContainerSync_ClientRequestQuickStack_Guard
    {
        [HarmonyPrefix]
        private static void Prefix(
            Il2CppSystem.Collections.Generic.List<InventoryData> m_explicitSources,
            ref List<FavoriteGuardHelper.StashedItem> __state)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled
                || Plugin.ProtectFromQuickStack == null
                || !Plugin.ProtectFromQuickStack.Value)
                return;

            try
            {
                var sources = new List<InventoryData>();
                if (m_explicitSources != null && m_explicitSources.Count > 0)
                {
                    for (int i = 0; i < m_explicitSources.Count; i++)
                    {
                        if (m_explicitSources[i] != null)
                            sources.Add(m_explicitSources[i]);
                    }
                }
                else
                {
                    var cd = InventoryHelper.LocalPlayer?.characterData;
                    if (cd != null)
                    {
                        var tmp = new Il2CppSystem.Collections.Generic.List<InventoryData>();
                        NearbyStorageLocator.CollectQuickStackSources(cd, tmp, false);
                        for (int i = 0; i < tmp.Count; i++)
                        {
                            if (tmp[i] != null)
                                sources.Add(tmp[i]);
                        }
                    }
                }

                __state = FavoriteGuardHelper.StashFavoritesInInventories(sources);
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("net_quickstack_guard", ex);
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer(List<FavoriteGuardHelper.StashedItem> __state)
        {
            FavoriteGuardHelper.RestoreStashed(__state);
        }
    }

    [HarmonyPatch(typeof(InventoryPanel), nameof(InventoryPanel.DepositAllItemsTo),
        typeof(InventoryData), typeof(InventoryData))]
    internal static class Patch_InventoryPanel_DepositAllItemsTo_Guard
    {
        [HarmonyPrefix]
        private static void Prefix(
            InventoryData m_source,
            InventoryData m_target,
            ref List<FavoriteGuardHelper.StashedItem> __state)
        {
            if (m_source == null || m_target == null || InventoryHelper.Same(m_source, m_target))
                return;

            try
            {
                bool protectFav = FavoriteGuardHelper.FavoritesEnabled
                                  && Plugin.ProtectFromDepositAll != null
                                  && Plugin.ProtectFromDepositAll.Value;
                bool filterTarget = Plugin.ApplyFilterOnDepositAll != null
                                    && Plugin.ApplyFilterOnDepositAll.Value;

                bool sourceProtected = protectFav && ContainerRuleStore.IsContainerProtected(m_source);
                var targetRule = filterTarget ? ContainerRuleStore.GetRule(m_target) : null;
                bool checkRule = targetRule != null && targetRule.HasActiveFilter;

                if (!protectFav && !checkRule) return;

                __state = FavoriteGuardHelper.StashMatching(m_source, it =>
                {
                    if (protectFav && (sourceProtected || ItemFavoriteManager.IsFavorite(it)))
                        return true;
                    if (checkRule)
                    {
                        var attr = InventoryHelper.GetAttr(it.itemId);
                        if (!ContainerRuleStore.Accepts(m_target, it, attr))
                            return true;
                    }
                    return false;
                });
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("deposit_all_guard", ex);
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer(List<FavoriteGuardHelper.StashedItem> __state)
        {
            FavoriteGuardHelper.RestoreStashed(__state);
        }
    }

    [HarmonyPatch(typeof(InventoryPanel), nameof(InventoryPanel.CollectAllItemsToPlayer),
        typeof(InventoryData))]
    internal static class Patch_InventoryPanel_CollectAllItemsToPlayer_Fav
    {
        [HarmonyPrefix]
        private static void Prefix(
            InventoryData sourceInventory,
            ref List<FavoriteGuardHelper.StashedItem> __state)
        {
            if (sourceInventory == null) return;
            if (!FavoriteGuardHelper.FavoritesEnabled
                || Plugin.ProtectFromTakeAll == null
                || !Plugin.ProtectFromTakeAll.Value)
                return;

            try
            {
                __state = FavoriteGuardHelper.StashMatching(
                    sourceInventory,
                    it => FavoriteGuardHelper.IsProtectedItem(sourceInventory, it));
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("take_all_fav_guard", ex);
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer(
            InventoryPanel __instance,
            List<FavoriteGuardHelper.StashedItem> __state)
        {
            if (__state == null || __state.Count == 0) return;
            FavoriteGuardHelper.RestoreStashed(__state);
            try
            {
                __instance?.Refresh();
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("take_all_fav_refresh", ex);
            }
            Plugin.Notify("已保留容器内的受保护物品（Alt+左键或关闭整包保护后可取走）",
                          "Kept protected item(s) in container (Alt+Click or disable container protection to take)");
        }
    }

    [HarmonyPatch(typeof(TerrainObject_Furniture_Inventory_Grinder),
        nameof(TerrainObject_Furniture_Inventory_Grinder.IsBulkGrindCandidate), typeof(ItemData))]
    internal static class Patch_Grinder_IsBulkGrindCandidate_Fav
    {
        [HarmonyPostfix]
        private static void Postfix(
            TerrainObject_Furniture_Inventory_Grinder __instance,
            ItemData m_itemData,
            ref bool __result)
        {
            if (!__result || !FavoriteGuardHelper.FavoritesEnabled) return;
            if (Plugin.ProtectFromBulkGrind == null || !Plugin.ProtectFromBulkGrind.Value) return;

            if (ItemFavoriteManager.IsFavorite(m_itemData)
                || ContainerRuleStore.IsContainerProtected(__instance?.objectData?.inventoryData)
                || FavoriteGuardHelper.IsInProtectedCarriedContainer(m_itemData))
                __result = false;
        }
    }

    // -------------------------------------------------------------------------
    // 5. 丢弃 / 拆解 / 交易拖拽 / 可选快捷转移保护
    // -------------------------------------------------------------------------

    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.DropItem))]
    internal static class Patch_BasicItemUI_DropItem_Fav
    {
        [HarmonyPrefix]
        private static bool Prefix(BasicItemUI __instance)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled) return true;
            if (Plugin.ProtectFromDrop == null || !Plugin.ProtectFromDrop.Value) return true;
            if (__instance == null) return true;
            if (!FavoriteGuardHelper.IsProtectedUI(__instance, out bool fromContainer))
                return true;

            try
            {
                FavoriteGuardHelper.CancelActiveDragAndRestore(__instance);
                __instance.CloseItemOptions();
                if (fromContainer)
                    Plugin.Notify("容器已开启整包保护，无法丢弃", "Protected container item cannot be dropped");
                else
                    Plugin.Notify("物品已收藏，无法丢弃（Alt+左键可取消收藏）",
                                  "Favorited item cannot be dropped (Alt+Click to unfavorite)");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("drop_fav_guard", ex);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.DropDraggingGroupMembersToGround))]
    internal static class Patch_BasicItemUI_DropDraggingGroup_Fav
    {
        [HarmonyPrefix]
        private static bool Prefix(BasicItemUI __instance)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled) return true;
            if (Plugin.ProtectFromDrop == null || !Plugin.ProtectFromDrop.Value) return true;
            if (__instance == null) return true;

            if (ContainerRuleStore.IsUIInProtectedContainer(__instance))
            {
                FavoriteGuardHelper.CancelActiveDragAndRestore(__instance);
                Plugin.Notify("容器已开启整包保护，已取消丢弃", "Protected container; drop cancelled");
                return false;
            }

            var members = __instance.draggingGroupMembers;
            if (members == null || members.Count == 0) return true;

            for (int i = 0; i < members.Count; i++)
            {
                if (ItemFavoriteManager.IsFavorite(members[i]))
                {
                    FavoriteGuardHelper.CancelActiveDragAndRestore(__instance);
                    Plugin.Notify("包含已收藏物品，已取消丢弃", "Contains favorited item; drop cancelled");
                    return false;
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.DropMultiMembersToGround))]
    internal static class Patch_BasicItemUI_DropMulti_Fav
    {
        [HarmonyPrefix]
        private static bool Prefix(BasicItemUI __instance)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled) return true;
            if (Plugin.ProtectFromDrop == null || !Plugin.ProtectFromDrop.Value) return true;
            if (__instance == null) return true;

            if (ContainerRuleStore.IsUIInProtectedContainer(__instance))
            {
                FavoriteGuardHelper.CancelActiveDragAndRestore(__instance);
                Plugin.Notify("容器已开启整包保护，已取消丢弃", "Protected container; drop cancelled");
                return false;
            }

            var members = __instance.multiDragMembers;
            if (members == null || members.Count == 0) return true;

            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                if (m != null && ItemFavoriteManager.IsFavorite(m.item))
                {
                    FavoriteGuardHelper.CancelActiveDragAndRestore(__instance);
                    Plugin.Notify("框选物品中包含已收藏物品，已取消丢弃",
                                  "Selection contains favorited item; drop cancelled");
                    return false;
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.DropOn_MultiOnPanel),
        typeof(PointerEventData), typeof(InventoryPanel))]
    internal static class Patch_BasicItemUI_DropOn_MultiOnPanel_Fav
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(BasicItemUI __instance, InventoryPanel m_targetPanel, ref bool __result)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled) return true;
            if (Plugin.ProtectFromDrop == null || !Plugin.ProtectFromDrop.Value) return true;
            if (__instance == null || m_targetPanel == null) return true;
            if (m_targetPanel.TryCast<NearbyItemInventoryPanel>() == null) return true;

            if (!FavoriteGuardHelper.IsProtectedUI(__instance, out bool fromContainer))
                return true;

            try
            {
                FavoriteGuardHelper.CancelActiveDragAndRestore(__instance);
                __instance.multiDropBlockedFrame = Time.frameCount;
                if (fromContainer)
                    Plugin.Notify("容器已开启整包保护，已取消丢弃", "Protected container; drop cancelled");
                else
                    Plugin.Notify("框选物品中包含已收藏物品，已取消丢弃",
                                  "Selection contains favorited item; drop cancelled");
                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("drop_multi_panel_fav", ex);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.Disassemble))]
    internal static class Patch_BasicItemUI_Disassemble_Fav
    {
        [HarmonyPrefix]
        private static bool Prefix(BasicItemUI __instance)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled) return true;
            if (Plugin.ProtectFromDisassemble == null || !Plugin.ProtectFromDisassemble.Value) return true;
            if (__instance == null || !FavoriteGuardHelper.IsProtectedUI(__instance, out bool fromContainer)) return true;

            __instance.CloseItemOptions();
            if (fromContainer)
                Plugin.Notify("容器已开启整包保护，无法拆解", "Protected container item cannot be disassembled");
            else
                Plugin.Notify("物品已收藏，无法拆解（Alt+左键可取消收藏）",
                              "Favorited item cannot be disassembled (Alt+Click to unfavorite)");
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_NearbyItemInventoryPanel_DisassembleAll_Fav
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(NearbyItemInventoryPanel),
                nameof(NearbyItemInventoryPanel.CollectDisassembleAllCandidates),
                new[] { typeof(Il2CppSystem.Collections.Generic.List<ItemData>), typeof(int).MakeByRefType() });
        }

        [HarmonyPostfix]
        private static void Postfix(
            Il2CppSystem.Collections.Generic.List<ItemData> m_results,
            ref int __result)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled) return;
            if (Plugin.ProtectFromDisassemble == null || !Plugin.ProtectFromDisassemble.Value) return;
            if (m_results == null || m_results.Count == 0) return;

            for (int i = m_results.Count - 1; i >= 0; i--)
            {
                if (ItemFavoriteManager.IsFavorite(m_results[i]))
                    m_results.RemoveAt(i);
            }
            __result = m_results.Count;
        }
    }

    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.DropOn_Trade),
        typeof(PointerEventData), typeof(ItemAttr), typeof(GameObject))]
    internal static class Patch_BasicItemUI_DropOn_Trade_Fav
    {
        [HarmonyPrefix]
        private static bool Prefix(BasicItemUI __instance, GameObject dropObj, ref bool __result)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled) return true;
            if (Plugin.ProtectFromSell == null || !Plugin.ProtectFromSell.Value) return true;
            if (__instance == null || dropObj == null) return true;
            if (!FavoriteGuardHelper.IsProtectedUI(__instance, out bool fromContainer))
                return true;

            try
            {
                if (dropObj.GetComponentInParent<TradePanel>() == null) return true;

                FavoriteGuardHelper.CancelActiveDragAndRestore(__instance);
                if (fromContainer)
                    Plugin.Notify("容器已开启整包保护，无法出售", "Protected container item cannot be sold");
                else
                    Plugin.Notify("物品已收藏，无法出售（Alt+左键可取消收藏）",
                                  "Favorited item cannot be sold (Alt+Click to unfavorite)");
                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("drop_trade_fav", ex);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(InventoryData), nameof(InventoryData.RestoreOrDrop), typeof(ItemData))]
    internal static class Patch_InventoryData_RestoreOrDrop_Guard
    {
        [HarmonyPrefix]
        private static void Prefix(out bool __state)
        {
            __state = PickupRoutingState.InCustomAdd;
            PickupRoutingState.InCustomAdd = true;
        }

        [HarmonyFinalizer]
        private static void Finalizer(bool __state)
        {
            PickupRoutingState.InCustomAdd = __state;
        }
    }

    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.TryQuickTransferToOpenContainer))]
    internal static class Patch_BasicItemUI_QuickTransfer_Fav
    {
        [HarmonyPrefix]
        private static bool Prefix(BasicItemUI __instance, ref BasicItemUI.QuickTransferResult __result)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled) return true;
            if (Plugin.ProtectFromQuickTransfer == null || !Plugin.ProtectFromQuickTransfer.Value) return true;
            if (__instance == null || !FavoriteGuardHelper.IsProtectedUI(__instance, out bool fromContainer)) return true;

            if (fromContainer)
                Plugin.Notify("容器已开启整包保护，已阻止快速转移", "Protected container blocked quick transfer");
            else
                Plugin.Notify("物品已收藏，已阻止快速转移", "Favorited item blocked from quick transfer");
            __result = BasicItemUI.QuickTransferResult.Rejected;
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // 6. 商人出售列表、统计与堆叠扣除保护（兼容 NestedInventoryFix）
    // -------------------------------------------------------------------------

    [HarmonyPatch(typeof(NPCInteractController), nameof(NPCInteractController.GetAllCharacterItems))]
    internal static class Patch_NPCInteractController_GetAllCharacterItems_Fav
    {
        // Priority.Low 确保晚于 NestedInventoryFix 的 Postfix 执行，连套包内的受保护物品一并排除
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        private static void Postfix(Il2CppSystem.Collections.Generic.List<ItemData> __result)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled) return;
            if (Plugin.ProtectFromSell == null || !Plugin.ProtectFromSell.Value) return;
            if (__result == null || __result.Count == 0) return;

            var cd = InventoryHelper.LocalPlayer?.characterData;
            var protectedPtrs = FavoriteGuardHelper.CollectProtectedCarriedItemPtrs(cd, excludeNonFavoriteCash: true);

            for (int i = __result.Count - 1; i >= 0; i--)
            {
                var it = __result[i];
                if (ItemFavoriteManager.IsFavorite(it))
                {
                    __result.RemoveAt(i);
                    continue;
                }
                if (protectedPtrs != null)
                {
                    IntPtr ptr = InventoryHelper.Ptr(it);
                    if (ptr != IntPtr.Zero && protectedPtrs.Contains(ptr))
                        __result.RemoveAt(i);
                }
            }
        }
    }

    [HarmonyPatch(typeof(NPCInteractController), nameof(NPCInteractController.GetTotalItemNumberInAllInventories),
        typeof(int))]
    internal static class Patch_NPCInteractController_GetTotalItemNumber_Fav
    {
        // Priority.High 先临时移出全部携带容器（含套包）中的受保护物品，Priority.Low 在原版及 NestedInventoryFix 统计结束后立刻归位
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static void Prefix(int itemId, ref List<FavoriteGuardHelper.StashedItem> __state)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled) return;
            if (Plugin.ProtectFromSell == null || !Plugin.ProtectFromSell.Value) return;

            var cd = InventoryHelper.LocalPlayer?.characterData;
            if (cd == null) return;
            var allInvs = InventoryHelper.CollectPlayerCarriedInventories(cd, includeNested: true);
            __state = FavoriteGuardHelper.StashFavoritesInInventories(allInvs, itemId, allowCashInProtectedContainer: true);
        }

        [HarmonyFinalizer]
        [HarmonyPriority(Priority.Low)]
        private static void Finalizer(List<FavoriteGuardHelper.StashedItem> __state)
        {
            FavoriteGuardHelper.RestoreStashed(__state);
        }
    }

    [HarmonyPatch(typeof(NPCInteractController), nameof(NPCInteractController.RemoveStackableItemFromAllInventories),
        typeof(int), typeof(int))]
    internal static class Patch_NPCInteractController_RemoveStackable_Fav
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static void Prefix(int itemId, ref List<FavoriteGuardHelper.StashedItem> __state)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled) return;
            if (Plugin.ProtectFromSell == null || !Plugin.ProtectFromSell.Value) return;

            var cd = InventoryHelper.LocalPlayer?.characterData;
            if (cd == null) return;
            var allInvs = InventoryHelper.CollectPlayerCarriedInventories(cd, includeNested: true);
            __state = FavoriteGuardHelper.StashFavoritesInInventories(allInvs, itemId, allowCashInProtectedContainer: true);
        }

        [HarmonyFinalizer]
        [HarmonyPriority(Priority.Low)]
        private static void Finalizer(List<FavoriteGuardHelper.StashedItem> __state)
        {
            FavoriteGuardHelper.RestoreStashed(__state);
        }
    }

    // -------------------------------------------------------------------------
    // 7. 换弹/卸弹匣与更换武器电池时跨生命周期保留收藏标记
    //    （原版装入枪械后会回收原 ItemData 并在退出时新建 ItemData，此处借枪械本体属性代存收藏位）
    // -------------------------------------------------------------------------

    [HarmonyPatch(typeof(ItemAttr_RangedWeapon), nameof(ItemAttr_RangedWeapon.SwapMagazineIntoWeapon),
        typeof(ItemData), typeof(ItemData))]
    internal static class Patch_RangedWeapon_SwapMagazine_Fav
    {
        internal struct SwapState
        {
            public bool OldFav;
            public bool NewFav;
        }

        [HarmonyPrefix]
        private static void Prefix(ItemData weaponData, ItemData newMagazineData, out SwapState __state)
        {
            __state = default;
            if (!FavoriteGuardHelper.FavoritesEnabled || weaponData == null) return;
            __state.OldFav = ItemFavoriteManager.GetStoredSlotFavorite(weaponData, ItemFavoriteManager.MagazineFavoritePropertyKey);
            __state.NewFav = ItemFavoriteManager.IsFavorite(newMagazineData);
        }

        [HarmonyPostfix]
        private static void Postfix(ItemData weaponData, ItemData __result, SwapState __state)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled || weaponData == null) return;
            try
            {
                if (__result != null && __state.OldFav)
                    ItemFavoriteManager.SetFavorite(__result, true);
                ItemFavoriteManager.SetStoredSlotFavorite(weaponData, ItemFavoriteManager.MagazineFavoritePropertyKey, __state.NewFav);
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("swap_mag_fav", ex);
            }
        }
    }

    [HarmonyPatch(typeof(ItemData), nameof(ItemData.StoreMagazinePaintFrom), typeof(ItemData))]
    internal static class Patch_ItemData_StoreMagazinePaintFrom_Fav
    {
        [HarmonyPostfix]
        private static void Postfix(ItemData __instance, ItemData magazineItemData)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled || __instance == null) return;
            ItemFavoriteManager.SetStoredSlotFavorite(
                __instance,
                ItemFavoriteManager.MagazineFavoritePropertyKey,
                ItemFavoriteManager.IsFavorite(magazineItemData));
        }
    }

    [HarmonyPatch(typeof(ItemData), nameof(ItemData.RestoreMagazinePaintTo), typeof(ItemData))]
    internal static class Patch_ItemData_RestoreMagazinePaintTo_Fav
    {
        [HarmonyPostfix]
        private static void Postfix(ItemData __instance, ItemData magazineItemData)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled || __instance == null || magazineItemData == null) return;
            if (ItemFavoriteManager.GetStoredSlotFavorite(__instance, ItemFavoriteManager.MagazineFavoritePropertyKey))
            {
                ItemFavoriteManager.SetFavorite(magazineItemData, true);
                ItemFavoriteManager.SetStoredSlotFavorite(__instance, ItemFavoriteManager.MagazineFavoritePropertyKey, false);
            }
        }
    }

    [HarmonyPatch(typeof(ItemAttr_RangedWeapon), nameof(ItemAttr_RangedWeapon.SwapBatteryIntoWeapon),
        typeof(ItemData), typeof(ItemData))]
    internal static class Patch_RangedWeapon_SwapBattery_Fav
    {
        internal struct SwapState
        {
            public bool OldFav;
            public bool NewFav;
        }

        [HarmonyPrefix]
        private static void Prefix(ItemData weaponData, ItemData newBatteryData, out SwapState __state)
        {
            __state = default;
            if (!FavoriteGuardHelper.FavoritesEnabled || weaponData == null) return;
            __state.OldFav = ItemFavoriteManager.GetStoredSlotFavorite(weaponData, ItemFavoriteManager.BatteryFavoritePropertyKey);
            __state.NewFav = ItemFavoriteManager.IsFavorite(newBatteryData);
        }

        [HarmonyPostfix]
        private static void Postfix(ItemData weaponData, ItemData __result, SwapState __state)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled || weaponData == null) return;
            try
            {
                if (__result != null && __state.OldFav)
                    ItemFavoriteManager.SetFavorite(__result, true);
                ItemFavoriteManager.SetStoredSlotFavorite(weaponData, ItemFavoriteManager.BatteryFavoritePropertyKey, __state.NewFav);
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("swap_bat_fav", ex);
            }
        }
    }

    [HarmonyPatch(typeof(ItemAttr_RangedWeapon), nameof(ItemAttr_RangedWeapon.StoreWeaponBatteryDurability),
        typeof(ItemData), typeof(ItemData))]
    internal static class Patch_RangedWeapon_StoreBatteryDurability_Fav
    {
        [HarmonyPostfix]
        private static void Postfix(ItemData weaponData, ItemData batteryData)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled || weaponData == null) return;
            ItemFavoriteManager.SetStoredSlotFavorite(
                weaponData,
                ItemFavoriteManager.BatteryFavoritePropertyKey,
                ItemFavoriteManager.IsFavorite(batteryData));
        }
    }

    [HarmonyPatch(typeof(ItemAttr_RangedWeapon), nameof(ItemAttr_RangedWeapon.RestoreWeaponBatteryDurability),
        typeof(ItemData), typeof(ItemData))]
    internal static class Patch_RangedWeapon_RestoreBatteryDurability_Fav
    {
        [HarmonyPostfix]
        private static void Postfix(ItemData weaponData, ItemData batteryData)
        {
            if (!FavoriteGuardHelper.FavoritesEnabled || weaponData == null || batteryData == null) return;
            if (ItemFavoriteManager.GetStoredSlotFavorite(weaponData, ItemFavoriteManager.BatteryFavoritePropertyKey))
            {
                ItemFavoriteManager.SetFavorite(batteryData, true);
                ItemFavoriteManager.SetStoredSlotFavorite(weaponData, ItemFavoriteManager.BatteryFavoritePropertyKey, false);
            }
        }
    }

    // -------------------------------------------------------------------------
    // 8. 远程枪械配件（枪管/瞄具/枪托/弹壳收集袋）、近战配件与普通设备电池盒换装保留收藏标记
    // -------------------------------------------------------------------------

    [HarmonyPatch(typeof(ItemAttr_RangedWeapon), nameof(ItemAttr_RangedWeapon.ChangeGunPart),
        typeof(ItemData), typeof(GunPartType), typeof(ItemData))]
    internal static class Patch_RangedWeapon_ChangeGunPart_Fav
    {
        internal struct GunPartSwapState
        {
            public string SlotKey;
            public bool NewFav;
            public bool Active;
        }

        [HarmonyPrefix]
        private static void Prefix(
            ItemData m_gunPartData,
            GunPartType m_gunPartType,
            ItemData m_rangedWeaponData,
            out GunPartSwapState __state)
        {
            __state = default;
            if (!FavoriteGuardHelper.FavoritesEnabled || m_rangedWeaponData == null) return;

            string slotKey = ItemFavoriteManager.GetGunPartFavoriteKey(m_gunPartType);
            __state.SlotKey = slotKey;
            __state.NewFav = ItemFavoriteManager.IsFavorite(m_gunPartData);
            __state.Active = true;

            FavoriteGuardHelper.ChangeGunPartScopeDepth++;
            if (ItemFavoriteManager.GetStoredSlotFavorite(m_rangedWeaponData, slotKey))
                FavoriteGuardHelper.PendingRestoreFavoriteOnAdd = true;
        }

        [HarmonyFinalizer]
        private static void Finalizer(ItemData m_rangedWeaponData, GunPartSwapState __state)
        {
            if (!__state.Active) return;

            FavoriteGuardHelper.PendingRestoreFavoriteOnAdd = false;
            if (FavoriteGuardHelper.ChangeGunPartScopeDepth > 0)
                FavoriteGuardHelper.ChangeGunPartScopeDepth--;

            if (m_rangedWeaponData != null && !string.IsNullOrEmpty(__state.SlotKey))
                ItemFavoriteManager.SetStoredSlotFavorite(m_rangedWeaponData, __state.SlotKey, __state.NewFav);
        }
    }

    [HarmonyPatch(typeof(ItemAttr_RangedWeapon), nameof(ItemAttr_RangedWeapon.ReturnEquippedGunPart),
        typeof(ItemData), typeof(int))]
    internal static class Patch_RangedWeapon_ReturnEquippedGunPart_Fav
    {
        [HarmonyPrefix]
        private static void Prefix(ItemData m_rangedWeaponData, int idIdx, out string __state)
        {
            __state = null;
            if (!FavoriteGuardHelper.FavoritesEnabled || m_rangedWeaponData == null) return;
            // 由 ChangeGunPart 调用时，外层已完整管理收藏恢复与新槽位写入
            if (FavoriteGuardHelper.ChangeGunPartScopeDepth > 0) return;

            string slotKey = ItemFavoriteManager.GetGunPartFavoriteKeyByIdIndex(idIdx);
            if (string.IsNullOrEmpty(slotKey)) return;
            if (!ItemFavoriteManager.GetStoredSlotFavorite(m_rangedWeaponData, slotKey)) return;

            __state = slotKey;
            FavoriteGuardHelper.PendingRestoreFavoriteOnAdd = true;
        }

        [HarmonyFinalizer]
        private static void Finalizer(ItemData m_rangedWeaponData, string __state)
        {
            if (string.IsNullOrEmpty(__state)) return;
            FavoriteGuardHelper.PendingRestoreFavoriteOnAdd = false;
            if (m_rangedWeaponData != null)
                ItemFavoriteManager.SetStoredSlotFavorite(m_rangedWeaponData, __state, false);
        }
    }

    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.InstallMeleeWeaponAttachment),
        typeof(ToolData), typeof(bool))]
    internal static class Patch_BasicItemUI_InstallMeleeAttachment_Fav
    {
        internal struct MeleeSwapState
        {
            public ItemData WeaponData;
            public bool NewFav;
            public bool Active;
        }

        [HarmonyPrefix]
        private static void Prefix(BasicItemUI __instance, ToolData toolData, out MeleeSwapState __state)
        {
            __state = default;
            if (!FavoriteGuardHelper.FavoritesEnabled || __instance?.itemdata == null) return;

            var weapon = __instance.itemdata;
            __state.WeaponData = weapon;
            __state.NewFav = ItemFavoriteManager.IsFavorite(toolData?.itemData);
            __state.Active = true;

            if (ItemFavoriteManager.GetStoredSlotFavorite(weapon, ItemFavoriteManager.MeleeAttachmentFavoritePropertyKey))
                FavoriteGuardHelper.PendingRestoreFavoriteOnAdd = true;
        }

        [HarmonyFinalizer]
        private static void Finalizer(MeleeSwapState __state)
        {
            if (!__state.Active) return;
            FavoriteGuardHelper.PendingRestoreFavoriteOnAdd = false;
            if (__state.WeaponData != null)
                ItemFavoriteManager.SetStoredSlotFavorite(__state.WeaponData, ItemFavoriteManager.MeleeAttachmentFavoritePropertyKey, __state.NewFav);
        }
    }

    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.RemoveMeleeWeaponAttachmentFromWeapon),
        typeof(ItemData))]
    internal static class Patch_BasicItemUI_RemoveMeleeAttachment_Fav
    {
        [HarmonyPrefix]
        private static void Prefix(ItemData weaponData, out bool __state)
        {
            __state = false;
            if (!FavoriteGuardHelper.FavoritesEnabled || weaponData == null) return;
            if (!ItemFavoriteManager.GetStoredSlotFavorite(weaponData, ItemFavoriteManager.MeleeAttachmentFavoritePropertyKey)) return;

            __state = true;
            FavoriteGuardHelper.PendingRestoreFavoriteOnAdd = true;
        }

        [HarmonyFinalizer]
        private static void Finalizer(ItemData weaponData, bool __state)
        {
            if (!__state) return;
            FavoriteGuardHelper.PendingRestoreFavoriteOnAdd = false;
            if (weaponData != null)
                ItemFavoriteManager.SetStoredSlotFavorite(weaponData, ItemFavoriteManager.MeleeAttachmentFavoritePropertyKey, false);
        }
    }

    [HarmonyPatch(typeof(GameController), nameof(GameController.TryAddItemToCharacter),
        typeof(BasicCharacterController), typeof(ItemData), typeof(bool), typeof(bool))]
    internal static class Patch_GameController_TryAddItemToCharacter_RestoreSlotFav
    {
        [HarmonyPrefix]
        private static void Prefix(ItemData itemData)
        {
            if (!FavoriteGuardHelper.PendingRestoreFavoriteOnAdd || itemData == null) return;
            FavoriteGuardHelper.PendingRestoreFavoriteOnAdd = false;
            ItemFavoriteManager.SetFavorite(itemData, true);
        }
    }

    [HarmonyPatch(typeof(ItemFeature_BatteryBox), nameof(ItemFeature_BatteryBox.TryAddBattery),
        typeof(ItemData), typeof(ItemData))]
    internal static class Patch_BatteryBox_TryAddBattery_Fav
    {
        internal struct BatteryAddState
        {
            public int SlotIndex;
            public bool IsFav;
        }

        [HarmonyPrefix]
        private static void Prefix(ItemData batteryBoxItemData, ItemData batteryItemData, out BatteryAddState __state)
        {
            __state = new BatteryAddState { SlotIndex = -1, IsFav = false };
            if (!FavoriteGuardHelper.FavoritesEnabled || batteryBoxItemData == null || batteryItemData == null) return;

            try
            {
                __state.SlotIndex = ItemFeature_BatteryBox.GetEmptyBatterySlotIndex(batteryBoxItemData, batteryItemData);
                __state.IsFav = ItemFavoriteManager.IsFavorite(batteryItemData);
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("bbox_add_pre", ex);
            }
        }

        [HarmonyPostfix]
        private static void Postfix(ItemData batteryBoxItemData, bool __result, BatteryAddState __state)
        {
            if (!__result || __state.SlotIndex < 0 || batteryBoxItemData == null) return;
            ItemFavoriteManager.SetStoredSlotFavorite(
                batteryBoxItemData,
                ItemFavoriteManager.GetBatteryBoxSlotKey(__state.SlotIndex),
                __state.IsFav);
        }
    }

    [HarmonyPatch(typeof(ItemFeature_BatteryBox), nameof(ItemFeature_BatteryBox.RemoveBattery),
        typeof(ItemData))]
    internal static class Patch_BatteryBox_RemoveBattery_Fav
    {
        [HarmonyPrefix]
        private static void Prefix(ItemData falshlightItemData, out Dictionary<string, string> __state)
        {
            __state = null;
            if (!FavoriteGuardHelper.FavoritesEnabled || falshlightItemData == null) return;

            var pairs = falshlightItemData.itemPropertyPairs;
            if (pairs == null || pairs.Count == 0) return;

            // 快照移除前的所有非模组属性键，便于 Postfix 精确识别刚被取出的电池槽位号
            var snap = new Dictionary<string, string>(pairs.Count, StringComparer.Ordinal);
            for (int i = 0; i < pairs.Count; i++)
            {
                var p = pairs[i];
                if (p == null || string.IsNullOrEmpty(p.key)) continue;
                if (p.key.StartsWith("bi_", StringComparison.Ordinal)) continue;
                if (!string.IsNullOrEmpty(p.value))
                    snap[p.key] = p.value;
            }
            __state = snap;
        }

        [HarmonyPostfix]
        private static void Postfix(ItemData falshlightItemData, ItemData __result, Dictionary<string, string> __state)
        {
            if (__result == null || falshlightItemData == null || __state == null) return;

            try
            {
                int removedSlot = -1;
                foreach (var kv in __state)
                {
                    string afterVal = falshlightItemData.GetProperty(kv.Key);
                    if (string.IsNullOrEmpty(afterVal))
                    {
                        if (TryParseTrailingInt(kv.Key, out int parsed))
                        {
                            removedSlot = parsed;
                            break;
                        }
                    }
                }

                if (removedSlot >= 0)
                {
                    string slotKey = ItemFavoriteManager.GetBatteryBoxSlotKey(removedSlot);
                    if (ItemFavoriteManager.GetStoredSlotFavorite(falshlightItemData, slotKey))
                    {
                        ItemFavoriteManager.SetFavorite(__result, true);
                        ItemFavoriteManager.SetStoredSlotFavorite(falshlightItemData, slotKey, false);
                    }
                }
                else
                {
                    // 兜底检查 0 号槽，防止特殊单槽设备键名不含数字
                    string fallbackKey = ItemFavoriteManager.GetBatteryBoxSlotKey(0);
                    if (ItemFavoriteManager.GetStoredSlotFavorite(falshlightItemData, fallbackKey))
                    {
                        ItemFavoriteManager.SetFavorite(__result, true);
                        ItemFavoriteManager.SetStoredSlotFavorite(falshlightItemData, fallbackKey, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("bbox_remove_post", ex);
            }
        }

        private static bool TryParseTrailingInt(string key, out int value)
        {
            value = -1;
            if (string.IsNullOrEmpty(key)) return false;
            int idx = key.Length - 1;
            while (idx >= 0 && char.IsDigit(key[idx])) idx--;
            if (idx == key.Length - 1) return false;
            return int.TryParse(key.Substring(idx + 1), out value);
        }
    }
}
