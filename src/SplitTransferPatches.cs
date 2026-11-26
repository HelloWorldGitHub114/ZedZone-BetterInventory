using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ZedZone.BetterInventory
{
    /// <summary>
    /// 接管并修复原版 Alt+右键（移 1 个）与 Ctrl+右键（移一半）拆分快移：
    /// 1. 解决搭配 Floating Loot Windows 时 LootInventoryPanel.instance 为空壳导致的 NullReferenceException 与全堆叠误移；
    /// 2. 支持食物/工具等同格合并组（isFoodGroup，xN）按 1 个 / 一半成员双向拆分；
    /// 3. 补齐右 Control、多口袋面板（3 个及以上）与外包悬浮窗向激活内包转移；
    /// 4. 当目标已满或被严格过滤拒绝时消费点击并提示，不回退到 OnPointerClick 整组移走。
    /// </summary>
    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.TryHandleSplitQuickTransferClick))]
    internal static class Patch_BasicItemUI_TryHandleSplitQuickTransferClick_Scope
    {
        private sealed class TransferTarget
        {
            internal readonly InventoryData Inventory;
            internal readonly InventoryPanel Panel;

            internal TransferTarget(InventoryData inventory, InventoryPanel panel)
            {
                Inventory = inventory;
                Panel = panel;
            }
        }

        private static bool s_flwLookupDone;
        private static MethodInfo s_flwGetFocusedPanelMethod;

        [HarmonyPrefix]
        private static bool Prefix(BasicItemUI __instance, ref bool __result, out InventoryData __state)
        {
            __state = PickupRoutingState.ExcludedSourceInventory;
            var sourcePanel = __instance != null ? __instance.inventoryPanel : null;
            var sourceInv = sourcePanel != null ? sourcePanel.inventoryData : null;
            if (sourceInv != null)
                PickupRoutingState.ExcludedSourceInventory = sourceInv;

            if (Plugin.FixSplitQuickTransfer == null || !Plugin.FixSplitQuickTransfer.Value)
                return true;

            try
            {
                if (GameInputManager.isUsingController)
                    return true;

                bool isAlt = GameInputManager.GetKey(KeyCode.LeftAlt) || GameInputManager.GetKey(KeyCode.RightAlt);
                bool isCtrl = GameInputManager.GetKey(KeyCode.LeftControl) || GameInputManager.GetKey(KeyCode.RightControl);
                if (!isAlt && !isCtrl)
                {
                    __result = false;
                    return false;
                }

                var tradePanel = TradePanel._instance;
                if (tradePanel != null && tradePanel.gameObject != null && tradePanel.gameObject.activeSelf)
                    return true;

                var item = __instance != null ? __instance.itemdata : null;
                if (sourcePanel == null || sourceInv == null || item == null)
                {
                    __result = false;
                    return false;
                }

                // 地面附近面板走原版逻辑，保持与多人房主同步拦截一致
                if (sourcePanel.TryCast<NearbyItemInventoryPanel>() != null)
                    return true;

                if (__instance.HostGroundTakeBlocked())
                {
                    __result = true;
                    return false;
                }

                if (!string.IsNullOrEmpty(item.combinationPassword))
                {
                    __result = true;
                    return false;
                }

                var player = InventoryHelper.LocalPlayer;
                var mainInv = player?.characterData?.inventoryData;
                var gc = GameController.instance;
                if (player == null || mainInv == null || gc == null)
                    return true;

                var attr = InventoryHelper.GetAttr(item.itemId);
                if (attr == null || attr.isMissingMod)
                    return true;

                bool isFoodGroup = __instance.isFoodGroup
                    && !__instance.isGroundGroup
                    && __instance.foodGroupMembers != null
                    && __instance.foodGroupMembers.Count > 1;
                bool isStack = !isFoodGroup && attr.stackNumber > 1 && item.itemNumberFloat > 1.0001f;

                // 非合并组且非可堆叠数量 (>1) 的单件物品交回原版右键处理
                if (!isFoodGroup && !isStack)
                {
                    __result = false;
                    return false;
                }

                bool isMain = InventoryHelper.Same(sourceInv, mainInv);
                if (isMain && IsMainBackpackRightClickEquippedGear(attr))
                {
                    __result = false;
                    return false;
                }

                if (!__instance.IsItemStillInSource())
                {
                    sourcePanel.Refresh();
                    __result = true;
                    return false;
                }

                if (FavoriteGuardHelper.FavoritesEnabled
                    && Plugin.ProtectFromQuickTransfer != null
                    && Plugin.ProtectFromQuickTransfer.Value
                    && FavoriteGuardHelper.IsProtectedUI(__instance, out bool fromContainer))
                {
                    if (fromContainer)
                        Plugin.Notify("容器已开启整包保护，已阻止快速转移", "Protected container blocked quick transfer");
                    else
                        Plugin.Notify("物品已收藏，已阻止快速转移", "Favorited item blocked from quick transfer");
                    __result = true;
                    return false;
                }

                LootInventoryPanel lootTargetPanel = ResolveActiveLootTarget();
                InventoryData lootTargetInv = lootTargetPanel != null ? lootTargetPanel.inventoryData : null;

                var craftPanel = CraftPanel._instance;
                bool craftOpen = craftPanel != null
                    && craftPanel.gameObject != null
                    && craftPanel.gameObject.activeInHierarchy
                    && craftPanel.materialInventoryData != null;
                InventoryData craftTargetInv = craftOpen ? craftPanel.materialInventoryData : null;

                bool isCommonPanel = !isMain && sourcePanel.TryCast<CommonInventoryPanel>() != null;
                bool isLootPanel = !isMain && !isCommonPanel && sourcePanel.TryCast<LootInventoryPanel>() != null;

                List<TransferTarget> containerTargets = null;
                bool notifyFull = true;
                bool transferOutToPlayer = false;

                if (isMain)
                {
                    if (lootTargetInv != null && !InventoryHelper.Same(lootTargetInv, sourceInv))
                    {
                        containerTargets = new List<TransferTarget>(1) { new TransferTarget(lootTargetInv, lootTargetPanel) };
                    }
                    else if (craftTargetInv != null && !InventoryHelper.Same(craftTargetInv, sourceInv))
                    {
                        containerTargets = new List<TransferTarget>(1)
                        {
                            new TransferTarget(craftTargetInv, craftPanel.materialInventoryPanel)
                        };
                    }
                    else
                    {
                        containerTargets = CollectMultiplePanelTargets(sourceInv);
                        if (containerTargets.Count == 0)
                        {
                            __result = true;
                            return false;
                        }
                        notifyFull = true;
                    }
                }
                else if (isCommonPanel)
                {
                    if (lootTargetInv != null && !InventoryHelper.Same(lootTargetInv, sourceInv))
                    {
                        containerTargets = new List<TransferTarget>(1) { new TransferTarget(lootTargetInv, lootTargetPanel) };
                    }
                    else if (craftTargetInv != null && !InventoryHelper.Same(craftTargetInv, sourceInv))
                    {
                        containerTargets = new List<TransferTarget>(1)
                        {
                            new TransferTarget(craftTargetInv, craftPanel.materialInventoryPanel)
                        };
                    }
                    else if (BasicItemUI.IsPlayerClothingPocket(player, sourceInv))
                    {
                        containerTargets = new List<TransferTarget>(1)
                        {
                            new TransferTarget(mainInv, PlayerInventoryPanel._instance)
                        };
                    }
                    else
                    {
                        transferOutToPlayer = true;
                    }
                }
                else if (isLootPanel
                    && lootTargetInv != null
                    && !InventoryHelper.Same(lootTargetInv, sourceInv)
                    && InventoryHelper.IsPlayerCarriedInventory(sourceInv))
                {
                    // 同时打开外层随身套包与内层子包（如悬浮多窗口模式）时，允许从外层套包拆分放入聚焦的内层容器
                    containerTargets = new List<TransferTarget>(1) { new TransferTarget(lootTargetInv, lootTargetPanel) };
                }
                else
                {
                    transferOutToPlayer = true;
                }

                if (isStack)
                {
                    ExecuteStackSplitTransfer(
                        __instance, item, attr, sourceInv, isAlt,
                        transferOutToPlayer, containerTargets, notifyFull, player, gc);
                }
                else
                {
                    ExecuteFoodGroupSplitTransfer(
                        __instance, item, attr, sourceInv, isAlt,
                        transferOutToPlayer, containerTargets, notifyFull, player, gc);
                }

                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                // 出现异常时依然消费此次点击，防止回退到 OnPointerClick 触发整组误移
                Plugin.LogOnce("split_quick_transfer", ex);
                __result = true;
                return false;
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer(InventoryData __state)
        {
            PickupRoutingState.ExcludedSourceInventory = __state;
        }

        private static bool IsMainBackpackRightClickEquippedGear(ItemAttr attr)
        {
            if (attr == null) return false;
            switch (attr.itemType)
            {
                case ItemType.FaceMask:
                case ItemType.MeleeWeapon:
                case ItemType.RangedWeapon:
                case ItemType.Clothing:
                case ItemType.Cloak:
                    return true;
            }

            var features = attr.itemFeatures;
            if (features == null) return false;
            if (features.Contains(ItemFeatureType.ExecutionTool)
                || features.Contains(ItemFeatureType.LightingTool))
                return false;

            return features.Contains(ItemFeatureType.HeadDecoration);
        }

        /// <summary>
        /// 解析当前活跃的战利品/背包容器面板：优先读取 Floating Loot Windows 的焦点悬浮窗，再回退到原版单例与活跃面板列表。
        /// </summary>
        private static LootInventoryPanel ResolveActiveLootTarget()
        {
            EnsureFloatingLootReflection();
            if (s_flwGetFocusedPanelMethod != null)
            {
                try
                {
                    var focused = s_flwGetFocusedPanelMethod.Invoke(null, null) as LootInventoryPanel;
                    if (IsValidActiveLootPanel(focused))
                        return focused;
                }
                catch (Exception ex)
                {
                    Plugin.LogOnce("split_flw_focus", ex);
                }
            }

            var singleton = LootInventoryPanel._instance;
            if (IsValidActiveLootPanel(singleton))
                return singleton;

            try
            {
                var activePanels = InventoryPanel.GetActivePanels();
                if (activePanels != null)
                {
                    for (int i = activePanels.Length - 1; i >= 0; i--)
                    {
                        var candidate = activePanels[i]?.TryCast<LootInventoryPanel>();
                        if (IsValidActiveLootPanel(candidate))
                            return candidate;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("split_active_panels", ex);
            }

            return null;
        }

        private static bool IsValidActiveLootPanel(LootInventoryPanel panel)
        {
            return panel != null
                && panel.gameObject != null
                && panel.gameObject.activeInHierarchy
                && panel.inventoryData != null;
        }

        private static void EnsureFloatingLootReflection()
        {
            if (s_flwLookupDone) return;
            s_flwLookupDone = true;

            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    var type = assemblies[i].GetType("ZedZone.FloatingLootWindows.FloatingWindowManager", false);
                    if (type == null) continue;

                    var method = type.GetMethod(
                        "GetFocusedPanel",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null,
                        Type.EmptyTypes,
                        null);
                    if (method != null)
                    {
                        s_flwGetFocusedPanelMethod = method;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("split_flw_reflect", ex);
            }
        }

        private static List<TransferTarget> CollectMultiplePanelTargets(InventoryData sourceInv)
        {
            var targets = new List<TransferTarget>();
            var multi = MultipleInventoryPanel._instance;
            if (multi == null || multi.gameObject == null || !multi.gameObject.activeInHierarchy)
                return targets;

            var panels = multi.inventoryPanels;
            if (panels == null || panels.Count == 0)
                return targets;

            for (int i = 0; i < panels.Count; i++)
            {
                var p = panels[i];
                if (p == null || p.inventoryData == null) continue;
                if (InventoryHelper.Same(p.inventoryData, sourceInv)) continue;
                if (p.inventoryData.isLocked) continue;
                targets.Add(new TransferTarget(p.inventoryData, p));
            }

            return targets;
        }

        private static void ExecuteStackSplitTransfer(
            BasicItemUI ui,
            ItemData item,
            ItemAttr attr,
            InventoryData sourceInv,
            bool isAlt,
            bool transferOutToPlayer,
            List<TransferTarget> targets,
            bool notifyFull,
            HumanCharacterController player,
            GameController gc)
        {
            float total = item.itemNumberFloat;
            float keep = isAlt ? (total - 1f) : Mathf.Floor(total * 0.5f);
            if (keep < 1f && total >= 2f) keep = 1f;

            float moveCount = Mathf.Clamp(total - keep, 0f, total);
            if (moveCount <= 0.0001f) return;

            ItemData moveData = item.CopyItemData(null);
            if (moveData == null) return;
            moveData.itemNumberFloat = moveCount;

            float totalMoved = 0f;
            bool strictFilterBlocked = false;
            var touchedTargets = new List<(TransferTarget target, Vector2Int? highlightCoord)>();

            if (transferOutToPlayer)
            {
                bool ok = gc.TryAddItemToCharacter(player, moveData, true, false);
                totalMoved = ok ? moveCount : Mathf.Clamp(moveCount - moveData.itemNumberFloat, 0f, moveCount);
            }
            else if (targets != null && targets.Count > 0)
            {
                float remaining = moveCount;
                for (int i = 0; i < targets.Count && remaining > 0.0001f; i++)
                {
                    var t = targets[i];
                    var targetInv = t.Inventory;
                    if (!CanAcceptTransferItem(targetInv, moveData, attr, out bool blockedByFilter))
                    {
                        if (blockedByFilter) strictFilterBlocked = true;
                        continue;
                    }

                    targetInv.RefreshOccupyData();
                    bool partiallyAdded = false;
                    bool addedAll = targetInv.TryAddItem(moveData, out partiallyAdded, true, true);
                    if (addedAll)
                    {
                        totalMoved += remaining;
                        remaining = 0f;
                        touchedTargets.Add((t, moveData.inventoryCoordinate));
                        break;
                    }

                    if (partiallyAdded)
                    {
                        float delta = Mathf.Clamp(remaining - moveData.itemNumberFloat, 0f, remaining);
                        if (delta > 0.0001f)
                        {
                            totalMoved += delta;
                            remaining = moveData.itemNumberFloat;
                            touchedTargets.Add((t, null));
                        }
                    }
                    else if (IsBlockedByStrictFilter(targetInv, moveData, attr))
                    {
                        strictFilterBlocked = true;
                    }
                }
            }

            totalMoved = Mathf.Clamp(totalMoved, 0f, total);
            if (totalMoved > 0.0001f)
            {
                item.itemNumberFloat = Mathf.Max(0f, total - totalMoved);
                ui.FinishPartialQuickTransfer(true);

                for (int i = 0; i < touchedTargets.Count; i++)
                {
                    RefreshTargetInventoryPanels(touchedTargets[i].target, touchedTargets[i].highlightCoord);
                }
                if (transferOutToPlayer || touchedTargets.Count > 0)
                    InventoryHelper.RefreshAllCarriedWeights();
            }
            else if (notifyFull)
            {
                if (strictFilterBlocked && targets != null && targets.Count == 1)
                    NotifyFilterBlocked(targets[0].Inventory);
                else
                    ShowContainerFullNotification();
            }
        }

        private static void ExecuteFoodGroupSplitTransfer(
            BasicItemUI ui,
            ItemData anchorItem,
            ItemAttr attr,
            InventoryData sourceInv,
            bool isAlt,
            bool transferOutToPlayer,
            List<TransferTarget> targets,
            bool notifyFull,
            HumanCharacterController player,
            GameController gc)
        {
            var aliveMembers = BuildGroupTransferSnapshot(ui, anchorItem, sourceInv);
            if (aliveMembers.Count <= 1) return;

            int totalGroup = aliveMembers.Count;
            int keepGroup = isAlt ? (totalGroup - 1) : (totalGroup / 2);
            if (keepGroup < 1) keepGroup = 1;
            int moveTarget = Math.Max(1, totalGroup - keepGroup);

            int movedCount = 0;
            bool strictFilterBlocked = false;
            var touchedTargets = new Dictionary<IntPtr, (TransferTarget target, Vector2Int? highlightCoord)>();

            for (int i = 0; i < aliveMembers.Count && movedCount < moveTarget; i++)
            {
                var m = aliveMembers[i];
                Vector2Int origCoord = m.inventoryCoordinate;
                bool origRot = m.rotateFlag;

                if (transferOutToPlayer)
                {
                    bool added = gc.TryAddItemToCharacter(player, m, movedCount == 0, false);
                    if (added)
                    {
                        sourceInv.RemoveItem(m, true);
                        movedCount++;
                    }
                    else
                    {
                        m.inventoryCoordinate = origCoord;
                        m.rotateFlag = origRot;
                        break;
                    }
                }
                else if (targets != null && targets.Count > 0)
                {
                    bool placed = false;
                    for (int j = 0; j < targets.Count; j++)
                    {
                        var t = targets[j];
                        var targetInv = t.Inventory;
                        if (!CanAcceptTransferItem(targetInv, m, attr, out bool blockedByFilter))
                        {
                            if (blockedByFilter) strictFilterBlocked = true;
                            continue;
                        }

                        targetInv.RefreshOccupyData();
                        bool partiallyAdded = false;
                        if (targetInv.TryAddItem(m, out partiallyAdded, true, true))
                        {
                            sourceInv.RemoveItem(m, true);
                            if (m.inventoryData != null)
                                LootInventoryPanel.CloseIfShowing(m.inventoryData);

                            movedCount++;
                            placed = true;
                            IntPtr tp = InventoryHelper.Ptr(targetInv);
                            if (tp != IntPtr.Zero)
                                touchedTargets[tp] = (t, m.inventoryCoordinate);
                            break;
                        }

                        m.inventoryCoordinate = origCoord;
                        m.rotateFlag = origRot;
                        if (IsBlockedByStrictFilter(targetInv, m, attr))
                            strictFilterBlocked = true;
                    }

                    if (!placed)
                    {
                        m.inventoryCoordinate = origCoord;
                        m.rotateFlag = origRot;
                        break;
                    }
                }
            }

            if (movedCount > 0)
            {
                ui.FinishPartialQuickTransfer(true);

                foreach (var kv in touchedTargets)
                {
                    RefreshTargetInventoryPanels(kv.Value.target, kv.Value.highlightCoord);
                }
                InventoryHelper.RefreshAllCarriedWeights();
            }
            else if (notifyFull)
            {
                if (strictFilterBlocked && targets != null && targets.Count == 1)
                    NotifyFilterBlocked(targets[0].Inventory);
                else
                    ShowContainerFullNotification();
            }
        }

        private static List<ItemData> BuildGroupTransferSnapshot(
            BasicItemUI ui,
            ItemData anchorItem,
            InventoryData sourceInv)
        {
            var others = new List<ItemData>();
            var group = ui.foodGroupMembers;
            var sourceList = sourceInv.itemList;
            if (group == null || sourceList == null)
                return others;

            var aliveSet = new HashSet<IntPtr>();
            for (int i = 0; i < sourceList.Count; i++)
            {
                IntPtr p = InventoryHelper.Ptr(sourceList[i]);
                if (p != IntPtr.Zero) aliveSet.Add(p);
            }

            bool protectFav = FavoriteGuardHelper.FavoritesEnabled
                && Plugin.ProtectFromQuickTransfer != null
                && Plugin.ProtectFromQuickTransfer.Value;
            bool containerProtected = protectFav && ContainerRuleStore.IsContainerProtected(sourceInv);

            ItemData anchorAlive = null;
            IntPtr anchorPtr = InventoryHelper.Ptr(anchorItem);
            var seenMember = new HashSet<IntPtr>();

            for (int i = 0; i < group.Count; i++)
            {
                var m = group[i];
                IntPtr mp = InventoryHelper.Ptr(m);
                if (mp == IntPtr.Zero || !aliveSet.Contains(mp) || !seenMember.Add(mp))
                    continue;
                if (!string.IsNullOrEmpty(m.combinationPassword))
                    continue;
                if (protectFav && (containerProtected || ItemFavoriteManager.IsFavorite(m)))
                    continue;

                if (mp == anchorPtr)
                    anchorAlive = m;
                else
                    others.Add(m);
            }

            // 把原图标绑定的 anchor 放回末尾，使拆分优先从其他成员取走，原格图标保持稳定
            if (anchorAlive != null)
                others.Add(anchorAlive);

            return others;
        }

        private static bool CanAcceptTransferItem(
            InventoryData targetInv,
            ItemData item,
            ItemAttr attr,
            out bool blockedByFilter)
        {
            blockedByFilter = false;
            if (targetInv == null || item == null || targetInv.isLocked)
                return false;

            if (item.inventoryData != null)
            {
                if (InventoryHelper.Same(item.inventoryData, targetInv)) return false;
                if (InventoryHelper.IsInventoryInsideItem(item, targetInv)) return false;
            }

            if (attr != null && targetInv.WouldCreateBackpackCycle(item, attr))
                return false;

            if (attr != null && !targetInv.PassesFeatureLimit(attr))
            {
                if (IsBlockedByStrictFilter(targetInv, item, attr))
                    blockedByFilter = true;
                return false;
            }

            if (IsBlockedByStrictFilter(targetInv, item, attr))
            {
                blockedByFilter = true;
                return false;
            }

            return true;
        }

        private static bool IsBlockedByStrictFilter(InventoryData targetInv, ItemData item, ItemAttr attr)
        {
            if (Plugin.StrictFilterOnManualDrop == null || !Plugin.StrictFilterOnManualDrop.Value)
                return false;
            if (GameInputManager.GetKey(KeyCode.LeftShift))
                return false;
            return !ContainerRuleStore.Accepts(targetInv, item, attr);
        }

        private static void RefreshTargetInventoryPanels(TransferTarget target, Vector2Int? highlightCoord)
        {
            if (target == null || target.Inventory == null) return;

            try
            {
                target.Inventory.RefreshTotalItemWeight();
                target.Inventory.RefreshOccupyData();

                bool refreshedKnown = false;
                if (target.Panel != null && target.Panel.gameObject != null && target.Panel.gameObject.activeInHierarchy)
                {
                    if (highlightCoord.HasValue)
                        target.Panel.HighlightCellAfterRefresh(highlightCoord.Value);
                    target.Panel.Refresh();
                    refreshedKnown = true;
                }

                var activePanels = InventoryPanel.GetActivePanels();
                if (activePanels == null) return;

                IntPtr knownPtr = InventoryHelper.Ptr(target.Panel);
                for (int i = 0; i < activePanels.Length; i++)
                {
                    var p = activePanels[i];
                    if (p == null || p.inventoryData == null) continue;
                    if (!InventoryHelper.Same(p.inventoryData, target.Inventory)) continue;
                    if (refreshedKnown && InventoryHelper.Ptr(p) == knownPtr) continue;

                    if (highlightCoord.HasValue)
                        p.HighlightCellAfterRefresh(highlightCoord.Value);
                    p.Refresh();
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("split_refresh_target", ex);
            }
        }

        private static void NotifyFilterBlocked(InventoryData targetInv)
        {
            string name = InventoryHelper.GetContainerDisplayName(targetInv);
            Plugin.Notify(
                $"「{name}」过滤规则拒绝放入该物品（按住 LeftShift 可临时绕过）",
                $"Filter on '{name}' blocked item (hold LeftShift to bypass)",
                playSound: true);
        }

        private static void ShowContainerFullNotification()
        {
            try
            {
                if (SystemNotificationPanel._instance == null) return;
                string msg = null;
                try
                {
                    msg = TextManager.instance?.GetText(2014);
                }
                catch
                {
                    msg = null;
                }

                if (string.IsNullOrEmpty(msg))
                    msg = Plugin.L("目标容器空间不足", "Container is full");

                SystemNotificationPanel.ShowNotificationSafe(msg, true);
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("split_full_notify", ex);
            }
        }
    }
}
