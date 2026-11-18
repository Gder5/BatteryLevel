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
            foreach (var d in list)
            {
                if (d.DefaultName == null) d.DefaultName = d.Name;
                var a = Get(d.Id);
                d.Name = string.IsNullOrEmpty(a) ? d.DefaultName : a;
            }
            list.Sort((a, b) =>
            {
                if (a.IsSystem != b.IsSystem) return a.IsSystem ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }
    }
}
