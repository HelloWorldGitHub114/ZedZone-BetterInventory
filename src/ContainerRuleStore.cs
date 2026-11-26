using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;

namespace ZedZone.BetterInventory
{
    internal enum ContainerFilterMode
    {
        None = 0,
        Whitelist = 1,
        Blacklist = 2
    }

    internal enum ContainerMatchKind
    {
        Rejected = 0,
        CatchAll = 1,
        ExplicitMatch = 2
    }

    /// <summary>
    /// 单个容器的拾取优先级（三档：优先装入 / 正常 / 最后兜底）与过滤规则。
    /// </summary>
    internal sealed class ContainerRule
    {
        internal const int PriorityLow = -1;
        internal const int PriorityNormal = 0;
        internal const int PriorityHigh = 1;

        internal int Priority;
        internal ContainerFilterMode Mode = ContainerFilterMode.None;
        internal bool ProtectContents;
        internal readonly HashSet<ItemType> Types = new HashSet<ItemType>();
        internal readonly HashSet<ItemFeatureType> Features = new HashSet<ItemFeatureType>();
        internal readonly HashSet<int> ItemIds = new HashSet<int>();

        internal bool HasFilterCriteria =>
            Types.Count > 0 || Features.Count > 0 || ItemIds.Count > 0;

        internal bool HasEffectiveCustomRule =>
            Priority != 0 || ProtectContents || (Mode != ContainerFilterMode.None && HasFilterCriteria);

        internal bool HasActiveFilter =>
            Mode != ContainerFilterMode.None && HasFilterCriteria;

        internal bool IsDefault => !HasEffectiveCustomRule;

        internal ContainerRule Clone()
        {
            var c = new ContainerRule
            {
                Priority = Priority,
                Mode = Mode,
                ProtectContents = ProtectContents
            };
            foreach (var t in Types) c.Types.Add(t);
            foreach (var f in Features) c.Features.Add(f);
            foreach (var id in ItemIds) c.ItemIds.Add(id);
            return c;
        }

        internal void Reset()
        {
            Priority = 0;
            Mode = ContainerFilterMode.None;
            ProtectContents = false;
            Types.Clear();
            Features.Clear();
            ItemIds.Clear();
        }

        /// <summary>
        /// 将规则序列化为紧凑的单行字符串（示例：v1|p=1|m=1|k=1|t=1,6|f=7|i=101,102）。
        /// </summary>
        internal string Serialize()
        {
            if (IsDefault) return string.Empty;

            var sb = new StringBuilder(64);
            sb.Append("v1|p=").Append(ClampPriority(Priority));
            sb.Append("|m=").Append((int)Mode);
            if (ProtectContents)
                sb.Append("|k=1");

            if (Types.Count > 0)
            {
                sb.Append("|t=");
                bool first = true;
                foreach (var t in Types)
                {
                    if (!first) sb.Append(',');
                    sb.Append((int)t);
                    first = false;
                }
            }

            if (Features.Count > 0)
            {
                sb.Append("|f=");
                bool first = true;
                foreach (var f in Features)
                {
                    if (!first) sb.Append(',');
                    sb.Append((int)f);
                    first = false;
                }
            }

            if (ItemIds.Count > 0)
            {
                sb.Append("|i=");
                bool first = true;
                foreach (var id in ItemIds)
                {
                    if (!first) sb.Append(',');
                    sb.Append(id);
                    first = false;
                }
            }

            return sb.ToString();
        }

        internal static ContainerRule Deserialize(string raw)
        {
            var rule = new ContainerRule();
            if (string.IsNullOrWhiteSpace(raw)) return rule;

            string[] parts = raw.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                int eq = part.IndexOf('=');
                if (eq <= 0 || eq >= part.Length - 1) continue;

                string k = part.Substring(0, eq);
                string v = part.Substring(eq + 1);

                switch (k)
                {
                    case "p":
                        if (int.TryParse(v, out int prio))
                            rule.Priority = ClampPriority(prio);
                        break;
                    case "m":
                        if (int.TryParse(v, out int mode) && mode >= 0 && mode <= 2)
                            rule.Mode = (ContainerFilterMode)mode;
                        break;
                    case "e":
                        // 兼容旧存档字段：旧版同名开关已移除，读取时静默忽略
                        break;
                    case "k":
                        rule.ProtectContents = v == "1";
                        break;
                    case "t":
                        foreach (var tok in v.Split(','))
                        {
                            if (int.TryParse(tok, out int tv))
                                rule.Types.Add((ItemType)tv);
                        }
                        break;
                    case "f":
                        foreach (var tok in v.Split(','))
                        {
                            if (int.TryParse(tok, out int fv))
                                rule.Features.Add((ItemFeatureType)fv);
                        }
                        break;
                    case "i":
                        foreach (var tok in v.Split(','))
                        {
                            if (int.TryParse(tok, out int id))
                                rule.ItemIds.Add(id);
                        }
                        break;
                }
            }

