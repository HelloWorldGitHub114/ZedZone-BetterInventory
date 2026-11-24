using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using ZedZone.Net;

namespace ZedZone.BetterInventory
{
    /// <summary>
    /// 补全原版框选多物品拖拽（BasicItemUI.DropOn_Multi）漏接“拖进闭合背包图标”的路径：
    /// 松手时若鼠标位于有效背包物品图标上，逐个校验防套娃死循环与过滤规则并批量收入目标背包，
    /// 空间不足或不可放入的物品全部安全保留在原格位，不丢件、不复制。
    /// </summary>
    [HarmonyPatch(typeof(BasicItemUI), nameof(BasicItemUI.DropOn_Multi),
        typeof(PointerEventData), typeof(ItemAttr), typeof(GameObject))]
    internal static class Patch_BasicItemUI_DropOn_Multi_IntoBag
    {
        [HarmonyPrefix]
        private static bool Prefix(
            BasicItemUI __instance,
            PointerEventData eventData,
            ItemAttr itemattr,
            GameObject dropObj,
            ref bool __result)
        {
            if (Plugin.EnableMultiDragIntoBag == null || !Plugin.EnableMultiDragIntoBag.Value)
                return true;
            if (__instance == null || dropObj == null)
                return true;
            if (__instance.multiDropBlockedFrame == Time.frameCount)
                return true;

            var members = __instance.multiDragMembers;
            if (members == null || members.Count == 0)
                return true;

            var anchor = __instance.itemdata;
            var sourcePanel = __instance.inventoryPanel;
            var sourceInv = sourcePanel != null ? sourcePanel.inventoryData : null;
            if (anchor == null || sourceInv == null)
                return true;

            try
            {
                var targetUI = dropObj.GetComponentInParent<BasicItemUI>();
                if (targetUI == null || InventoryHelper.Same(targetUI, __instance))
                    return true;

                var targetBagItem = targetUI.itemdata;
                if (targetBagItem == null || targetBagItem.inventoryData == null)
                    return true;

                // 与原版 TryDropIntoBackpackItem 保持一致：拒绝带密码锁的背包与非背包类型物品
                if (!string.IsNullOrEmpty(targetBagItem.combinationPassword))
                    return true;

                var targetBagAttr = InventoryHelper.GetAttr(targetBagItem.itemId);
                if (targetBagAttr == null || targetBagAttr.itemType != ItemType.Backpack)
                    return true;

                var targetBagInv = targetBagItem.inventoryData;
                if (targetBagInv.isLocked || InventoryHelper.Same(targetBagInv, sourceInv))
                    return true;

                // 联机客户端下地面未拾取背包需走服务端单件 RPC，此处跳过避免本地状态与房主不同步
                if (NetworkManager.isClient &&
                    targetUI.inventoryPanel != null &&
                    targetUI.inventoryPanel.TryCast<NearbyItemInventoryPanel>() != null)
                {
                    return true;
                }

                // 若目标背包本身就是本次框选拖动列表中的一员，不能往自己身上拖
                IntPtr targetBagItemPtr = InventoryHelper.Ptr(targetBagItem);
                if (targetBagItemPtr == InventoryHelper.Ptr(anchor))
                    return true;
                for (int i = 0; i < members.Count; i++)
                {
                    if (InventoryHelper.Ptr(members[i].item) == targetBagItemPtr)
                        return true;
                }

                // 是否启用了“手动拖拽严格过滤”（按住 LeftShift 可临时绕过）
                bool strictFilter = Plugin.StrictFilterOnManualDrop != null
                    && Plugin.StrictFilterOnManualDrop.Value
                    && !GameInputManager.GetKey(KeyCode.LeftShift);

                // 先快照成员列表，避免迭代过程中修改原集合
                var snapshot = new List<BasicItemUI.MultiDragMember>(members.Count);
                for (int i = 0; i < members.Count; i++)
                    snapshot.Add(members[i]);

                int totalCount = 1 + snapshot.Count;
                int placedCount = 0;
                int partialStackedCount = 0;
                int blockedByFilter = 0;

                // 1. 尝试放入锚点物品（主拖动物品）
                bool anchorPlaced = TryMoveOneItemIntoBag(
                    anchor, sourceInv, targetBagInv, strictFilter,
                    ref partialStackedCount, ref blockedByFilter);
                if (anchorPlaced)
                    placedCount++;

                // 2. 逐个尝试放入其余框选成员；未放进去的留在 remaining，由 OnEndDrag 原生 RestoreMultiMembers 精确归位
                var remaining = new List<BasicItemUI.MultiDragMember>(snapshot.Count);
                for (int i = 0; i < snapshot.Count; i++)
                {
                    var m = snapshot[i];
                    if (m.item != null && TryMoveOneItemIntoBag(
                            m.item, sourceInv, targetBagInv, strictFilter,
                            ref partialStackedCount, ref blockedByFilter))
                    {
                        placedCount++;
                    }
                    else
                    {
                        remaining.Add(m);
                    }
                }

                // 没有任何物品成功放入或部分堆叠：阻断同帧重复判定并交回原版完整回滚
                if (placedCount == 0 && partialStackedCount == 0)
                {
                    __instance.multiDropBlockedFrame = Time.frameCount;
                    string bagNameFail = InventoryHelper.GetItemDisplayName(targetBagItem);
                    if (blockedByFilter > 0)
                    {
                        Plugin.Notify(
                            $"「{bagNameFail}」过滤规则拒绝放入所选物品（按住 Shift 可临时绕过）",
                            $"Filter on '{bagNameFail}' blocked selected items (hold Shift to bypass)");
                    }
                    else
                    {
                        Plugin.Notify(
                            $"「{bagNameFail}」空间不足或物品无法放入",
                            $"Not enough space or incompatible items for '{bagNameFail}'");
                    }

                    __result = false;
                    return false;
                }

                // 3. 清理/归位锚点状态（若发生部分堆叠，保留剩余数量，只恢复坐标与旋转避免复制堆叠物）
                if (anchorPlaced)
                {
                    __instance.itemdataTemp = null;
                    __instance.itemdata = null;
                }
                else
                {
                    if (__instance.itemdataTemp != null)
                    {
                        anchor.inventoryCoordinate = __instance.itemdataTemp.inventoryCoordinate;
                        anchor.rotateFlag = __instance.itemdataTemp.rotateFlag;
                        __instance.itemdataTemp = null;
                    }
                    sourceInv.RestoreOrDrop(anchor);
                }

                // 4. 仅把未放入的成员回写进 multiDragMembers，供 OnEndDrag 紧接着自动恢复到原格位与旋转状态
                members.Clear();
                for (int i = 0; i < remaining.Count; i++)
                    members.Add(remaining[i]);

                // 5. 刷新双方容器格位、负重与界面
                sourceInv.RefreshOccupyData();
                sourceInv.RefreshTotalItemWeight();
                targetBagInv.RefreshOccupyData();
                targetBagInv.RefreshTotalItemWeight();
                RefreshPlayerWeightsIfNeeded();

                sourcePanel.Refresh();
                if (targetUI.inventoryPanel != null && !InventoryHelper.Same(targetUI.inventoryPanel, sourcePanel))
                    targetUI.inventoryPanel.Refresh();

                var lootPanel = LootInventoryPanel._instance;
                if (lootPanel != null &&
                    lootPanel.gameObject.activeInHierarchy &&
                    InventoryHelper.Same(LootInventoryPanel.CurrentOpenInventoryData, targetBagInv))
                {
                    lootPanel.Refresh();
                }

                __instance.multiDropBlockedFrame = Time.frameCount;

                string bagName = InventoryHelper.GetItemDisplayName(targetBagItem);
                if (placedCount == totalCount)
                {
                    Plugin.Notify(
                        $"已将 {placedCount} 件物品放入「{bagName}」",
                        $"Moved {placedCount} item(s) into '{bagName}'");
                }
                else
                {
                    Plugin.Notify(
                        $"已放入 {placedCount}/{totalCount} 件到「{bagName}」，其余保留原位",
                        $"Moved {placedCount}/{totalCount} item(s) into '{bagName}'; remaining restored");
                }

                Plugin.LogVerbose($"MultiDrag into bag '{bagName}': placed={placedCount}/{totalCount}, partial={partialStackedCount}, filterBlocked={blockedByFilter}");
                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("multidrag_into_bag", ex);
                return true;
            }
        }

