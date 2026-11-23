using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ZedZone.BetterInventory
{
    /// <summary>
    /// 容器标题栏“筛选/优先级”快捷入口按钮与浮动配置弹窗（纯 Unity uGUI，适配中英文界面与多浮窗）。
    /// </summary>
    internal static class ContainerFilterUI
    {
        private const string HeaderButtonName = "BetterInv_RuleBtn";

        // 标题栏按钮配色（中性深炭灰贴合原版 UI，启用规则后用暗底琥珀金描边与文字提示）
        private static readonly Color BtnDefaultBg = new Color(0.15f, 0.15f, 0.15f, 0.94f);
        private static readonly Color BtnDefaultBorder = new Color(0.28f, 0.28f, 0.28f, 1f);
        private static readonly Color BtnDefaultText = new Color(0.80f, 0.80f, 0.80f, 1f);
        private static readonly Color BtnActiveBg = new Color(0.24f, 0.20f, 0.12f, 0.96f);
        private static readonly Color BtnActiveBorder = new Color(0.76f, 0.56f, 0.18f, 1f);
        private static readonly Color BtnActiveText = new Color(0.96f, 0.82f, 0.42f, 1f);

        // 弹窗主题色（中性深炭灰扁平风格，贴合游戏原版背包与菜单视觉）
        private static readonly Color BackdropCol = new Color(0f, 0f, 0f, 0.50f);
        private static readonly Color PanelBgCol = new Color(0.09f, 0.09f, 0.09f, 0.97f);
        private static readonly Color PanelBorderCol = new Color(0.28f, 0.28f, 0.28f, 1f);
        private static readonly Color HeaderBgCol = new Color(0.13f, 0.13f, 0.13f, 1f);
        private static readonly Color SectionBgCol = new Color(0.12f, 0.12f, 0.12f, 0.94f);
        private static readonly Color ChipOffCol = new Color(0.18f, 0.18f, 0.18f, 1f);
        private static readonly Color ChipOnAmber = new Color(0.60f, 0.43f, 0.13f, 1f);
        private static readonly Color ChipOnCyan = new Color(0.24f, 0.44f, 0.28f, 1f);
        private static readonly Color ChipOnSlate = new Color(0.32f, 0.34f, 0.38f, 1f);
        private static readonly Color DangerCol = new Color(0.50f, 0.18f, 0.18f, 1f);
        private static readonly Color TextPrimary = new Color(0.88f, 0.88f, 0.88f, 1f);
        private static readonly Color TextMuted = new Color(0.60f, 0.60f, 0.60f, 1f);

        // 可勾选的物品大类（按常用度排列）
        private static readonly ItemType[] FilterableTypes =
        {
            ItemType.Food,
            ItemType.Ammo,
            ItemType.Arrow,
            ItemType.Magazine,
            ItemType.GunPart,
            ItemType.RangedWeapon,
            ItemType.MeleeWeapon,
            ItemType.Throwable,
            ItemType.Material,
            ItemType.Clothing,
            ItemType.FaceMask,
            ItemType.Cloak,
            ItemType.Backpack,
            ItemType.Container,
            ItemType.Deployable
        };

        // 跨大类高频功能标签
        private static readonly ItemFeatureType[] FilterableFeatures =
        {
            ItemFeatureType.Bandage,
            ItemFeatureType.Battery,
            ItemFeatureType.Combustible,
            ItemFeatureType.AccessCard,
            ItemFeatureType.LightingTool,
            ItemFeatureType.PickLock,
            ItemFeatureType.BreakDoor,
            ItemFeatureType.Crop,
            ItemFeatureType.PetFood
        };

        // 跟踪所有带有标题栏按钮的容器面板，用于保存规则后同步刷新按钮徽章
        private static readonly List<InventoryPanel> TrackedPanels = new List<InventoryPanel>();

        private readonly struct TitleTextOrigBounds
        {
            public readonly bool UseOffsetMax;
            public readonly float Value;

            public TitleTextOrigBounds(bool useOffsetMax, float value)
            {
                UseOffsetMax = useOffsetMax;
                Value = value;
            }
        }

        // 记录窄标题栏被压缩前的原始边界，按钮改挂至底部工具栏时用于无损还原
        private static readonly Dictionary<IntPtr, TitleTextOrigBounds> OrigTitleBounds = new Dictionary<IntPtr, TitleTextOrigBounds>();

        // 单例复用弹窗对象
        private static GameObject s_popupCanvasGO;
        private static GameObject s_popupRootGO;
        private static RectTransform s_cardRT;
        private static InventoryData s_editingInventory;
        private static ItemData s_editingOwner;
        private static string s_editingTitle;
        private static ContainerRule s_editingRule;

        private static Text s_titleText;
        private static Button s_prioHighBtn;
        private static Button s_prioNormalBtn;
        private static Button s_prioLowBtn;
        private static Button s_modeNoneBtn;
        private static Button s_modeWhiteBtn;
        private static Button s_modeBlackBtn;
        private static Button s_matchExistingBtn;
        private static Text s_matchExistingText;
        private static readonly Dictionary<ItemType, Button> s_typeButtons = new Dictionary<ItemType, Button>();
        private static readonly Dictionary<ItemFeatureType, Button> s_featureButtons = new Dictionary<ItemFeatureType, Button>();
        private static Transform s_pinnedChipsContainer;
        private static Text s_pinnedEmptyHint;

        internal static bool IsPopupOpen => s_popupRootGO != null && s_popupRootGO.activeSelf;

        private static Font GetUiFont()
        {
            var gc = GameController.instance;
            if (gc != null)
            {
                if (gc.defaultFont != null) return gc.defaultFont;
                if (gc.arialFont != null) return gc.arialFont;
            }
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                   ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        /// <summary>
        /// 在目标容器窗口的底栏（InventoryButtonRow / LootInventoryPanel 底部按钮行）或回退至 titleRect 创建/刷新“筛选/优先级”按钮。
        /// </summary>
        internal static void AttachOrRefreshHeaderButton(
            InventoryPanel panel,
            RectTransform titleRect,
            RectTransform autosortRect,
            RectTransform closeRect,
            string fallbackTitle)
        {
            if (panel == null || titleRect == null) return;

            var buttonRow = panel.GetComponent<InventoryButtonRow>();
            RectTransform barRect = buttonRow != null ? buttonRow.barRect : null;

            // 跳过地面伪容器与粉碎机模式
            if (panel.TryCast<NearbyItemInventoryPanel>() != null) return;
            var lootPanel = panel.TryCast<LootInventoryPanel>();

            RectTransform refBottomBtn = null;
            RectTransform lootBottomParent = null;
            if (lootPanel != null)
            {
                if (lootPanel.depositAllButtonRT != null && lootPanel.depositAllButtonRT.gameObject.activeInHierarchy)
                    refBottomBtn = lootPanel.depositAllButtonRT;
                else if (lootPanel.collectAllButtonRT != null && lootPanel.collectAllButtonRT.gameObject.activeInHierarchy)
                    refBottomBtn = lootPanel.collectAllButtonRT;

                if (refBottomBtn != null)
                    lootBottomParent = refBottomBtn.parent != null ? refBottomBtn.parent.TryCast<RectTransform>() : null;
            }

            if (Plugin.ShowContainerHeaderButton == null || !Plugin.ShowContainerHeaderButton.Value)
            {
                HideExistingHeaderButtons(titleRect, barRect, lootBottomParent);
                RestoreHeaderTitleText(titleRect);
                return;
            }

            if (lootPanel != null && lootPanel.IsGrinderMode)
            {
                HideExistingHeaderButtons(titleRect, barRect, lootBottomParent);
                RestoreHeaderTitleText(titleRect);
                return;
            }

            var inv = panel.inventoryData;
            if (inv == null) return;

            TrackPanel(panel);

            try
            {
                bool dockInBar = barRect != null && barRect.gameObject.activeInHierarchy;
                bool dockInLootBottom = !dockInBar && refBottomBtn != null && lootBottomParent != null && refBottomBtn.gameObject.activeInHierarchy;

                RectTransform targetParent = dockInBar ? barRect : (dockInLootBottom ? lootBottomParent : titleRect);
                GameObject btnGO = FindOrCreateHeaderButton(targetParent, titleRect, barRect, lootBottomParent);
                if (btnGO == null) return;

                if (!btnGO.activeSelf) btnGO.SetActive(true);

                var rule = ContainerRuleStore.GetRule(inv);
                string label = ContainerRuleStore.BuildCompactBadgeLabel(rule, out bool hasCustom);

                var rt = btnGO.GetComponent<RectTransform>();
                var img = btnGO.GetComponent<Image>();
                var outline = btnGO.GetComponent<Outline>();
                var txt = btnGO.GetComponentInChildren<Text>(true);
                var btn = btnGO.GetComponent<Button>();

                if (dockInBar)
                {
                    TryMeasureBarButtons(barRect, out float minChildLocalLeftX, out float barCenterY, out float barRefH);
                    float freeLeftInBar = minChildLocalLeftX - barRect.rect.xMin;
                    bool isNarrow = barRect.rect.width <= 1f || freeLeftInBar < 40f;

                    string displayLabel = label;
                    if (isNarrow && !hasCustom)
                        displayLabel = Plugin.L("筛", "F");

                    if (txt != null)
                    {
                        txt.text = displayLabel;
                        txt.fontSize = isNarrow ? 9 : 10;
                        txt.resizeTextMaxSize = isNarrow ? 9 : 10;
                        txt.color = hasCustom ? BtnActiveText : BtnDefaultText;
                    }
                    if (img != null)
                        img.color = hasCustom ? BtnActiveBg : BtnDefaultBg;
                    if (outline != null)
                        outline.effectColor = hasCustom ? BtnActiveBorder : BtnDefaultBorder;

                    float desiredWidth = ComputeCompactButtonWidth(displayLabel, isNarrow);
                    float btnHeight = barRefH > 1f ? Mathf.Clamp(barRefH, 12f, 16f) : 13f;
                    float targetLocalLeftX = (freeLeftInBar >= desiredWidth + 4f)
                        ? (barRect.rect.xMin + 2f)
                        : (minChildLocalLeftX - desiredWidth - 3f);

                    rt.anchorMin = new Vector2(0f, 0.5f);
                    rt.anchorMax = new Vector2(0f, 0.5f);
                    rt.pivot = new Vector2(0f, 0.5f);
                    rt.sizeDelta = new Vector2(desiredWidth, btnHeight);
                    rt.localScale = Vector3.one;
                    rt.localPosition = new Vector3(targetLocalLeftX, barCenterY, 0f);

                    // 按钮已在底栏停靠，还原标题栏原本文本宽度
                    RestoreHeaderTitleText(titleRect);
                }
                else if (dockInLootBottom)
                {
                    if (txt != null)
                    {
                        txt.text = label;
                        txt.fontSize = 11;
                        txt.resizeTextMaxSize = 11;
                        txt.color = hasCustom ? BtnActiveText : BtnDefaultText;
                    }
                    if (img != null)
                        img.color = hasCustom ? BtnActiveBg : BtnDefaultBg;
                    if (outline != null)
                        outline.effectColor = hasCustom ? BtnActiveBorder : BtnDefaultBorder;

                    Vector3 refWorldCenter = refBottomBtn.TransformPoint(new Vector3(refBottomBtn.rect.center.x, refBottomBtn.rect.center.y, 0f));
                    Vector3 refLocalCenter = lootBottomParent.InverseTransformPoint(refWorldCenter);
                    float localCenterY = refLocalCenter.y;
                    float btnHeight = refBottomBtn.rect.height > 1f ? Mathf.Clamp(refBottomBtn.rect.height, 14f, 24f) : 18f;

                    Vector3 worldPanelLeft = titleRect.TransformPoint(new Vector3(titleRect.rect.xMin, titleRect.rect.center.y, 0f));
                    float panelLeftLocalX = lootBottomParent.InverseTransformPoint(worldPanelLeft).x;

                    float minBottomBtnLocalLeftX = float.PositiveInfinity;
                    RectTransform[] rowBtns = new[]
                    {
                        lootPanel.depositAllButtonRT,
                        lootPanel.collectAllButtonRT,
                        lootPanel.depositSameButtonRT
                    };
                    for (int i = 0; i < rowBtns.Length; i++)
                    {
                        var b = rowBtns[i];
                        if (b == null || !b.gameObject.activeInHierarchy || b.rect.width <= 0.5f) continue;
                        Vector3 wLeft = b.TransformPoint(new Vector3(b.rect.xMin, b.rect.center.y, 0f));
                        Vector3 lLeft = lootBottomParent.InverseTransformPoint(wLeft);
                        if (Mathf.Abs(lLeft.y - localCenterY) <= btnHeight * 0.65f)
                            minBottomBtnLocalLeftX = Mathf.Min(minBottomBtnLocalLeftX, lLeft.x);
                    }
                    if (float.IsPositiveInfinity(minBottomBtnLocalLeftX))
                    {
                        Vector3 wLeft = refBottomBtn.TransformPoint(new Vector3(refBottomBtn.rect.xMin, refBottomBtn.rect.center.y, 0f));
                        minBottomBtnLocalLeftX = lootBottomParent.InverseTransformPoint(wLeft).x;
                    }

                    float desiredWidth = Mathf.Max(36f, ComputeCompactButtonWidth(label, false));
                    float targetLocalLeftX = (minBottomBtnLocalLeftX - panelLeftLocalX >= desiredWidth + 6f)
                        ? (panelLeftLocalX + 2f)
                        : (minBottomBtnLocalLeftX - desiredWidth - 4f);

                    rt.anchorMin = refBottomBtn.anchorMin;
                    rt.anchorMax = refBottomBtn.anchorMax;
                    rt.pivot = new Vector2(0f, 0.5f);
                    rt.sizeDelta = new Vector2(desiredWidth, btnHeight);
                    rt.localScale = Vector3.one;
                    rt.localPosition = new Vector3(targetLocalLeftX, localCenterY, 0f);

                    RestoreHeaderTitleText(titleRect);
                }
                else
                {
                    // 无底栏容器的兜底方案：停靠标题栏右侧原版按钮左邻
                    float rightInset = ComputeRightInset(titleRect, autosortRect, closeRect, out float refHeight);
                    float titleWidth = titleRect.rect.width;
                    bool isNarrow = titleWidth > 1f && (titleWidth - rightInset) < 78f;

                    string displayLabel = label;
                    if (isNarrow && !hasCustom)
                        displayLabel = Plugin.L("筛", "F");

                    if (txt != null)
                    {
                        txt.text = displayLabel;
                        txt.fontSize = isNarrow ? 9 : 10;
                        txt.resizeTextMaxSize = isNarrow ? 9 : 10;
                        txt.color = hasCustom ? BtnActiveText : BtnDefaultText;
                    }
                    if (img != null)
                        img.color = hasCustom ? BtnActiveBg : BtnDefaultBg;
                    if (outline != null)
                        outline.effectColor = hasCustom ? BtnActiveBorder : BtnDefaultBorder;

                    float desiredWidth = ComputeCompactButtonWidth(displayLabel, isNarrow);
                    if (titleWidth > 1f)
                    {
                        float maxAllowed = Mathf.Max(18f, titleWidth - rightInset - 34f);
                        desiredWidth = Mathf.Min(desiredWidth, maxAllowed);
                    }

                    float btnHeight = refHeight > 1f ? Mathf.Clamp(refHeight, 12f, 18f) : 14f;
                    rt.anchorMin = new Vector2(1f, 0.5f);
                    rt.anchorMax = new Vector2(1f, 0.5f);
                    rt.pivot = new Vector2(1f, 0.5f);
                    rt.sizeDelta = new Vector2(desiredWidth, btnHeight);
                    rt.localScale = Vector3.one;
                    rt.anchoredPosition = new Vector2(-rightInset, 0f);

                    ConstrainHeaderTitleText(titleRect, rightInset + desiredWidth + 3f);
                }

                if (btn != null)
                {
                    btn.onClick.RemoveAllListeners();
                    InventoryPanel capturedPanel = panel;
                    string capturedFallback = fallbackTitle;
                    btn.onClick.AddListener((UnityAction)(() =>
                    {
                        ClearUiSelection();
                        if (capturedPanel == null || capturedPanel.inventoryData == null) return;
                        string title = ResolvePanelTitle(capturedPanel, capturedFallback);
                        OpenPopup(capturedPanel.inventoryData, null, title);
                    }));
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("header_btn_refresh", ex);
            }
        }

        private static void HideExistingHeaderButtons(params RectTransform[] parents)
        {
            if (parents == null) return;
            for (int i = 0; i < parents.Length; i++)
            {
                RectTransform p = parents[i];
                if (p == null) continue;
                Transform t = p.Find(HeaderButtonName);
                if (t != null && t.gameObject.activeSelf)
                    t.gameObject.SetActive(false);
            }
        }

        private static GameObject FindOrCreateHeaderButton(RectTransform targetParent, params RectTransform[] candidateParents)
        {
            Transform existing = targetParent != null ? targetParent.Find(HeaderButtonName) : null;
            if (candidateParents != null)
            {
                for (int i = 0; i < candidateParents.Length; i++)
                {
                    RectTransform p = candidateParents[i];
                    if (p == null || p == targetParent) continue;
                    Transform other = p.Find(HeaderButtonName);
                    if (other == null) continue;

                    if (existing == null)
                    {
                        other.SetParent(targetParent, false);
                        existing = other;
                    }
                    else if (other.gameObject.activeSelf)
                    {
                        other.gameObject.SetActive(false);
                    }
                }
            }
            return existing != null ? existing.gameObject : CreateHeaderButton(targetParent);
        }

        private static void TryMeasureBarButtons(
            RectTransform barRect,
            out float minChildLocalLeftX,
            out float centerY,
            out float referenceHeight)
        {
            minChildLocalLeftX = barRect != null ? barRect.rect.xMax - 65f : 0f;
            centerY = barRect != null ? barRect.rect.center.y : 0f;
            referenceHeight = 0f;
            if (barRect == null) return;

            float barWidth = barRect.rect.width;
            float measuredLeft = float.PositiveInfinity;
            float measuredCenterY = centerY;
            bool foundButton = false;

            for (int i = 0; i < barRect.childCount; i++)
            {
                Transform child = barRect.GetChild(i);
                if (child == null || !child.gameObject.activeInHierarchy || child.name == HeaderButtonName)
                    continue;
                if (child.GetComponent<Button>() == null)
                    continue;

                var childRT = child.TryCast<RectTransform>();
                if (childRT == null || childRT.rect.width <= 0.5f)
                    continue;

                Vector3 worldLeft = childRT.TransformPoint(new Vector3(childRT.rect.xMin, childRT.rect.center.y, 0f));
                Vector3 localLeft = barRect.InverseTransformPoint(worldLeft);
                if (barWidth <= 1f || (localLeft.x >= barRect.rect.xMin - 8f && localLeft.x <= barRect.rect.xMax + 8f))
                {
                    if (localLeft.x < measuredLeft)
                    {
                        measuredLeft = localLeft.x;
                        measuredCenterY = localLeft.y;
                    }
                    if (childRT.rect.height > 1f)
                        referenceHeight = Mathf.Max(referenceHeight, childRT.rect.height);
                    foundButton = true;
                }
            }

            if (foundButton)
            {
                minChildLocalLeftX = measuredLeft;
                centerY = measuredCenterY;
            }
        }

        private static void ConstrainHeaderTitleText(RectTransform titleRect, float reservedRightPx)
        {
            if (titleRect == null || titleRect.rect.width <= 1f) return;

            for (int i = 0; i < titleRect.childCount; i++)
            {
                Transform child = titleRect.GetChild(i);
                if (child == null || child.name == HeaderButtonName || !child.gameObject.activeInHierarchy)
                    continue;
                if (child.GetComponent<Button>() != null)
                    continue;

                var txt = child.GetComponent<Text>();
                if (txt == null) continue;
                var trt = child.TryCast<RectTransform>();
                if (trt == null) continue;

                IntPtr key = InventoryHelper.Ptr(txt);
                bool useOffsetMax = trt.anchorMax.x >= 0.9f;

                if (key != IntPtr.Zero && !OrigTitleBounds.ContainsKey(key))
                    OrigTitleBounds[key] = new TitleTextOrigBounds(useOffsetMax, useOffsetMax ? trt.offsetMax.x : trt.sizeDelta.x);

                // 仅调整靠左延伸的标题文本，确保右端不越过筛选按钮左边界
                if (useOffsetMax)
                {
                    float targetRight = -reservedRightPx;
                    if (trt.offsetMax.x > targetRight)
                        trt.offsetMax = new Vector2(targetRight, trt.offsetMax.y);
                }
                else
                {
                    float avail = Mathf.Max(20f, titleRect.rect.width - reservedRightPx - 4f);
                    if (trt.sizeDelta.x > avail)
                        trt.sizeDelta = new Vector2(avail, trt.sizeDelta.y);
                }

                txt.horizontalOverflow = HorizontalWrapMode.Wrap;
                if (!txt.resizeTextForBestFit && txt.fontSize >= 10)
                {
                    txt.resizeTextForBestFit = true;
                    txt.resizeTextMinSize = 7;
                    txt.resizeTextMaxSize = txt.fontSize;
                }
            }
        }

        private static void RestoreHeaderTitleText(RectTransform titleRect)
        {
            if (titleRect == null || OrigTitleBounds.Count == 0) return;

            for (int i = 0; i < titleRect.childCount; i++)
            {
                Transform child = titleRect.GetChild(i);
                if (child == null || child.name == HeaderButtonName) continue;
                var txt = child.GetComponent<Text>();
                if (txt == null) continue;

                IntPtr key = InventoryHelper.Ptr(txt);
                if (key == IntPtr.Zero || !OrigTitleBounds.TryGetValue(key, out TitleTextOrigBounds orig))
                    continue;

                var trt = child.TryCast<RectTransform>();
                if (trt != null)
                {
                    if (orig.UseOffsetMax)
                        trt.offsetMax = new Vector2(orig.Value, trt.offsetMax.y);
                    else
                        trt.sizeDelta = new Vector2(orig.Value, trt.sizeDelta.y);
                }
                OrigTitleBounds.Remove(key);
            }
        }

        private static float ComputeCompactButtonWidth(string label, bool isNarrow)
        {
            if (string.IsNullOrEmpty(label)) return 20f;
            float est = 8f;
            for (int i = 0; i < label.Length; i++)
            {
                // 中文与宽符号按 10px 估算，ASCII 按 6px 估算
                est += label[i] > 127 ? (isNarrow ? 9f : 10f) : 6f;
            }
            return Mathf.Clamp(est, isNarrow ? 18f : 22f, 68f);
        }

        private static float ComputeRightInset(
            RectTransform titleRect,
            RectTransform autosortRect,
            RectTransform closeRect,
            out float referenceHeight)
        {
            const float gap = 3f;
            referenceHeight = 0f;

            float inset = 0f;
            bool measuredAny = false;

            if (TryMeasureButtonLeftInset(titleRect, autosortRect, out float sortInset, out float sortH))
            {
                inset = Mathf.Max(inset, sortInset);
                if (sortH > 1f) referenceHeight = sortH;
                measuredAny = true;
            }

            if (TryMeasureButtonLeftInset(titleRect, closeRect, out float closeInset, out float closeH))
            {
                inset = Mathf.Max(inset, closeInset);
                if (referenceHeight <= 1f && closeH > 1f) referenceHeight = closeH;
                measuredAny = true;
            }

            // 顺带扫描标题栏右侧其它原版激活按钮，确保不与任何同类图标重叠
            float titleCenterX = titleRect.rect.center.x;
            for (int i = 0; i < titleRect.childCount; i++)
            {
                Transform child = titleRect.GetChild(i);
                if (child == null || !child.gameObject.activeInHierarchy || child.name == HeaderButtonName)
                    continue;
                if (child.GetComponent<Button>() == null)
                    continue;

                var childRT = child.TryCast<RectTransform>();
                if (childRT == null) continue;
                if (!TryMeasureButtonLeftInset(titleRect, childRT, out float childInset, out float childH))
                    continue;

                float childLocalLeft = titleRect.rect.xMax - childInset;
                if (childLocalLeft >= titleCenterX - 4f)
                {
                    inset = Mathf.Max(inset, childInset);
                    if (referenceHeight <= 1f && childH > 1f) referenceHeight = childH;
                    measuredAny = true;
                }
            }

            if (measuredAny)
                return inset + gap;

            // 首帧尚未完成布局时的保守回退值（预留“整理”+“X”的宽度）
            bool hasSort = autosortRect != null && autosortRect.gameObject.activeInHierarchy;
            bool hasClose = closeRect != null && closeRect.gameObject.activeInHierarchy;
            if (hasSort) return 58f;
            if (hasClose) return 22f;
            return 4f;
        }

        private static bool TryMeasureButtonLeftInset(
            RectTransform titleRect,
            RectTransform buttonRect,
            out float distFromTitleRight,
            out float measuredHeight)
        {
            distFromTitleRight = 0f;
            measuredHeight = 0f;

            if (titleRect == null || buttonRect == null || !buttonRect.gameObject.activeInHierarchy)
                return false;

            Rect btnRect = buttonRect.rect;
            if (btnRect.width <= 0.5f || titleRect.rect.width <= 0.5f)
                return false;

            Vector3 worldLeft = buttonRect.TransformPoint(new Vector3(btnRect.xMin, btnRect.center.y, 0f));
            Vector3 localLeft = titleRect.InverseTransformPoint(worldLeft);
            float dist = titleRect.rect.xMax - localLeft.x;
            if (dist <= 0.5f || dist > titleRect.rect.width * 0.85f)
                return false;

            distFromTitleRight = dist;
            measuredHeight = btnRect.height;
            return true;
        }

        private static string ResolvePanelTitle(InventoryPanel panel, string fallback)
        {
            var loot = panel.TryCast<LootInventoryPanel>();
            if (loot != null && loot.titleText != null && !string.IsNullOrWhiteSpace(loot.titleText.text))
                return loot.titleText.text;

            var common = panel.TryCast<CommonInventoryPanel>();
            if (common != null && common.titleText != null && !string.IsNullOrWhiteSpace(common.titleText.text))
                return common.titleText.text;

            return InventoryHelper.GetContainerDisplayName(panel.inventoryData, fallback);
        }

        private static GameObject CreateHeaderButton(RectTransform titleRect)
        {
            var go = new GameObject(HeaderButtonName);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(titleRect, false);
            rt.localScale = Vector3.one;

            var img = go.AddComponent<Image>();
            img.color = BtnDefaultBg;

            var outline = go.AddComponent<Outline>();
            outline.effectColor = BtnDefaultBorder;
            outline.effectDistance = new Vector2(0.8f, -0.8f);

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            DisableSelectionHighlight(btn);

            var textGO = new GameObject("Label");
            var trt = textGO.AddComponent<RectTransform>();
            trt.SetParent(rt, false);
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(1f, 0f);
            trt.offsetMax = new Vector2(-1f, 0f);
            trt.localScale = Vector3.one;

            var txt = textGO.AddComponent<Text>();
            txt.font = GetUiFont();
            txt.fontSize = 10;
            txt.resizeTextForBestFit = true;
            txt.resizeTextMinSize = 6;
            txt.resizeTextMaxSize = 10;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Truncate;
            txt.raycastTarget = false;
            txt.color = BtnDefaultText;

            return go;
        }

        private static void DisableSelectionHighlight(Button btn)
        {
            if (btn == null) return;
            btn.navigation = new Navigation { mode = Navigation.Mode.None };
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.16f, 1.16f, 1.16f, 1f);
            colors.pressedColor = new Color(0.84f, 0.84f, 0.84f, 1f);
            colors.selectedColor = Color.white;
            btn.colors = colors;
        }

        private static void ClearUiSelection()
        {
            var es = EventSystem.current;
            if (es != null && es.currentSelectedGameObject != null)
                es.SetSelectedGameObject(null);
        }

        private static void TrackPanel(InventoryPanel panel)
        {
            IntPtr ptr = InventoryHelper.Ptr(panel);
            if (ptr == IntPtr.Zero) return;
            for (int i = TrackedPanels.Count - 1; i >= 0; i--)
            {
                var p = TrackedPanels[i];
                if (p == null)
                {
                    TrackedPanels.RemoveAt(i);
                    continue;
                }
                if (InventoryHelper.Ptr(p) == ptr)
                    return;
            }
            TrackedPanels.Add(panel);
        }

        internal static void RefreshAllHeaderButtons()
        {
            for (int i = TrackedPanels.Count - 1; i >= 0; i--)
            {
                var panel = TrackedPanels[i];
                if (panel == null)
                {
                    TrackedPanels.RemoveAt(i);
                    continue;
                }
                if (!panel.gameObject.activeInHierarchy) continue;
                RefreshPanelHeader(panel);
            }
        }

        internal static void RefreshPanelHeader(InventoryPanel panel)
        {
            if (panel == null) return;

            var playerInv = panel.TryCast<PlayerInventoryPanel>();
            if (playerInv != null)
            {
                AttachOrRefreshHeaderButton(playerInv, playerInv.titleRect, playerInv.autosortButtonRect, playerInv.closeButtonRect, Plugin.L("主背包", "Main Backpack"));
                return;
            }

            var lootInv = panel.TryCast<LootInventoryPanel>();
            if (lootInv != null)
            {
                RectTransform sortRT = lootInv.autoSortButtonGO != null ? lootInv.autoSortButtonGO.GetComponent<RectTransform>() : null;
                AttachOrRefreshHeaderButton(lootInv, lootInv.titleRect, sortRT, lootInv.closeButtonRect, lootInv.lootObjectName);
                return;
            }

            var commonInv = panel.TryCast<CommonInventoryPanel>();
            if (commonInv != null)
            {
                RectTransform sortRT = commonInv.autoSortButtonGO != null ? commonInv.autoSortButtonGO.GetComponent<RectTransform>() : null;
                AttachOrRefreshHeaderButton(commonInv, commonInv.titleRect, sortRT, commonInv.closeButtonRect, null);
            }
        }

        /// <summary>
        /// 打开容器规则配置弹窗（支持从标题栏按钮或背包物品右键菜单进入）。
        /// </summary>
        internal static void OpenPopup(InventoryData inv, ItemData explicitOwner, string containerTitle)
        {
            if (inv == null && explicitOwner == null) return;

            try
            {
                s_editingInventory = inv ?? explicitOwner?.inventoryData;
                s_editingOwner = explicitOwner ?? InventoryHelper.FindOwnerItemData(s_editingInventory);
                s_editingTitle = InventoryHelper.GetContainerDisplayName(s_editingInventory, containerTitle);

                ContainerRule existing = s_editingOwner != null
                    ? ContainerRuleStore.GetRuleForItem(s_editingOwner)
                    : ContainerRuleStore.GetRule(s_editingInventory);

                s_editingRule = existing != null ? existing.Clone() : new ContainerRule();

                EnsurePopupBuilt();
                s_popupRootGO.SetActive(true);
                s_popupCanvasGO.transform.SetAsLastSibling();
                ClearUiSelection();
                RefreshPopupVisuals();
                ForceRebuildPopupLayout();
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("open_rule_popup", ex);
            }
        }

        private static void ForceRebuildPopupLayout()
        {
            if (s_cardRT == null || s_popupRootGO == null || !s_popupRootGO.activeInHierarchy)
                return;
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(s_cardRT);
        }

        internal static void ClosePopup()
        {
            ClearUiSelection();
            if (s_popupRootGO != null && s_popupRootGO.activeSelf)
                s_popupRootGO.SetActive(false);
        }

        private static void CommitRuleChange()
        {
            ClearUiSelection();
            ContainerRuleStore.SaveRule(s_editingInventory, s_editingOwner, s_editingRule);
            RefreshPopupVisuals();
            RefreshAllHeaderButtons();
        }

        private static void EnsurePopupBuilt()
        {
            if (s_popupRootGO != null) return;

            // 独立顶层 Canvas，确保浮在所有背包窗口与浮窗之上
            s_popupCanvasGO = new GameObject("BetterInventory_RuleCanvas");
            UnityEngine.Object.DontDestroyOnLoad(s_popupCanvasGO);

            var canvas = s_popupCanvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;

            var scaler = s_popupCanvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1440f, 810f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            s_popupCanvasGO.AddComponent<GraphicRaycaster>();

            // 全屏根容器（遮罩与卡片作为兄弟节点，点击卡片空白处不会穿透触发遮罩关闭）
            s_popupRootGO = new GameObject("PopupRoot");
            var rootRT = s_popupRootGO.AddComponent<RectTransform>();
            rootRT.SetParent(s_popupCanvasGO.transform, false);
            rootRT.anchorMin = Vector2.zero;
            rootRT.anchorMax = Vector2.one;
            rootRT.offsetMin = Vector2.zero;
            rootRT.offsetMax = Vector2.zero;

            var backdropGO = new GameObject("Backdrop");
            var backdropRT = backdropGO.AddComponent<RectTransform>();
            backdropRT.SetParent(rootRT, false);
            backdropRT.anchorMin = Vector2.zero;
            backdropRT.anchorMax = Vector2.one;
            backdropRT.offsetMin = Vector2.zero;
            backdropRT.offsetMax = Vector2.zero;

            var backdropImg = backdropGO.AddComponent<Image>();
            backdropImg.color = BackdropCol;
            var backdropBtn = backdropGO.AddComponent<Button>();
            backdropBtn.transition = Selectable.Transition.None;
            backdropBtn.onClick.AddListener((UnityAction)(() => ClosePopup()));

            // 居中主卡片（固定宽度 476px，高度随内容自适应并严格由子布局驱动）
            var cardGO = new GameObject("Card");
            var cardRT = cardGO.AddComponent<RectTransform>();
            cardRT.SetParent(rootRT, false);
            cardRT.anchorMin = new Vector2(0.5f, 0.5f);
            cardRT.anchorMax = new Vector2(0.5f, 0.5f);
            cardRT.pivot = new Vector2(0.5f, 0.5f);
            cardRT.sizeDelta = new Vector2(476f, 680f);
            s_cardRT = cardRT;

            var cardImg = cardGO.AddComponent<Image>();
            cardImg.color = PanelBgCol;
            var cardOutline = cardGO.AddComponent<Outline>();
            cardOutline.effectColor = PanelBorderCol;
            cardOutline.effectDistance = new Vector2(1.5f, -1.5f);

            var cardLayout = cardGO.AddComponent<VerticalLayoutGroup>();
            cardLayout.padding = new RectOffset(14, 14, 12, 12);
            cardLayout.spacing = 8f;
            cardLayout.childAlignment = TextAnchor.UpperCenter;
            cardLayout.childControlWidth = true;
            cardLayout.childControlHeight = true;
            cardLayout.childForceExpandWidth = true;
            cardLayout.childForceExpandHeight = false;

            var cardFitter = cardGO.AddComponent<ContentSizeFitter>();
            cardFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            cardFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // 1. 顶部标题栏
            BuildHeader(cardRT);

            // 2. 拾取优先级区域
            BuildPrioritySection(cardRT);

            // 3. 过滤模式与智能同类开关
            BuildFilterModeSection(cardRT);

            // 4. 物品大类多选网格
            BuildCategorySection(cardRT);

            // 5. 常用功能标签网格
            BuildFeatureSection(cardRT);

            // 6. 指定物品（一键导入容器内物品 + 芯片展示）
            BuildPinnedItemsSection(cardRT);

            // 7. 底部操作栏
            BuildFooter(cardRT);

            s_popupRootGO.SetActive(false);
        }

        private static void BuildHeader(RectTransform parent)
        {
            var row = CreateBoxRow(parent, "Header", HeaderBgCol, 36f);
            var h = row.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(12, 8, 5, 5);
            h.spacing = 8f;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;

            s_titleText = CreateLabel(row.transform, "Title", "", 15, FontStyle.Bold, TextPrimary, TextAnchor.MiddleLeft);
            s_titleText.resizeTextForBestFit = true;
            s_titleText.resizeTextMinSize = 11;
            s_titleText.resizeTextMaxSize = 15;
            var titleLE = s_titleText.gameObject.AddComponent<LayoutElement>();
            titleLE.minWidth = 100f;
            titleLE.preferredWidth = 100f;
            titleLE.flexibleWidth = 1f;
            titleLE.minHeight = 26f;
            titleLE.preferredHeight = 26f;

            var closeBtn = CreateStyledButton(row.transform, "CloseBtn", "×", 28f, 26f, ChipOffCol, TextPrimary, 16);
            closeBtn.onClick.AddListener((UnityAction)(() => ClosePopup()));
        }

        private static void BuildPrioritySection(RectTransform parent)
        {
            var box = CreateSectionBox(parent, "PrioritySection");

            var title = CreateLabel(box.transform, "SecTitle", Plugin.L("拾取顺序（三档直选）", "Pickup Priority Tier"), 12, FontStyle.Bold, TextMuted, TextAnchor.MiddleLeft);
            SetRowHeight(title.gameObject, 18f);

            var tierRow = CreatePlainRow(box.transform, "TierRow", 28f, 6f);
            s_prioHighBtn = CreateFlexButton(tierRow.transform, "PrioHigh", Plugin.L("优先装入", "High (First)"), 28f, 12);
            s_prioNormalBtn = CreateFlexButton(tierRow.transform, "PrioNormal", Plugin.L("正常", "Normal"), 28f, 12);
            s_prioLowBtn = CreateFlexButton(tierRow.transform, "PrioLow", Plugin.L("最后兜底", "Low (Fallback)"), 28f, 12);

            s_prioHighBtn.onClick.AddListener((UnityAction)(() =>
            {
                if (s_editingRule == null) return;
                s_editingRule.Priority = ContainerRule.PriorityHigh;
                CommitRuleChange();
            }));
            s_prioNormalBtn.onClick.AddListener((UnityAction)(() =>
            {
                if (s_editingRule == null) return;
                s_editingRule.Priority = ContainerRule.PriorityNormal;
                CommitRuleChange();
            }));
            s_prioLowBtn.onClick.AddListener((UnityAction)(() =>
            {
                if (s_editingRule == null) return;
                s_editingRule.Priority = ContainerRule.PriorityLow;
                CommitRuleChange();
            }));

            var hint = CreateLabel(
                box.transform,
                "Hint",
                Plugin.L("按「优先装入 → 正常 → 最后兜底」自动分配；同档位下命中筛选规则的容器优先。",
                         "Loot routes in order: High -> Normal -> Low. Matched filters beat unfiltered bags."),
                11, FontStyle.Normal, TextMuted, TextAnchor.MiddleLeft);
            SetRowHeight(hint.gameObject, 26f);
        }

        private static void BuildFilterModeSection(RectTransform parent)
        {
            var box = CreateSectionBox(parent, "ModeSection");

            var modeRow = CreatePlainRow(box.transform, "ModeRow", 28f, 6f);
            s_modeNoneBtn = CreateFlexButton(modeRow.transform, "ModeNone", Plugin.L("关闭筛选", "Filter Off"), 28f, 12);
            s_modeWhiteBtn = CreateFlexButton(modeRow.transform, "ModeWhite", Plugin.L("白名单（仅收勾选）", "Whitelist"), 28f, 12);
            s_modeBlackBtn = CreateFlexButton(modeRow.transform, "ModeBlack", Plugin.L("黑名单（排除勾选）", "Blacklist"), 28f, 12);

            s_modeNoneBtn.onClick.AddListener((UnityAction)(() =>
            {
                if (s_editingRule == null) return;
                s_editingRule.Mode = ContainerFilterMode.None;
                s_editingRule.Types.Clear();
                s_editingRule.Features.Clear();
                s_editingRule.ItemIds.Clear();
                CommitRuleChange();
            }));
            s_modeWhiteBtn.onClick.AddListener((UnityAction)(() =>
            {
                if (s_editingRule == null) return;
                s_editingRule.Mode = ContainerFilterMode.Whitelist;
                CommitRuleChange();
            }));
            s_modeBlackBtn.onClick.AddListener((UnityAction)(() =>
            {
                if (s_editingRule == null) return;
                s_editingRule.Mode = ContainerFilterMode.Blacklist;
                CommitRuleChange();
            }));

            s_matchExistingBtn = CreateFlexButton(box.transform, "MatchExistingBtn", "", 26f, 12);
            s_matchExistingText = s_matchExistingBtn.GetComponentInChildren<Text>(true);
            s_matchExistingBtn.onClick.AddListener((UnityAction)(() =>
            {
                if (s_editingRule == null) return;
                s_editingRule.MatchExistingTypesOnly = !s_editingRule.MatchExistingTypesOnly;
                CommitRuleChange();
            }));
        }

        private static void BuildCategorySection(RectTransform parent)
        {
            var box = CreateSectionBox(parent, "CategorySection");
            var title = CreateLabel(box.transform, "SecTitle", Plugin.L("按物品分类筛选", "Filter by Item Category"), 12, FontStyle.Bold, TextMuted, TextAnchor.MiddleLeft);
            SetRowHeight(title.gameObject, 18f);

            int rows = (FilterableTypes.Length + 2) / 3;
            float gridHeight = rows * 24f + Math.Max(0, rows - 1) * 5f;

            var gridGO = new GameObject("TypeGrid");
            var gridRT = gridGO.AddComponent<RectTransform>();
            gridRT.SetParent(box.transform, false);
            SetRowHeight(gridGO, gridHeight);

            var grid = gridGO.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(138f, 24f);
            grid.spacing = new Vector2(7f, 5f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 3;

            s_typeButtons.Clear();
            for (int i = 0; i < FilterableTypes.Length; i++)
            {
                ItemType type = FilterableTypes[i];
                var btn = CreateStyledButton(gridRT, "T_" + type, InventoryHelper.GetItemTypeLabel(type), 138f, 24f, ChipOffCol, TextPrimary, 11);
                btn.onClick.AddListener((UnityAction)(() =>
                {
                    if (s_editingRule == null) return;
                    if (!s_editingRule.Types.Add(type))
                        s_editingRule.Types.Remove(type);
                    // 用户勾选分类时若仍处于关闭状态，自动切到白名单减少操作步骤
                    if (s_editingRule.Mode == ContainerFilterMode.None && s_editingRule.HasFilterCriteria)
                        s_editingRule.Mode = ContainerFilterMode.Whitelist;
                    CommitRuleChange();
                }));
                s_typeButtons[type] = btn;
            }
        }

        private static void BuildFeatureSection(RectTransform parent)
        {
            var box = CreateSectionBox(parent, "FeatureSection");
            var title = CreateLabel(box.transform, "SecTitle", Plugin.L("按功能标签筛选（跨分类）", "Filter by Feature Tag"), 12, FontStyle.Bold, TextMuted, TextAnchor.MiddleLeft);
            SetRowHeight(title.gameObject, 18f);

            int rows = (FilterableFeatures.Length + 2) / 3;
            float gridHeight = rows * 24f + Math.Max(0, rows - 1) * 5f;

            var gridGO = new GameObject("FeatureGrid");
            var gridRT = gridGO.AddComponent<RectTransform>();
            gridRT.SetParent(box.transform, false);
            SetRowHeight(gridGO, gridHeight);

            var grid = gridGO.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(138f, 24f);
            grid.spacing = new Vector2(7f, 5f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 3;

            s_featureButtons.Clear();
            for (int i = 0; i < FilterableFeatures.Length; i++)
            {
                ItemFeatureType feat = FilterableFeatures[i];
                var btn = CreateStyledButton(gridRT, "F_" + feat, InventoryHelper.GetFeatureLabel(feat), 138f, 24f, ChipOffCol, TextPrimary, 11);
                btn.onClick.AddListener((UnityAction)(() =>
                {
                    if (s_editingRule == null) return;
                    if (!s_editingRule.Features.Add(feat))
                        s_editingRule.Features.Remove(feat);
                    if (s_editingRule.Mode == ContainerFilterMode.None && s_editingRule.HasFilterCriteria)
                        s_editingRule.Mode = ContainerFilterMode.Whitelist;
                    CommitRuleChange();
                }));
                s_featureButtons[feat] = btn;
            }
        }

        private static void BuildPinnedItemsSection(RectTransform parent)
        {
            var box = CreateSectionBox(parent, "PinnedSection");

            var topRow = CreatePlainRow(box.transform, "PinnedHeader", 24f, 6f);
            var lbl = CreateLabel(topRow.transform, "Lbl", Plugin.L("指定物品（精确 ID）", "Pinned Specific Items"), 12, FontStyle.Bold, TextMuted, TextAnchor.MiddleLeft);
            var lblLE = lbl.gameObject.AddComponent<LayoutElement>();
            lblLE.minWidth = 80f;
            lblLE.preferredWidth = 80f;
            lblLE.flexibleWidth = 1f;
            lblLE.minHeight = 24f;
            lblLE.preferredHeight = 24f;

            var importBtn = CreateStyledButton(topRow.transform, "ImportBtn", Plugin.L("+ 导入当前容器物品", "+ Import Current Items"), 148f, 24f, ChipOnCyan, TextPrimary, 11);
            importBtn.onClick.AddListener((UnityAction)(() => ImportCurrentContainerItems()));

            var clearIdsBtn = CreateStyledButton(topRow.transform, "ClearIdsBtn", Plugin.L("清空", "Clear"), 48f, 24f, ChipOffCol, TextMuted, 11);
            clearIdsBtn.onClick.AddListener((UnityAction)(() =>
            {
                if (s_editingRule == null || s_editingRule.ItemIds.Count == 0) return;
                s_editingRule.ItemIds.Clear();
                CommitRuleChange();
            }));

            var chipsGO = new GameObject("PinnedGrid");
            var chipsRT = chipsGO.AddComponent<RectTransform>();
            chipsRT.SetParent(box.transform, false);
            SetRowHeight(chipsGO, 53f);

            var grid = chipsGO.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(138f, 24f);
            grid.spacing = new Vector2(7f, 5f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 3;
            s_pinnedChipsContainer = chipsRT;

            // 空状态提示脱离 GridLayoutGroup 排版，居中铺满展示区
            s_pinnedEmptyHint = CreateLabel(
                chipsRT,
                "EmptyHint",
                Plugin.L("暂无指定物品；可将常用物品放入本容器后点击“导入”。", "No pinned items yet. Put items in this container and click Import."),
                11, FontStyle.Normal, TextMuted, TextAnchor.MiddleLeft);
            var hintLE = s_pinnedEmptyHint.gameObject.AddComponent<LayoutElement>();
            hintLE.ignoreLayout = true;
            var hintRT = s_pinnedEmptyHint.GetComponent<RectTransform>();
            hintRT.anchorMin = Vector2.zero;
            hintRT.anchorMax = Vector2.one;
            hintRT.offsetMin = Vector2.zero;
            hintRT.offsetMax = Vector2.zero;
        }

        private static void BuildFooter(RectTransform parent)
        {
            var row = CreatePlainRow(parent, "Footer", 32f, 8f);

            var clearAllBtn = CreateStyledButton(row.transform, "ClearAllBtn", Plugin.L("清空全部规则", "Clear All Rules"), 124f, 30f, DangerCol, TextPrimary, 12);
            clearAllBtn.onClick.AddListener((UnityAction)(() =>
            {
                if (s_editingRule == null) return;
                s_editingRule.Reset();
                CommitRuleChange();
            }));

            var spacer = new GameObject("Spacer");
            spacer.AddComponent<RectTransform>().SetParent(row.transform, false);
            var spacerLE = spacer.AddComponent<LayoutElement>();
            spacerLE.minWidth = 0f;
            spacerLE.preferredWidth = 0f;
            spacerLE.flexibleWidth = 1f;
            spacerLE.minHeight = 1f;

            var doneBtn = CreateStyledButton(row.transform, "DoneBtn", Plugin.L("完成", "Done"), 96f, 30f, ChipOnCyan, TextPrimary, 13);
            doneBtn.onClick.AddListener((UnityAction)(() => ClosePopup()));
        }

        private static void ImportCurrentContainerItems()
        {
            if (s_editingRule == null || s_editingInventory == null) return;
            var list = s_editingInventory.itemList;
            if (list == null || list.Count == 0)
            {
                Plugin.Notify("当前容器内没有可导入的物品", "Container has no items to import");
                return;
            }

            int added = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it != null && it.itemId >= 0 && s_editingRule.ItemIds.Add(it.itemId))
                    added++;
            }

            if (s_editingRule.Mode == ContainerFilterMode.None && s_editingRule.ItemIds.Count > 0)
                s_editingRule.Mode = ContainerFilterMode.Whitelist;

            CommitRuleChange();
            if (added > 0)
                Plugin.Notify($"已导入 {added} 种指定物品", $"Imported {added} item type(s)");
        }

        private static void RefreshPopupVisuals()
        {
            if (s_editingRule == null) return;

            if (s_titleText != null)
                s_titleText.text = $"{s_editingTitle}  —  {Plugin.L("筛选与优先级", "Filter & Priority")}";

            SetChipColor(s_prioHighBtn, s_editingRule.Priority > 0, ChipOnAmber);
            SetChipColor(s_prioNormalBtn, s_editingRule.Priority == 0, ChipOnCyan);
            SetChipColor(s_prioLowBtn, s_editingRule.Priority < 0, ChipOnSlate);

            SetChipColor(s_modeNoneBtn, s_editingRule.Mode == ContainerFilterMode.None, ChipOnCyan);
            SetChipColor(s_modeWhiteBtn, s_editingRule.Mode == ContainerFilterMode.Whitelist, ChipOnAmber);
            SetChipColor(s_modeBlackBtn, s_editingRule.Mode == ContainerFilterMode.Blacklist, DangerCol);

            if (s_matchExistingBtn != null && s_matchExistingText != null)
            {
                bool on = s_editingRule.MatchExistingTypesOnly;
                SetChipColor(s_matchExistingBtn, on, ChipOnCyan);
                s_matchExistingText.text = on
                    ? Plugin.L("✓ 智能匹配：自动收入容器内已有同款物品", "✓ Auto-match: accept items already inside")
                    : Plugin.L("☐ 智能匹配：自动收入容器内已有同款物品", "☐ Auto-match: accept items already inside");
            }

            bool filterActive = s_editingRule.Mode != ContainerFilterMode.None;
            Color activeFilterCol = s_editingRule.Mode == ContainerFilterMode.Blacklist ? DangerCol : ChipOnAmber;

            foreach (var kv in s_typeButtons)
            {
                bool active = filterActive && s_editingRule.Types.Contains(kv.Key);
                SetChipColor(kv.Value, active, activeFilterCol);
            }

            foreach (var kv in s_featureButtons)
            {
                bool active = filterActive && s_editingRule.Features.Contains(kv.Key);
                SetChipColor(kv.Value, active, activeFilterCol);
            }

            RebuildPinnedItemChips();
            ForceRebuildPopupLayout();
        }

        private static void RebuildPinnedItemChips()
        {
            if (s_pinnedChipsContainer == null) return;

            for (int i = s_pinnedChipsContainer.childCount - 1; i >= 0; i--)
            {
                var child = s_pinnedChipsContainer.GetChild(i);
                if (s_pinnedEmptyHint != null && child == s_pinnedEmptyHint.transform)
                    continue;
                UnityEngine.Object.Destroy(child.gameObject);
            }

            if (s_editingRule == null || s_editingRule.ItemIds.Count == 0)
            {
                if (s_pinnedEmptyHint != null) s_pinnedEmptyHint.gameObject.SetActive(true);
                return;
            }

            if (s_pinnedEmptyHint != null) s_pinnedEmptyHint.gameObject.SetActive(false);

            bool filterActive = s_editingRule.Mode != ContainerFilterMode.None;
            Color chipBg = !filterActive
                ? ChipOffCol
                : (s_editingRule.Mode == ContainerFilterMode.Blacklist ? DangerCol : ChipOnAmber);
            Color chipText = !filterActive
                ? TextMuted
                : (s_editingRule.Mode == ContainerFilterMode.Blacklist ? Color.white : BtnActiveText);

            const int maxVisible = 6;
            int shown = 0;
            int total = s_editingRule.ItemIds.Count;

            foreach (int itemId in s_editingRule.ItemIds)
            {
                if (shown >= maxVisible - 1 && total > maxVisible)
                {
                    int remaining = total - shown;
                    var moreLbl = CreateStyledButton(
                        s_pinnedChipsContainer,
                        "MoreChip",
                        Plugin.L($"… 另有 {remaining} 种", $"... +{remaining} more"),
                        138f, 24f, ChipOffCol, TextMuted, 11);
                    moreLbl.interactable = false;
                    break;
                }

                int capturedId = itemId;
                string name = InventoryHelper.GetItemDisplayName(itemId);
                if (name.Length > 8) name = name.Substring(0, 7) + "…";
                var chip = CreateStyledButton(
                    s_pinnedChipsContainer,
                    "Pin_" + itemId,
                    name + " ×",
                    138f, 24f,
                    chipBg,
                    chipText, 11);

                chip.onClick.AddListener((UnityAction)(() =>
                {
                    if (s_editingRule == null) return;
                    s_editingRule.ItemIds.Remove(capturedId);
                    CommitRuleChange();
                }));

                shown++;
            }
        }

        private static void SetChipColor(Button btn, bool active, Color activeColor)
        {
            if (btn == null) return;
            var img = btn.GetComponent<Image>();
            if (img != null) img.color = active ? activeColor : ChipOffCol;
            var txt = btn.GetComponentInChildren<Text>(true);
            if (txt != null)
            {
                txt.color = active
                    ? (activeColor == ChipOnAmber ? BtnActiveText : Color.white)
                    : TextPrimary;
            }
        }

        private static void SetRowHeight(GameObject go, float height)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            le.flexibleHeight = 0f;
        }

        private static GameObject CreateBoxRow(RectTransform parent, string name, Color bg, float height)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            SetRowHeight(go, height);
            var img = go.AddComponent<Image>();
            img.color = bg;
            return go;
        }

        private static GameObject CreateSectionBox(RectTransform parent, string name)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);

            var img = go.AddComponent<Image>();
            img.color = SectionBgCol;

            var v = go.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(10, 10, 7, 7);
            v.spacing = 5f;
            v.childAlignment = TextAnchor.UpperLeft;
            v.childControlWidth = true;
            v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;
            return go;
        }

        private static GameObject CreatePlainRow(Transform parent, string name, float height, float spacing)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            SetRowHeight(go, height);

            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;
            return go;
        }

        private static Text CreateLabel(Transform parent, string name, string content, int fontSize, FontStyle style, Color color, TextAnchor align)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);

            var txt = go.AddComponent<Text>();
            txt.font = GetUiFont();
            txt.text = content;
            txt.fontSize = fontSize;
            txt.fontStyle = style;
            txt.color = color;
            txt.alignment = align;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;
            return txt;
        }

        private static Button CreateStyledButton(
            Transform parent,
            string name,
            string label,
            float width,
            float height,
            Color bg,
            Color textColor,
            int fontSize)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(width, height);

            var le = go.AddComponent<LayoutElement>();
            le.minWidth = width;
            le.preferredWidth = width;
            le.flexibleWidth = 0f;
            le.minHeight = height;
            le.preferredHeight = height;
            le.flexibleHeight = 0f;

            var img = go.AddComponent<Image>();
            img.color = bg;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            DisableSelectionHighlight(btn);

            var txt = CreateLabel(rt, "Text", label, fontSize, FontStyle.Normal, textColor, TextAnchor.MiddleCenter);
            txt.resizeTextForBestFit = true;
            txt.resizeTextMinSize = 9;
            txt.resizeTextMaxSize = fontSize;
            var trt = txt.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(4f, 0f);
            trt.offsetMax = new Vector2(-4f, 0f);
            return btn;
        }

        private static Button CreateFlexButton(Transform parent, string name, string label, float height, int fontSize)
        {
            var btn = CreateStyledButton(parent, name, label, 0f, height, ChipOffCol, TextPrimary, fontSize);
            var le = btn.GetComponent<LayoutElement>();
            le.minWidth = 0f;
            le.preferredWidth = 0f;
            le.flexibleWidth = 1f;
            return btn;
        }
    }

    // -------------------------------------------------------------------------
    // 容器标题栏按钮挂接与弹窗 Esc 关闭生命周期补丁
    // -------------------------------------------------------------------------

    [HarmonyPatch(typeof(PlayerInventoryPanel), nameof(PlayerInventoryPanel.LayoutPanelChrome))]
    internal static class Patch_PlayerInventoryPanel_LayoutChrome_UI
    {
        [HarmonyPostfix]
        private static void Postfix(PlayerInventoryPanel __instance)
        {
            ContainerFilterUI.RefreshPanelHeader(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerInventoryPanel), nameof(PlayerInventoryPanel.Refresh))]
    internal static class Patch_PlayerInventoryPanel_Refresh_UI
    {
        [HarmonyPostfix]
        private static void Postfix(PlayerInventoryPanel __instance)
        {
            ContainerFilterUI.RefreshPanelHeader(__instance);
        }
    }

    [HarmonyPatch(typeof(LootInventoryPanel), nameof(LootInventoryPanel.Refresh))]
    internal static class Patch_LootInventoryPanel_Refresh_UI
    {
        [HarmonyPostfix]
        private static void Postfix(LootInventoryPanel __instance)
        {
            ContainerFilterUI.RefreshPanelHeader(__instance);
        }
    }

    [HarmonyPatch(typeof(LootInventoryPanel), nameof(LootInventoryPanel.SetTitle), typeof(string))]
    internal static class Patch_LootInventoryPanel_SetTitle_UI
    {
        [HarmonyPostfix]
        private static void Postfix(LootInventoryPanel __instance)
        {
            ContainerFilterUI.RefreshPanelHeader(__instance);
        }
    }

    [HarmonyPatch(typeof(LootInventoryPanel), nameof(LootInventoryPanel.LayoutBottomButtons))]
    internal static class Patch_LootInventoryPanel_LayoutBottomButtons_UI
    {
        [HarmonyPostfix]
        private static void Postfix(LootInventoryPanel __instance)
        {
            ContainerFilterUI.RefreshPanelHeader(__instance);
        }
    }

    [HarmonyPatch(typeof(CommonInventoryPanel), nameof(CommonInventoryPanel.Refresh))]
    internal static class Patch_CommonInventoryPanel_Refresh_UI
    {
        [HarmonyPostfix]
        private static void Postfix(CommonInventoryPanel __instance)
        {
            ContainerFilterUI.RefreshPanelHeader(__instance);
        }
    }

    [HarmonyPatch(typeof(CommonInventoryPanel), nameof(CommonInventoryPanel.RefreshTitle))]
    internal static class Patch_CommonInventoryPanel_RefreshTitle_UI
    {
        [HarmonyPostfix]
        private static void Postfix(CommonInventoryPanel __instance)
        {
            ContainerFilterUI.RefreshPanelHeader(__instance);
        }
    }

    [HarmonyPatch(typeof(InventoryButtonRow), nameof(InventoryButtonRow.Layout))]
    internal static class Patch_InventoryButtonRow_Layout_UI
    {
        [HarmonyPostfix]
        private static void Postfix(InventoryButtonRow __instance)
        {
            if (__instance != null)
                ContainerFilterUI.RefreshPanelHeader(__instance.panel);
        }
    }

    [HarmonyPatch(typeof(PlayerInventoryPanel), nameof(PlayerInventoryPanel.Update))]
    internal static class Patch_PlayerInventoryPanel_Update_PopupEsc
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            if (ContainerFilterUI.IsPopupOpen && GameInputManager.GetKeyDown(KeyCode.Escape))
                ContainerFilterUI.ClosePopup();
        }
    }

    [HarmonyPatch(typeof(PlayerPanel), nameof(PlayerPanel.ClosePanel))]
    internal static class Patch_PlayerPanel_ClosePanel_ClosePopup
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            ContainerFilterUI.ClosePopup();
        }
    }
}
