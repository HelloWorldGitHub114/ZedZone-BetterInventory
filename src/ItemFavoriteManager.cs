using System;
using UnityEngine;
using UnityEngine.UI;

namespace ZedZone.BetterInventory
{
    /// <summary>
    /// 物品收藏状态管理与图标角标绘制：状态持久化在 ItemData.itemPropertyPairs 中，角标独立于原版密码锁图标。
    /// </summary>
    internal static class ItemFavoriteManager
    {
        internal const string FavoritePropertyKey = "bi_fav";
        internal const string MagazineFavoritePropertyKey = "bi_mag_fav";
        internal const string BatteryFavoritePropertyKey = "bi_bat_fav";
        internal const string MeleeAttachmentFavoritePropertyKey = "bi_melee_att_fav";
        internal const string GunPartFavoriteKeyPrefix = "bi_gp_";
        internal const string BatteryBoxSlotFavoriteKeyPrefix = "bi_bb_";
        private const string FavoriteFlagValue = "1";
        private const string BadgeObjectName = "BetterInv_FavBadge";

        internal static string GetGunPartFavoriteKey(GunPartType partType) => GunPartFavoriteKeyPrefix + (int)partType;

        internal static string GetGunPartFavoriteKeyByIdIndex(int idIdx)
        {
            return idIdx switch
            {
                8 => GunPartFavoriteKeyPrefix + (int)GunPartType.Barrel,
                10 => GunPartFavoriteKeyPrefix + (int)GunPartType.Aimer,
                12 => GunPartFavoriteKeyPrefix + (int)GunPartType.Gunstock,
                _ => null
            };
        }

        internal static string GetBatteryBoxSlotKey(int slotIndex) => BatteryBoxSlotFavoriteKeyPrefix + slotIndex;

