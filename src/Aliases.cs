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

        static Dictionary<string, string> ReadAll()
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