            return rule;
        }

        internal static int ClampPriority(int value)
        {
            if (value < 0) return PriorityLow;
            if (value > 0) return PriorityHigh;
            return PriorityNormal;
        }
    }

    /// <summary>
    /// 容器规则读写与判定中心：
    /// - 有宿主物品的容器（子背包、挂包、衣服口袋）直接序列化在 ItemData.itemPropertyPairs 中，天然跟随存档与联机同步；
    /// - 无宿主物品的容器（角色主背包、基地储物箱）通过 BepInEx 配置目录下的轻量键值文件按 inventoryId 持久化。
    /// </summary>
    internal static class ContainerRuleStore
    {
        internal const string RulePropertyKey = "bi_rule";

        private static readonly Dictionary<string, string> SidecarRawByKey =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly Dictionary<string, ContainerRule> ParsedCache =
            new Dictionary<string, ContainerRule>(StringComparer.Ordinal);

        private static string SidecarPath =>
            Path.Combine(Paths.ConfigPath, "BetterInventory.rules.txt");

        internal static void LoadSidecar()
        {
            SidecarRawByKey.Clear();
            try
            {
                string path = SidecarPath;
                if (!File.Exists(path)) return;

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    int tab = line.IndexOf('\t');
                    if (tab <= 0 || tab >= line.Length - 1) continue;
                    string key = line.Substring(0, tab);
                    string val = line.Substring(tab + 1);
                    SidecarRawByKey[key] = val;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("load_rules_sidecar", ex);
            }
        }

        private static void SaveSidecar()
        {
            try
            {
                string path = SidecarPath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (SidecarRawByKey.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("# BetterInventory non-item container rules (inventoryId<TAB>serializedRule)");
                foreach (var kv in SidecarRawByKey)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        sb.Append(kv.Key).Append('\t').AppendLine(kv.Value);
                }
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("save_rules_sidecar", ex);
            }
        }

        private static string GetSidecarKey(InventoryData inv)
        {
            if (inv == null) return null;

            // 主背包优先使用角色 ID 作为稳定键，避免换背包或重置 inventoryId 时规则丢失
            if (InventoryHelper.IsPlayerMainInventory(inv))
            {
                string charId = InventoryHelper.LocalPlayer?.characterData?.characterId;
                if (!string.IsNullOrEmpty(charId))
                    return "player_main:" + charId;
            }

            string invId = inv.inventoryId;
            return string.IsNullOrWhiteSpace(invId) ? null : "inv:" + invId.Trim();
        }

        internal static ContainerRule GetRuleForItem(ItemData ownerItem)
        {
            if (ownerItem == null) return null;
            string raw = ownerItem.GetProperty(RulePropertyKey);
            return GetCachedParsed(raw);
        }

        internal static ContainerRule GetRule(InventoryData inv)
        {
            if (inv == null) return null;

            var owner = InventoryHelper.FindOwnerItemData(inv);
            if (owner != null)
            {
                string raw = owner.GetProperty(RulePropertyKey);
                return GetCachedParsed(raw);
            }

            string key = GetSidecarKey(inv);
            if (key != null && SidecarRawByKey.TryGetValue(key, out string sidecarRaw))
                return GetCachedParsed(sidecarRaw);

            return null;
        }

        internal static void SaveRule(InventoryData inv, ItemData explicitOwner, ContainerRule rule)
        {
            if (inv == null && explicitOwner == null) return;

            string serialized = (rule == null || rule.IsDefault) ? string.Empty : rule.Serialize();
            ItemData owner = explicitOwner ?? InventoryHelper.FindOwnerItemData(inv);

            if (owner != null)
            {
                if (string.IsNullOrEmpty(serialized))
                    InventoryHelper.RemoveItemProperty(owner, RulePropertyKey);
                else
                    owner.SetProperty(RulePropertyKey, serialized);
                return;
            }

            string key = GetSidecarKey(inv);
            if (key == null) return;

            if (string.IsNullOrEmpty(serialized))
                SidecarRawByKey.Remove(key);
            else
                SidecarRawByKey[key] = serialized;

            SaveSidecar();
        }

        private static ContainerRule GetCachedParsed(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            if (ParsedCache.TryGetValue(raw, out var cached))
                return cached;

            var parsed = ContainerRule.Deserialize(raw);
            if (ParsedCache.Count > 512)
                ParsedCache.Clear();
            ParsedCache[raw] = parsed;
            return parsed;
        }

        /// <summary>
        /// 判定某件物品是否符合目标容器的过滤规则。
        /// </summary>
        internal static ContainerMatchKind Evaluate(InventoryData inv, ItemData item, ItemAttr attr)
        {
            if (inv == null || item == null) return ContainerMatchKind.CatchAll;

            var rule = GetRule(inv);
            if (rule == null || !rule.HasActiveFilter)
                return ContainerMatchKind.CatchAll;

            bool matchesRuleCriteria = MatchesCriteria(rule, item, attr);

            if (rule.Mode == ContainerFilterMode.Whitelist)
                return matchesRuleCriteria ? ContainerMatchKind.ExplicitMatch : ContainerMatchKind.Rejected;

            if (rule.Mode == ContainerFilterMode.Blacklist)
                return matchesRuleCriteria ? ContainerMatchKind.Rejected : ContainerMatchKind.CatchAll;

            return ContainerMatchKind.CatchAll;
        }

        internal static bool Accepts(InventoryData inv, ItemData item, ItemAttr attr)
        {
            return Evaluate(inv, item, attr) != ContainerMatchKind.Rejected;
        }

        private static bool MatchesCriteria(ContainerRule rule, ItemData item, ItemAttr attr)
        {
            if (rule.ItemIds.Count > 0 && rule.ItemIds.Contains(item.itemId))
                return true;

            if (attr == null) return false;

            if (rule.Types.Count > 0 && rule.Types.Contains(attr.itemType))
                return true;

            if (rule.Features.Count > 0)
            {
                var features = attr.itemFeatures;
                if (features != null)
                {
                    for (int i = 0; i < features.Count; i++)
                    {
                        if (rule.Features.Contains(features[i]))
                            return true;
                    }
                }
            }

            return false;
        }

        internal static bool IsContainerProtected(InventoryData inv)
        {
            if (inv == null || Plugin.EnableFavorites == null || !Plugin.EnableFavorites.Value)
                return false;

            var rule = GetRule(inv);
            return rule != null && rule.ProtectContents;
        }

        internal static bool IsUIInProtectedContainer(BasicItemUI ui)
        {
            if (ui == null) return false;
            InventoryData inv = null;
            if (ui.inventoryPanel != null)
                inv = ui.inventoryPanel.inventoryData;
            else if (ui.itemdata != null && ui.itemdata.inventoryData != null)
                inv = ui.itemdata.inventoryData;
            else
                inv = BasicItemUI.dragDropSourceInventory;
            return IsContainerProtected(inv);
        }

        /// <summary>
        /// 生成标题栏按钮上的简短规则摘要标签（例如“筛选”/“护·优先·白”/“兜底”）。
        /// </summary>
        internal static string BuildCompactBadgeLabel(ContainerRule rule, out bool hasCustomRule)
        {
            if (rule == null || !rule.HasEffectiveCustomRule)
            {
                hasCustomRule = false;
                return Plugin.L("筛选", "Rule");
            }

            hasCustomRule = true;
            var parts = new List<string>(4);

            if (rule.ProtectContents)
                parts.Add(Plugin.L("护", "Keep"));

            if (rule.Priority > 0)
                parts.Add(Plugin.L("优先", "High"));
            else if (rule.Priority < 0)
                parts.Add(Plugin.L("兜底", "Low"));

            if (rule.Mode == ContainerFilterMode.Whitelist && rule.HasFilterCriteria)
                parts.Add(Plugin.L("白", "W"));
            else if (rule.Mode == ContainerFilterMode.Blacklist && rule.HasFilterCriteria)
                parts.Add(Plugin.L("黑", "B"));

            if (parts.Count == 0)
                return Plugin.L("筛选", "Rule");

            return string.Join("·", parts);
        }
    }
}