        internal static bool GetStoredSlotFavorite(ItemData hostItem, string slotKey)
        {
            if (hostItem == null || string.IsNullOrEmpty(slotKey)) return false;
            try
            {
                return string.Equals(hostItem.GetProperty(slotKey), FavoriteFlagValue, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        internal static void SetStoredSlotFavorite(ItemData hostItem, string slotKey, bool favorite)
        {
            if (hostItem == null || string.IsNullOrEmpty(slotKey)) return;
            try
            {
                if (favorite)
                    hostItem.SetProperty(slotKey, FavoriteFlagValue);
                else
                    InventoryHelper.RemoveItemProperty(hostItem, slotKey);
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("set_slot_fav_" + slotKey, ex);
            }
        }

        private static Sprite s_starSprite;
        private static Texture2D s_starTexture;

        internal static bool IsFavorite(ItemData item)
        {
            if (item == null) return false;
            try
            {
                return string.Equals(item.GetProperty(FavoritePropertyKey), FavoriteFlagValue, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        internal static void SetFavorite(ItemData item, bool favorite)
        {
            if (item == null) return;
            try
            {
                if (favorite)
                    item.SetProperty(FavoritePropertyKey, FavoriteFlagValue);
                else
                    InventoryHelper.RemoveItemProperty(item, FavoritePropertyKey);
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("set_fav", ex);
            }
        }

        internal static bool IsUIFavorited(BasicItemUI ui)
        {
            if (ui == null) return false;
            if (IsFavorite(ui.itemdata)) return true;

            var group = ui.foodGroupMembers;
            if (group != null && group.Count > 0)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    if (IsFavorite(group[i])) return true;
                }
            }

            var dragGroup = ui.draggingGroupMembers;
            if (dragGroup != null && dragGroup.Count > 0)
            {
                for (int i = 0; i < dragGroup.Count; i++)
                {
                    if (IsFavorite(dragGroup[i])) return true;
                }
            }

            return false;
        }

        internal static bool HasAnyFavoritedMultiDragMember(BasicItemUI ui)
        {
            if (ui == null) return false;
            var members = ui.multiDragMembers;
            if (members == null || members.Count == 0) return false;

            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                if (m != null && IsFavorite(m.item))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 切换指定 BasicItemUI（及食物/工具分组内所有成员）的收藏状态并立即刷新视觉与提示。
        /// </summary>
        internal static bool ToggleFavorite(BasicItemUI ui, bool notify = true)
        {
            if (ui == null || ui.itemdata == null) return false;

            bool nextState = !IsUIFavorited(ui);
            SetFavorite(ui.itemdata, nextState);

            var group = ui.foodGroupMembers;
            if (group != null && group.Count > 0)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    if (group[i] != null)
                        SetFavorite(group[i], nextState);
                }
            }

            RefreshBadge(ui);

            if (notify)
            {
                string itemName = InventoryHelper.GetItemDisplayName(ui.itemdata);
                if (nextState)
                    Plugin.Notify($"★ 已收藏：{itemName}", $"★ Favorited: {itemName}");
                else
                    Plugin.Notify($"☆ 已取消收藏：{itemName}", $"☆ Unfavorited: {itemName}");
            }

            return nextState;
        }

        internal static void RefreshBadge(BasicItemUI ui)
        {
            if (ui == null) return;

            bool show = Plugin.EnableFavorites != null
                        && Plugin.EnableFavorites.Value
                        && ui.itemdata != null
                        && IsUIFavorited(ui);

            Transform existing = ui.transform != null ? ui.transform.Find(BadgeObjectName) : null;
            if (!show)
            {
                if (existing != null && existing.gameObject.activeSelf)
                    existing.gameObject.SetActive(false);
                return;
            }

            GameObject badgeGO = existing != null ? existing.gameObject : CreateBadge(ui);
            if (badgeGO == null) return;

            if (!badgeGO.activeSelf)
                badgeGO.SetActive(true);

            // 保持星标始终显示在物品图标之上
            badgeGO.transform.SetAsLastSibling();
        }

        internal static void HideBadge(BasicItemUI ui)
        {
            if (ui == null || ui.transform == null) return;
            Transform existing = ui.transform.Find(BadgeObjectName);
            if (existing != null && existing.gameObject.activeSelf)
                existing.gameObject.SetActive(false);
        }

        private static GameObject CreateBadge(BasicItemUI ui)
        {
            try
            {
                var go = new GameObject(BadgeObjectName);
                var rt = go.AddComponent<RectTransform>();
                rt.SetParent(ui.transform, false);
                rt.anchorMin = new Vector2(1f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(-2f, -2f);
                rt.sizeDelta = new Vector2(14f, 14f);
                rt.localScale = Vector3.one;

                var img = go.AddComponent<Image>();
                img.sprite = GetOrCreateStarSprite();
                img.color = Color.white;
                img.raycastTarget = false;
                img.preserveAspect = true;
                return go;
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("create_fav_badge", ex);
                return null;
            }
        }

        /// <summary>
        /// 运行时生成一张 24x24 带深色描边的金色五角星精灵，零外部贴图依赖，内存占用约 2 KB。
        /// </summary>
        private static Sprite GetOrCreateStarSprite()
        {
            if (s_starSprite != null) return s_starSprite;

            const int size = 24;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;
            float outerR = 10.2f;
            float innerR = 4.3f;

            // 预计算五角星 10 个顶点（顶点朝上）
            Vector2[] poly = new Vector2[10];
            for (int i = 0; i < 10; i++)
            {
                float angle = (90f + i * 36f) * Mathf.Deg2Rad;
                float r = (i % 2 == 0) ? outerR : innerR;
                poly[i] = new Vector2(cx + Mathf.Cos(angle) * r, cy + Mathf.Sin(angle) * r);
            }

            Color32 goldTop = new Color32(255, 232, 102, 255);
            Color32 goldBot = new Color32(245, 158, 11, 255);
            Color32 borderCol = new Color32(45, 24, 5, 235);
            Color32 clear = new Color32(0, 0, 0, 0);

            bool[,] inside = new bool[size, size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                    inside[x, y] = PointInPolygon(x, y, poly);
            }

            for (int y = 0; y < size; y++)
            {
                float t = y / (float)(size - 1);
                Color32 fill = Color32.Lerp(goldBot, goldTop, t);

                for (int x = 0; x < size; x++)
                {
                    if (inside[x, y])
                    {
                        tex.SetPixel(x, y, fill);
                        continue;
                    }

                    // 1px 外描边，确保在浅色或深色物品图标上都清晰可见
                    bool near = false;
                    for (int dy = -1; dy <= 1 && !near; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            int ny = y + dy;
                            if (nx >= 0 && nx < size && ny >= 0 && ny < size && inside[nx, ny])
                            {
                                near = true;
                                break;
                            }
                        }
                    }

                    tex.SetPixel(x, y, near ? borderCol : clear);
                }
            }

            tex.Apply(false, false);
            UnityEngine.Object.DontDestroyOnLoad(tex);

            s_starTexture = tex;
            s_starSprite = Sprite.Create(
                tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f),
                100f);
            UnityEngine.Object.DontDestroyOnLoad(s_starSprite);
            return s_starSprite;
        }

        private static bool PointInPolygon(float px, float py, Vector2[] poly)
        {
            bool inside = false;
            int n = poly.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = poly[i].x, yi = poly[i].y;
                float xj = poly[j].x, yj = poly[j].y;

                bool intersect = ((yi > py) != (yj > py))
                                 && (px < (xj - xi) * (py - yi) / (yj - yi + 1e-6f) + xi);
                if (intersect) inside = !inside;
            }
            return inside;
        }
    }
}
