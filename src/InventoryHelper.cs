using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace ZedZone.BetterInventory
{
    /// <summary>
    /// 容器与物品通用工具：处理 IL2CPP 指针判等、套包递归收集、容器归属定位以及中英文本地化名称。
    /// </summary>
    internal static class InventoryHelper
    {
        private const int MaxNestDepth = 12;

        internal static IntPtr Ptr(Il2CppObjectBase obj)
        {
            return obj == null ? IntPtr.Zero : IL2CPP.Il2CppObjectBaseToPtr(obj);
        }

        internal static bool Same(Il2CppObjectBase a, Il2CppObjectBase b)
        {
            IntPtr pa = Ptr(a);
            return pa != IntPtr.Zero && pa == Ptr(b);
        }

        internal static HumanCharacterController LocalPlayer
        {
            get
            {
                var gc = GameController.instance;
                return gc != null ? gc.playerCharacter : null;
            }
        }

        internal static bool IsLocalPlayer(BasicCharacterController character)
        {
            if (character == null) return false;
            var player = LocalPlayer;
            return player != null && Same(character, player);
        }

        /// <summary>
        /// 获取物品属性定义（安全容错，找不到返回 null）。
        /// </summary>
        internal static ItemAttr GetAttr(int itemId)
        {
            var mgr = ItemManager.instance;
            if (mgr == null || itemId < 0) return null;
            return mgr.FindItemAttrById(itemId) ?? mgr.GetItemAttrById(itemId);
        }

        internal static string GetItemDisplayName(int itemId)
        {
            var attr = GetAttr(itemId);
            if (attr != null)
            {
                string n = attr.ItemName;
                if (!string.IsNullOrEmpty(n)) return n;
            }
            return $"#{itemId}";
        }

        internal static string GetItemDisplayName(ItemData item)
        {
            if (item == null) return string.Empty;
            string custom = item.GetCustomName();
            if (!string.IsNullOrEmpty(custom)) return custom;
            return GetItemDisplayName(item.itemId);
        }

        /// <summary>
        /// 从 ItemData.itemPropertyPairs 中移除指定键，避免取消收藏或清空规则后在存档里残留空键值。
        /// </summary>
        internal static void RemoveItemProperty(ItemData item, string key)
        {
            if (item == null || string.IsNullOrEmpty(key)) return;
            var pairs = item.itemPropertyPairs;
            if (pairs == null || pairs.Count == 0) return;

            for (int i = pairs.Count - 1; i >= 0; i--)
            {
                var p = pairs[i];
                if (p != null && string.Equals(p.key, key, StringComparison.Ordinal))
                    pairs.RemoveAt(i);
            }
        }

        /// <summary>
        /// 收集角色身上所有可放置物品的容器（按原版顺序：主背包 -> 上衣口袋 -> 裤子口袋 -> 递归子背包）。
        /// </summary>
        internal static List<InventoryData> CollectPlayerCarriedInventories(CharacterData cd, bool includeNested)
        {
            var result = new List<InventoryData>();
            if (cd == null) return result;

            var visited = new HashSet<IntPtr>();

            void TryAddRoot(InventoryData inv)
            {
                if (inv == null) return;
                IntPtr p = Ptr(inv);
                if (p == IntPtr.Zero || !visited.Add(p)) return;
                result.Add(inv);
            }

            // 1. 主背包
            TryAddRoot(cd.inventoryData);

            // 2. 装备中的衣服口袋
            var eq = cd.characterEquipmentData;
            if (eq != null)
            {
                AddClothingPockets(eq.torseItemList, TryAddRoot);
                AddClothingPockets(eq.legItemList, TryAddRoot);
            }

            // 3. 递归子背包（BFS 保证浅层容器优先于深层容器）
            if (includeNested)
            {
                var queue = new Queue<(InventoryData inv, int depth)>();
                for (int i = 0; i < result.Count; i++)
                    queue.Enqueue((result[i], 0));

                while (queue.Count > 0)
                {
                    var (cur, depth) = queue.Dequeue();
                    if (depth >= MaxNestDepth) continue;
                    var items = cur.itemList;
                    if (items == null) continue;

                    for (int i = 0; i < items.Count; i++)
                    {
                        var subItem = items[i];
                        if (subItem == null) continue;
                        var subInv = subItem.inventoryData;
                        if (subInv == null) continue;
                        IntPtr sp = Ptr(subInv);
                        if (sp == IntPtr.Zero || !visited.Add(sp)) continue;

                        result.Add(subInv);
                        queue.Enqueue((subInv, depth + 1));
                    }
                }
            }

            return result;
        }

        private static void AddClothingPockets(
            Il2CppSystem.Collections.Generic.List<ItemData> clothList,
            Action<InventoryData> addAction)
        {
            if (clothList == null) return;
            for (int i = 0; i < clothList.Count; i++)
            {
                var cloth = clothList[i];
                if (cloth != null && cloth.inventoryData != null)
                    addAction(cloth.inventoryData);
            }
        }

        /// <summary>
        /// 判断候选容器是否位于待放入物品的子树内部，避免套娃死循环。
        /// </summary>
        internal static bool IsInventoryInsideItem(ItemData item, InventoryData candidate)
        {
            if (item == null || candidate == null || item.inventoryData == null) return false;
            IntPtr targetPtr = Ptr(candidate);
            if (targetPtr == IntPtr.Zero) return false;

            var visited = new HashSet<IntPtr>();
            var stack = new Stack<InventoryData>();
            stack.Push(item.inventoryData);

            int steps = 0;
            while (stack.Count > 0 && steps++ < 256)
            {
                var cur = stack.Pop();
                IntPtr cp = Ptr(cur);
                if (cp == IntPtr.Zero || !visited.Add(cp)) continue;
                if (cp == targetPtr) return true;

                var list = cur.itemList;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var child = list[i];
                    if (child != null && child.inventoryData != null)
                        stack.Push(child.inventoryData);
                }
            }

            return false;
        }

        /// <summary>
        /// 反查某个 InventoryData 对应的宿主 ItemData（如子背包、战术挂包、上衣/裤子口袋）；
        /// 主背包与家具储物箱无宿主物品，返回 null。
        /// </summary>
        internal static ItemData FindOwnerItemData(InventoryData inv)
        {
            if (inv == null) return null;
            IntPtr invPtr = Ptr(inv);
            if (invPtr == IntPtr.Zero) return null;

            // 先用原版缓存查询
            try
            {
                var info = InventoryOwnerLookup.GetOwner(inv);
                if (info.owner != null && Same(info.owner.inventoryData, inv))
                    return info.owner;
            }
            catch
            {
                // 缓存未就绪时走下方兜底扫描
            }

            var player = LocalPlayer;
            var cd = player?.characterData;
            if (cd == null) return null;

            // 主背包直接归属角色自身，不属于任何单件物品
            if (Same(cd.inventoryData, inv))
                return null;

            var eq = cd.characterEquipmentData;
            if (eq != null)
            {
                ItemData clothMatch = FindInListByInventory(eq.torseItemList, invPtr)
                                   ?? FindInListByInventory(eq.legItemList, invPtr);
                if (clothMatch != null) return clothMatch;
            }

            // 递归扫描携带容器中的所有物品
            var carried = CollectPlayerCarriedInventories(cd, includeNested: true);
            for (int i = 0; i < carried.Count; i++)
            {
                var list = carried[i]?.itemList;
                if (list == null) continue;
                for (int j = 0; j < list.Count; j++)
                {
                    var it = list[j];
                    if (it != null && Ptr(it.inventoryData) == invPtr)
                        return it;
                }
            }

            return null;
        }

        private static ItemData FindInListByInventory(
            Il2CppSystem.Collections.Generic.List<ItemData> list,
            IntPtr targetInvPtr)
        {
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it != null && Ptr(it.inventoryData) == targetInvPtr)
                    return it;
            }
            return null;
        }

        /// <summary>
        /// 判断一个容器是否属于本地玩家的随身容器（主背包、衣服口袋或随身套包）。
        /// </summary>
        internal static bool IsPlayerMainInventory(InventoryData inv)
        {
            var cd = LocalPlayer?.characterData;
            return cd != null && inv != null && Same(cd.inventoryData, inv);
        }

        /// <summary>
        /// 获取容器在界面上展示的可读标题。
        /// </summary>
        internal static string GetContainerDisplayName(InventoryData inv, string panelTitle = null)
        {
            if (!string.IsNullOrWhiteSpace(panelTitle))
                return panelTitle.Trim();

            if (inv == null)
                return Plugin.L("容器", "Container");

            if (IsPlayerMainInventory(inv))
                return Plugin.L("主背包", "Main Backpack");

            var owner = FindOwnerItemData(inv);
            if (owner != null)
                return GetItemDisplayName(owner);

            if (!string.IsNullOrWhiteSpace(inv.inventoryTitleName))
                return inv.inventoryTitleName.Trim();

            return Plugin.L("容器", "Container");
        }

        /// <summary>
        /// 物品大类（ItemType）中英文短名称，供过滤面板展示。
        /// </summary>
        internal static string GetItemTypeLabel(ItemType type)
        {
            if (Plugin.IsChinese())
            {
                return type switch
                {
                    ItemType.Food => "食物/药品",
                    ItemType.Ammo => "弹药",
                    ItemType.Arrow => "箭矢",
                    ItemType.Magazine => "弹匣",
                    ItemType.GunPart => "枪械配件",
                    ItemType.RangedWeapon => "远程武器",
                    ItemType.MeleeWeapon => "近战武器",
                    ItemType.Throwable => "投掷物",
                    ItemType.Material => "制作材料",
                    ItemType.Clothing => "服装护甲",
                    ItemType.FaceMask => "面罩口罩",
                    ItemType.Cloak => "披风",
                    ItemType.Backpack => "背包容器",
                    ItemType.Container => "液体容器",
                    ItemType.Deployable => "可部署物",
                    ItemType.Liqiud => "液体",
                    _ => type.ToString()
                };
            }

            return type switch
            {
                ItemType.Food => "Food / Meds",
                ItemType.Ammo => "Ammo",
                ItemType.Arrow => "Arrows",
                ItemType.Magazine => "Magazines",
                ItemType.GunPart => "Gun Parts",
                ItemType.RangedWeapon => "Firearms",
                ItemType.MeleeWeapon => "Melee",
                ItemType.Throwable => "Throwables",
                ItemType.Material => "Materials",
                ItemType.Clothing => "Clothing",
                ItemType.FaceMask => "Masks",
                ItemType.Cloak => "Cloaks",
                ItemType.Backpack => "Backpacks",
                ItemType.Container => "Containers",
                ItemType.Deployable => "Deployables",
                ItemType.Liqiud => "Liquids",
                _ => type.ToString()
            };
        }

        /// <summary>
        /// 常用功能标签（ItemFeatureType）中英文短名称，便于跨大类精细分拣。
        /// </summary>
        internal static string GetFeatureLabel(ItemFeatureType feature)
        {
            if (Plugin.IsChinese())
            {
                return feature switch
                {
                    ItemFeatureType.Bandage => "医疗/绷带",
                    ItemFeatureType.Battery => "电池电源",
                    ItemFeatureType.Combustible => "燃料可燃",
                    ItemFeatureType.AccessCard => "门禁钥匙卡",
                    ItemFeatureType.LightingTool => "照明工具",
                    ItemFeatureType.PickLock => "开锁工具",
                    ItemFeatureType.BreakDoor => "破门工具",
                    ItemFeatureType.Crop => "农作/种子",
                    ItemFeatureType.PetFood => "宠物食品",
                    _ => feature.ToString()
                };
            }

            return feature switch
            {
                ItemFeatureType.Bandage => "Bandage/Med",
                ItemFeatureType.Battery => "Battery",
                ItemFeatureType.Combustible => "Fuel",
                ItemFeatureType.AccessCard => "Keycard",
                ItemFeatureType.LightingTool => "Lighting",
                ItemFeatureType.PickLock => "Lockpick",
                ItemFeatureType.BreakDoor => "Breach Tool",
                ItemFeatureType.Crop => "Crops/Seeds",
                ItemFeatureType.PetFood => "Pet Food",
                _ => feature.ToString()
            };
        }
    }
}