        private static bool TryMoveOneItemIntoBag(
            ItemData item,
            InventoryData sourceInv,
            InventoryData targetBagInv,
            bool strictFilter,
            ref int partialStackedCount,
            ref int blockedByFilter)
        {
            if (item == null || targetBagInv == null) return false;

            var attr = InventoryHelper.GetAttr(item.itemId);

            // 防套娃死循环校验：禁止把目标背包自己或包含目标背包的外层包拖进去
            if (item.inventoryData != null)
            {
                if (InventoryHelper.Same(item.inventoryData, targetBagInv)) return false;
                if (InventoryHelper.IsInventoryInsideItem(item, targetBagInv)) return false;
            }
            if (attr != null && targetBagInv.WouldCreateBackpackCycle(item, attr))
                return false;

            // 原版功能标签容器限制
            if (attr != null && !targetBagInv.PassesFeatureLimit(attr))
                return false;

            // 可选严格容器过滤校验
            if (strictFilter && !ContainerRuleStore.Accepts(targetBagInv, item, attr))
            {
                blockedByFilter++;
                return false;
            }

            targetBagInv.RefreshOccupyData();
            bool partiallyAdded = false;
            bool added = targetBagInv.TryAddItem(item, out partiallyAdded, true, true);

            if (added || (partiallyAdded && item.itemNumberFloat <= 0.0001f))
            {
                // 防御性清理源容器残留引用，并关闭被收进包内的子背包浮窗
                sourceInv.RemoveItem(item, true);
                if (item.inventoryData != null)
                    LootInventoryPanel.CloseIfShowing(item.inventoryData);
                return true;
            }

            if (partiallyAdded)
                partialStackedCount++;

            return false;
        }

        private static void RefreshPlayerWeightsIfNeeded()
        {
            var cd = InventoryHelper.LocalPlayer?.characterData;
            if (cd == null) return;
            cd.inventoryData?.RefreshTotalItemWeight();
            var eq = cd.characterEquipmentData;
            if (eq == null) return;
            RefreshClothListWeight(eq.torseItemList);
            RefreshClothListWeight(eq.legItemList);
        }

        private static void RefreshClothListWeight(Il2CppSystem.Collections.Generic.List<ItemData> clothList)
        {
            if (clothList == null) return;
            for (int i = 0; i < clothList.Count; i++)
                clothList[i]?.inventoryData?.RefreshTotalItemWeight();
        }
    }
}
