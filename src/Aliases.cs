using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace BatteryLevel
{
    /// <summary>用户给设备起的别名，保存在 HKCU\Software\BatteryLevel\Aliases（设备 ID → 名称）。</summary>
    static class Aliases
    {
        const string KeyPath = @"Software\BatteryLevel\Aliases";

        public static string Get(string id)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                    return k == null ? null : k.GetValue(id) as string;
            }
            catch { return null; }
        }

        public static void Set(string id, string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (string.IsNullOrWhiteSpace(name)) k.DeleteValue(id, false);
                    else k.SetValue(id, name.Trim());
                }
            }
            catch { }
        }

        public static void Remove(string id) { Set(id, null); }

        /// <summary>删除设备的别名，包括按容器 ID 匹配到的（同一设备在其他识别方式下保存的）。</summary>
        public static void RemoveFor(DeviceBattery d)
        {
            Remove(d.Id);
            if (d.Container == Guid.Empty) return;
            var suffix = ":" + d.Container;
            foreach (var key in ReadAll().Keys)
                if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) Remove(key);
        }

        /// <summary>套用别名，并按名称排序（本机电池固定在第一行）。</summary>
        public static void Apply(List<DeviceBattery> list)
        {
            Dictionary<string, string> all = ReadAll();
            foreach (var d in list)
            {
                if (d.DefaultName == null) d.DefaultName = d.Name;
                string a;
                if (!all.TryGetValue(d.Id, out a) && d.Container != Guid.Empty)
                    a = FindByContainer(all, d.Container);
                d.Name = string.IsNullOrEmpty(a) ? d.DefaultName : a;
            }
            list.Sort((a, b) =>
            {
                if (a.IsSystem != b.IsSystem) return a.IsSystem ? -1 : 1;
                if (a.HasBattery != b.HasBattery) return a.HasBattery ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        /// <summary>
        /// 同一物理设备在不同阶段可能由不同模块识别（例如接收器上报电量前先被“自动发现”列出），
        /// ID 前缀不同但容器 ID 相同，因此按容器 ID 找回别名。
        /// </summary>
        static string FindByContainer(Dictionary<string, string> all, Guid container)
        {
            var suffix = ":" + container;
            foreach (var kv in all)
                if (kv.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        internal static Dictionary<string, string> ReadAll()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return map;
                    foreach (var n in k.GetValueNames())
                    {
                        var v = k.GetValue(n) as string;
                        if (!string.IsNullOrEmpty(v)) map[n] = v;
                    }
                }
            }
            catch { }
            return map;
        }
    }
}

namespace BatteryLevel
{
    /// <summary>
    /// 用户从列表中删除的设备，保存在 HKCU\Software\BatteryLevel\Hidden。
    /// 删除只是不再显示，点“重新获取”会清空此列表，让所有设备重新出现。
    /// </summary>
    static class HiddenDevices
    {
        const string KeyPath = @"Software\BatteryLevel\Hidden";

        /// <summary>
        /// 按物理设备（容器 ID）记录，这样同一设备换了识别方式（如先被自动发现、后上报电量）也仍保持隐藏。
        /// 罗技接收器下可挂多台设备、共享同一容器，因此按单台设备 ID 记录。
        /// </summary>
        static string KeyOf(DeviceBattery d)
        {
            if (d.Id.StartsWith("lg:") || d.Container == Guid.Empty) return d.Id;
            return "c:" + d.Container;
        }

        public static void Hide(DeviceBattery d)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                    k.SetValue(KeyOf(d), d.Name ?? "");
            }
            catch { }
        }

        public static void Clear()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false); } catch { }
        }

        /// <summary>从列表中移除已删除的设备。</summary>
        public static void Filter(List<DeviceBattery> list)
        {
            HashSet<string> hidden;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return;
                    hidden = new HashSet<string>(k.GetValueNames(), StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { return; }
            if (hidden.Count > 0) list.RemoveAll(d => hidden.Contains(KeyOf(d)));
        }
    }
}
