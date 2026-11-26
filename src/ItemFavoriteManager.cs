using System;
using UnityEngine;
using UnityEngine.UI;

namespace ZedZone.BetterInventory
{
    /// <summary>
    /// 物品收藏状态管理与格位高亮绘制：状态持久化在 ItemData.itemPropertyPairs 中，采用半透明银白磨砂内弧角扣与星芒边缘蒙版，与原版密码锁相互独立。
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
        private const string FavBgObjectName = "BetterInv_FavBg";
        private const string FavFrameObjectName = "BetterInv_FavFrame";

        private static readonly Color FavFrameTint = Color.white;

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

        private static Sprite s_frameSprite;
        private static Texture2D s_frameTexture;

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
                    Plugin.Notify($"已收藏：{itemName}", $"Favorited: {itemName}");
                else
                    Plugin.Notify($"已取消收藏：{itemName}", $"Unfavorited: {itemName}");
            }

            return nextState;
        }

        internal static void RefreshBadge(BasicItemUI ui)
        {
            if (ui == null || ui.transform == null) return;

            bool show = Plugin.EnableFavorites != null
                        && Plugin.EnableFavorites.Value
                        && ui.itemdata != null
                        && IsUIFavorited(ui);

            Transform existingBg = ui.transform.Find(FavBgObjectName);
            Transform existingFrame = ui.transform.Find(FavFrameObjectName);
            if (existingBg != null && existingBg.gameObject.activeSelf)
                existingBg.gameObject.SetActive(false);

            if (!show)
            {
                if (existingFrame != null && existingFrame.gameObject.activeSelf)
                    existingFrame.gameObject.SetActive(false);
                return;
            }

            GameObject frameGO = existingFrame != null ? existingFrame.gameObject : CreateSlotFrame(ui);
            if (frameGO != null)
            {
                if (!frameGO.activeSelf)
                    frameGO.SetActive(true);
                PlaceFrameBehindOverlays(ui, frameGO.transform, null);
            }
        }

        internal static void HideBadge(BasicItemUI ui)
        {
            if (ui == null || ui.transform == null) return;
            Transform existingBg = ui.transform.Find(FavBgObjectName);
            if (existingBg != null && existingBg.gameObject.activeSelf)
                existingBg.gameObject.SetActive(false);

            Transform existingFrame = ui.transform.Find(FavFrameObjectName);
            if (existingFrame != null && existingFrame.gameObject.activeSelf)
                existingFrame.gameObject.SetActive(false);
        }

        /// <summary>
        /// 将收藏边框严格放在物品图标之后、所有进度条（耐久/电量/子弹/液体/充电）、设备状态点、堆叠数字及原版框之下，
        /// 避免边框边缘遮挡底部状态条或角标。
        /// </summary>
        private static void PlaceFrameBehindOverlays(BasicItemUI ui, Transform target, Transform favBgTransform)
        {
            Transform parent = ui.transform;
            if (parent == null || target == null) return;

            string chargeBarName = null;
            string statusDotName = null;
            try { chargeBarName = BasicItemUI.CHARGE_BAR_NAME; } catch { }
            try { statusDotName = DeviceStatusDot.DOT_NAME; } catch { }

            Transform bgTf = ui.bgImage != null ? ui.bgImage.transform : null;
            Transform iconTf = ui.iconImage != null ? ui.iconImage.transform : null;
            Transform anchor =
                (iconTf != null && iconTf.parent == parent) ? iconTf :
                (favBgTransform != null && favBgTransform.parent == parent) ? favBgTransform :
                (bgTf != null && bgTf.parent == parent) ? bgTf :
                null;

            int earliestOverlayIdx = int.MaxValue;
            var pg = ui.progressGroup;
            if (pg != null)
                ConsiderOverlay(pg.groupRect, parent, target, ref earliestOverlayIdx);
            if (ui.stackNumberText != null)
                ConsiderOverlay(ui.stackNumberText.transform, parent, target, ref earliestOverlayIdx);
            if (ui.outlineImage != null)
                ConsiderOverlay(ui.outlineImage.transform, parent, target, ref earliestOverlayIdx);
            if (ui.selectionImage != null)
                ConsiderOverlay(ui.selectionImage.transform, parent, target, ref earliestOverlayIdx);
            if (ui.lockedImage != null)
                ConsiderOverlay(ui.lockedImage.transform, parent, target, ref earliestOverlayIdx);

            int childCount = parent.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c == null || c == target || c == favBgTransform || c == bgTf || c == iconTf)
                    continue;

                string name = c.name ?? string.Empty;
                bool isOverlay =
                    (!string.IsNullOrEmpty(chargeBarName) && string.Equals(name, chargeBarName, StringComparison.Ordinal)) ||
                    (!string.IsNullOrEmpty(statusDotName) && string.Equals(name, statusDotName, StringComparison.Ordinal)) ||
                    name.IndexOf("Progress", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Bar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Charge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("StatusDot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    c.GetComponent<ProgressBarGroup>() != null;

                if (isOverlay && i < earliestOverlayIdx)
                    earliestOverlayIdx = i;
            }

            int curIdx = target.GetSiblingIndex();
            int desiredIdx;
            if (anchor != null)
            {
                int anchorIdx = anchor.GetSiblingIndex();
                int afterAnchor = curIdx < anchorIdx ? anchorIdx : anchorIdx + 1;
                if (earliestOverlayIdx < int.MaxValue)
                {
                    int beforeOverlay = curIdx < earliestOverlayIdx ? Mathf.Max(0, earliestOverlayIdx - 1) : earliestOverlayIdx;
                    desiredIdx = Math.Min(afterAnchor, beforeOverlay);
                }
                else
                {
                    desiredIdx = afterAnchor;
                }
            }
            else if (earliestOverlayIdx < int.MaxValue)
            {
                desiredIdx = curIdx < earliestOverlayIdx ? Mathf.Max(0, earliestOverlayIdx - 1) : earliestOverlayIdx;
            }
            else
            {
                desiredIdx = Mathf.Min(1, Mathf.Max(0, childCount - 1));
            }

            desiredIdx = Mathf.Clamp(desiredIdx, 0, Mathf.Max(0, childCount - 1));
            if (curIdx != desiredIdx)
                target.SetSiblingIndex(desiredIdx);
        }

        private static void ConsiderOverlay(Transform overlay, Transform expectedParent, Transform frameTarget, ref int earliestIdx)
        {
            if (overlay == null || overlay == frameTarget || overlay.parent != expectedParent)
                return;

            int idx = overlay.GetSiblingIndex();
            if (idx < earliestIdx)
                earliestIdx = idx;
        }

        private static GameObject CreateSlotFrame(BasicItemUI ui)
        {
            try
            {
                var go = new GameObject(FavFrameObjectName);
                var rt = go.AddComponent<RectTransform>();
                rt.SetParent(ui.transform, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.localScale = Vector3.one;

                var img = go.AddComponent<Image>();
                img.sprite = GetOrCreateFrameSprite();
                img.type = Image.Type.Sliced;
                img.pixelsPerUnitMultiplier = 1f;
                img.color = FavFrameTint;
                img.raycastTarget = false;
                return go;
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("create_fav_frame", ex);
                return null;
            }
        }

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// 运行时生成 3x 超采样 (96x96) 抗锯齿九宫格磨砂银白内弧角扣与左上角星芒边缘蒙版精灵：
        /// 保留格子自身深色背景与底部进度条清晰度，四周辅以微圆角外框、内收弧形角扣、向内渐隐磨砂光晕与左上角四角星芒点缀。
        /// </summary>
        private static Sprite GetOrCreateFrameSprite()
        {
            if (s_frameSprite != null) return s_frameSprite;

            const int size = 96;
            const float spanUi = 32f; // 96 / 3 = 32 UI px total width
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            Color32 clear = new Color32(0, 0, 0, 0);

            for (int y = 0; y < size; y++)
            {
                float v = (y + 0.5f) / 3f;
                float dv = MathF.Min(v, spanUi - v);
                float vTop = spanUi - v;

                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / 3f;
                    float du = MathF.Min(u, spanUi - u);
                    float uLeft = u;

                    float qx = MathF.Max(1.8f - du, 0f);
                    float qy = MathF.Max(1.8f - dv, 0f);
                    float dIn = (qx == 0f && qy == 0f)
                        ? MathF.Min(du, dv)
                        : (1.8f - MathF.Sqrt(qx * qx + qy * qy));

                    float cov = SmoothStep(-0.22f, 0.22f, dIn);
                    if (cov <= 0.001f)
                    {
                        tex.SetPixel(x, y, clear);
                        continue;
                    }

                    float bottomMid = y < size / 2 ? SmoothStep(7.5f, 11.0f, du) : 0f;
                    float edgeSoft = 1f - 0.45f * bottomMid;
                    float lit = Math.Clamp(0.95f + 0.17f * ((spanUi - u) / spanUi - 0.5f + v / spanUi - 0.5f), 0.78f, 1.12f);

                    // 外沿极淡暗色分离描边
                    float darkA = 0.36f * (1f - SmoothStep(0.0f, 0.65f, MathF.Abs(dIn - 0.25f))) * edgeSoft;

                    // 边缘磨砂光晕与双层珍珠细边
                    float veilT = Math.Clamp((dIn - 0.65f) / 5.55f, 0f, 1f);
                    float veil = MathF.Pow(1f - veilT, 2.2f);
                    float veilA = 0.13f * veil * edgeSoft * lit;
                    float rimA = 0.32f * (1f - SmoothStep(0.15f, 0.65f, MathF.Abs(dIn - 0.85f))) * edgeSoft * lit;
                    float innerRimA = 0.12f * (1f - SmoothStep(0.10f, 0.45f, MathF.Abs(dIn - 2.25f))) * edgeSoft;

                    // 四角内弧收尖角扣
                    float claspFillA = 0f, claspArcA = 0f, claspOuterA = 0f;
                    if (du < 10.8f && dv < 10.8f)
                    {
                        float s = MathF.Sqrt(MathF.Max(0f, du / 10.2f)) + MathF.Sqrt(MathF.Max(0f, dv / 10.2f));
                        float claspMask = SmoothStep(1.03f, 0.94f, s) * SmoothStep(0.35f, 0.90f, dIn);
                        claspFillA = 0.18f * claspMask * (1f - 0.35f * MathF.Min(1f, s)) * lit;
                        claspArcA = 0.46f * (1f - SmoothStep(0.015f, 0.065f, MathF.Abs(s - 0.99f))) * SmoothStep(0.45f, 1.0f, dIn) * lit;
                        claspOuterA = 0.22f * claspMask * (1f - SmoothStep(0.15f, 0.70f, MathF.Abs(dIn - 0.90f))) * lit;
                    }

                    // 左上角微缩四角星芒 + 其余三角菱面点缀
                    float starA = 0f, starHaloA = 0f, starShadowA = 0f, pinA = 0f;
                    if (uLeft < 10.5f && vTop < 10.5f)
                    {
                        const float cx = 4.15f, cy = 4.15f, radius = 2.65f;
                        float sdx = MathF.Abs(uLeft - cx) / radius;
                        float sdy = MathF.Abs(vTop - cy) / radius;
                        float starVal = MathF.Pow(sdx, 0.56f) + MathF.Pow(sdy, 0.56f);
                        starA = 0.84f * SmoothStep(1.04f, 0.76f, starVal);
                        float r2 = (uLeft - cx) * (uLeft - cx) + (vTop - cy) * (vTop - cy);
                        starHaloA = 0.20f * MathF.Exp(-r2 / 5.5f);
                        float shdx = MathF.Abs(uLeft - (cx + 0.45f)) / radius;
                        float shdy = MathF.Abs(vTop - (cy + 0.45f)) / radius;
                        starShadowA = 0.32f * SmoothStep(1.08f, 0.75f, MathF.Pow(shdx, 0.56f) + MathF.Pow(shdy, 0.56f));
                    }
                    else if (du < 6.0f && dv < 6.0f)
                    {
                        float pinDist = MathF.Abs(du - 2.65f) + MathF.Abs(dv - 2.65f);
                        float pinAlpha = y >= size / 2 ? 0.42f : 0.30f;
                        pinA = pinAlpha * (1f - SmoothStep(0.35f, 0.80f, pinDist));
                    }

                    float lightA = Math.Clamp(
                        MathF.Max(MathF.Max(rimA + claspOuterA, claspArcA), MathF.Max(veilA + claspFillA + innerRimA, MathF.Max(starA, pinA)))
                        + 0.45f * veilA + starHaloA,
                        0f, 0.88f);
                    float darkTotal = Math.Clamp(darkA + starShadowA * (1f - starA), 0f, 0.55f);

                    float baseOutA = lightA + darkTotal * (1f - lightA);
                    float outA = baseOutA * cov;
                    if (outA <= 0.002f)
                    {
                        tex.SetPixel(x, y, clear);
                        continue;
                    }

                    float lr = 242f + 10f * starA;
                    float lg = 245f + 8f * starA;
                    float lb = 250f + 5f * starA;
                    float darkRem = darkTotal * (1f - lightA);
                    float r = (lr * lightA + 14f * darkRem) / baseOutA;
                    float g = (lg * lightA + 16f * darkRem) / baseOutA;
                    float b = (lb * lightA + 20f * darkRem) / baseOutA;

                    tex.SetPixel(x, y, new Color32(
                        (byte)Math.Clamp((int)MathF.Round(r), 0, 255),
                        (byte)Math.Clamp((int)MathF.Round(g), 0, 255),
                        (byte)Math.Clamp((int)MathF.Round(b), 0, 255),
                        (byte)Math.Clamp((int)MathF.Round(outA * 255f), 0, 255)));
                }
            }

            tex.Apply(false, false);
            UnityEngine.Object.DontDestroyOnLoad(tex);

            s_frameTexture = tex;
            s_frameSprite = Sprite.Create(
                tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f),
                300f,
                0u,
                SpriteMeshType.FullRect,
                new Vector4(36f, 36f, 36f, 36f));
            UnityEngine.Object.DontDestroyOnLoad(s_frameSprite);
            return s_frameSprite;
        }
    }
}
