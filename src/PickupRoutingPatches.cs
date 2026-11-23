using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ZedZone.BetterInventory
{
    /// <summary>
    /// 拾取优先级与容器过滤路由状态及辅助判定。
    /// </summary>
    internal static class PickupRoutingState
    {
        [ThreadStatic]
        internal static bool InCustomAdd;

        [ThreadStatic]
        internal static int QuickStackFilterDepth;

        /// <summary>
        /// 候选容器排序条目：按 优先级降序 -> 显式规则匹配优先于兜底 -> 已有同款堆叠优先 -> 原始顺序 稳定排序。
        /// </summary>
        internal readonly struct CandidateContainer
        {
            internal readonly InventoryData Inventory;
            internal readonly int Priority;
            internal readonly ContainerMatchKind MatchKind;
            internal readonly bool HasExistingStack;
            internal readonly int DiscoveryOrder;

            internal CandidateContainer(
                InventoryData inventory,
                int priority,
                ContainerMatchKind matchKind,
                bool hasExistingStack,
                int discoveryOrder)
            {
                Inventory = inventory;
                Priority = priority;
                MatchKind = matchKind;
                HasExistingStack = hasExistingStack;
                DiscoveryOrder = discoveryOrder;
            }
        }

        internal static int CompareCandidates(CandidateContainer a, CandidateContainer b)
        {
            // 1. 优先级越高越优先
            int cmp = b.Priority.CompareTo(a.Priority);
            if (cmp != 0) return cmp;

            // 2. 同优先级下，白名单/同类显式命中的专用容器优先于通用兜底容器
            cmp = ((int)b.MatchKind).CompareTo((int)a.MatchKind);
            if (cmp != 0) return cmp;

            // 3. 同级别下，若开启“优先合并已有堆叠”，已有该 itemId 的容器优先
            if (Plugin.PreferExistingStack != null && Plugin.PreferExistingStack.Value)
            {
                cmp = b.HasExistingStack.CompareTo(a.HasExistingStack);
                if (cmp != 0) return cmp;
            }

            // 4. 保持稳定发现顺序（主背包 -> 衣服口袋 -> 浅层套包 -> 深层套包）
            return a.DiscoveryOrder.CompareTo(b.DiscoveryOrder);
        }
    }

    /// <summary>
    /// 本地玩家拾取、一键拿取、快捷拿取统一汇流到 BasicCharacterController.AddItemToCharacter：
    /// 在此处接管容器排序与过滤规则，实现按优先级自动分拣到主背包、衣服口袋与多层套包。
    /// </summary>
    [HarmonyPatch(typeof(BasicCharacterController), nameof(BasicCharacterController.AddItemToCharacter),
        typeof(ItemData), typeof(bool))]
    internal static class Patch_BasicCharacterController_AddItemToCharacter_Routing
    {
        [HarmonyPrefix]
        private static bool Prefix(
            BasicCharacterController __instance,
            ItemData m_itemData,
            bool m_executeOnChange,
            ref bool __result)
        {
            if (PickupRoutingState.InCustomAdd) return true;
            if (Plugin.EnablePickupRouting == null || !Plugin.EnablePickupRouting.Value) return true;
            if (m_itemData == null || !InventoryHelper.IsLocalPlayer(__instance)) return true;

            var cd = __instance.characterData;
            if (cd == null) return true;

            try
            {
                bool includeNested = Plugin.RouteIntoNestedBags != null && Plugin.RouteIntoNestedBags.Value;
                var allInventories = InventoryHelper.CollectPlayerCarriedInventories(cd, includeNested);
                if (allInventories.Count == 0) return true;

                // 若没有任何容器设置过自定义规则，且未开启套包自动拾取，则直接走原版路径零开销放行
                bool anyCustomRule = false;
                for (int i = 0; i < allInventories.Count; i++)
                {
                    var r = ContainerRuleStore.GetRule(allInventories[i]);
                    if (r != null && !r.IsDefault)
                    {
                        anyCustomRule = true;
                        break;
                    }
                }

                if (!anyCustomRule && !includeNested)
                    return true;

                ItemAttr attr = InventoryHelper.GetAttr(m_itemData.itemId);
                var candidates = new List<PickupRoutingState.CandidateContainer>(allInventories.Count);

                for (int i = 0; i < allInventories.Count; i++)
                {
                    var inv = allInventories[i];
                    if (inv == null || inv.isLocked) continue;

                    // 防止背包套进自己或自己的子背包
                    if (InventoryHelper.IsInventoryInsideItem(m_itemData, inv)) continue;
                    if (attr != null && inv.WouldCreateBackpackCycle(m_itemData, attr)) continue;
                    if (attr != null && !inv.PassesFeatureLimit(attr)) continue;

                    ContainerMatchKind match = ContainerRuleStore.Evaluate(inv, m_itemData, attr);
                    if (match == ContainerMatchKind.Rejected) continue;

                    var rule = ContainerRuleStore.GetRule(inv);
                    int prio = rule != null ? rule.Priority : 0;
                    bool hasExisting = inv.HasItem(m_itemData.itemId, 1f);

                    candidates.Add(new PickupRoutingState.CandidateContainer(inv, prio, match, hasExisting, i));
                }

                if (candidates.Count == 0)
                {
                    __result = false;
                    return false;
                }

                candidates.Sort(PickupRoutingState.CompareCandidates);

                PickupRoutingState.InCustomAdd = true;
                try
                {
                    bool anyPartial = false;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        var inv = candidates[i].Inventory;
                        inv.RefreshOccupyData();

                        bool partiallyAdded = false;
                        if (inv.TryAddItem(m_itemData, out partiallyAdded, m_executeOnChange, true))
                        {
                            Plugin.LogVerbose(
                                $"Routed item {m_itemData.itemId} -> {InventoryHelper.GetContainerDisplayName(inv)} (P{candidates[i].Priority}, {candidates[i].MatchKind})");
                            __result = true;
                            return false;
                        }

                        if (partiallyAdded)
                        {
                            anyPartial = true;
                            if (m_itemData.itemNumberFloat <= 0.0001f)
                            {
                                __result = true;
                                return false;
                            }
                        }
                    }

                    // 全部候选容器已满或被过滤器拒绝，交给上层 TryAddItemToCharacter 触发原版自动整理/满包提示
                    __result = false;
                    if (anyPartial)
                        Plugin.LogVerbose($"Item {m_itemData.itemId} partially stacked; remainder={m_itemData.itemNumberFloat}");
                    return false;
                }
                finally
                {
                    PickupRoutingState.InCustomAdd = false;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("pickup_routing", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// 在一键堆叠（NearbyStorageLocator.QuickStack）执行期间拦截目标箱子的 TryAddItem，
    /// 让箱子上的自定义过滤规则也能生效，避免不匹配物品被一键塞进白/黑名单储物箱。
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_InventoryData_TryAddItem_QuickStackFilter
    {
        // TryAddItem(ItemData, out bool, bool, bool) 的第二个参数是 byref bool，使用显式 MethodBase 精准绑定
        [HarmonyTargetMethod]
        private static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(InventoryData),
                nameof(InventoryData.TryAddItem),
                new[] { typeof(ItemData), typeof(bool).MakeByRefType(), typeof(bool), typeof(bool) });
        }

        [HarmonyPrefix]
        private static bool Prefix(
            InventoryData __instance,
            ItemData m_itemdata,
            ref bool m_partiallyAddedFlag,
            ref bool __result)
        {
            if (PickupRoutingState.QuickStackFilterDepth <= 0) return true;
            if (Plugin.ApplyFilterOnQuickStack == null || !Plugin.ApplyFilterOnQuickStack.Value) return true;
            if (__instance == null || m_itemdata == null) return true;

            try
            {
                var attr = InventoryHelper.GetAttr(m_itemdata.itemId);
                if (!ContainerRuleStore.Accepts(__instance, m_itemdata, attr))
                {
                    m_partiallyAddedFlag = false;
                    __result = false;
                    return false;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("quickstack_filter", ex);
            }

            return true;
        }
    }

    /// <summary>
    /// 可选严格过滤模式：开启 StrictFilterOnManualDrop 后，手动拖拽物品放入容器也会校验容器过滤规则（按住 LeftShift 可临时绕过）。
    /// </summary>
    [HarmonyPatch(typeof(InventoryData), nameof(InventoryData.PassesFeatureLimit), typeof(ItemAttr))]
    internal static class Patch_InventoryData_PassesFeatureLimit_Strict
    {
        [HarmonyPostfix]
        private static void Postfix(InventoryData __instance, ItemAttr attr, ref bool __result)
        {
            if (!__result || __instance == null || attr == null) return;
            if (PickupRoutingState.InCustomAdd) return;
            if (Plugin.StrictFilterOnManualDrop == null || !Plugin.StrictFilterOnManualDrop.Value) return;

            try
            {
                if (GameInputManager.GetKey(KeyCode.LeftShift)) return;

                var rule = ContainerRuleStore.GetRule(__instance);
                if (rule == null || !rule.HasActiveFilter) return;

                // 用临时伪造/属性检查（此时无 ItemData 实例，仅按 itemId/itemType/itemFeatures 与已有同类判定）
                bool matchesCriteria = rule.ItemIds.Contains(attr.itemId) || rule.Types.Contains(attr.itemType);
                if (!matchesCriteria && rule.Features.Count > 0 && attr.itemFeatures != null)
                {
                    for (int i = 0; i < attr.itemFeatures.Count; i++)
                    {
                        if (rule.Features.Contains(attr.itemFeatures[i]))
                        {
                            matchesCriteria = true;
                            break;
                        }
                    }
                }

                bool matchesExisting = rule.MatchExistingTypesOnly
                                       && ContainerRuleStore.InventoryContainsItemId(__instance, attr.itemId);

                if (rule.Mode == ContainerFilterMode.Whitelist)
                {
                    if (!matchesCriteria && !matchesExisting) __result = false;
                }
                else if (rule.Mode == ContainerFilterMode.Blacklist)
                {
                    if (matchesCriteria || (rule.MatchExistingTypesOnly && !matchesExisting)) __result = false;
                }
                else if (rule.MatchExistingTypesOnly && !matchesExisting)
                {
                    __result = false;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("strict_manual_filter", ex);
            }
        }
    }
}
