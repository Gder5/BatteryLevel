using System;
using System.Collections.Generic;

namespace BatteryLevel
{
    /// <summary>
    /// 标准 HID 电量：描述符中声明了 Battery Strength(0x06:0x20) 或
    /// Battery System 页剩余电量(0x85:0x65/0x66) 的设备，不分品牌，按标准方式读取。
    /// </summary>
    static class StandardHid
    {
        public static void Read(List<HidInfo> all, List<DeviceBattery> output, HashSet<Guid> handled)
        {
            var done = new HashSet<Guid>();
            foreach (var h in all)
            {
                if (handled.Contains(h.Container) || done.Contains(h.Container)) continue;
                int level = TryRead(h, h.FeatureCaps, Native.HidP_Feature, h.FeatLen);
                if (level < 0) level = TryRead(h, h.InputCaps, Native.HidP_Input, h.InLen);
                if (level <= 0) continue;
                done.Add(h.Container);

                var name = Hid.GetProduct(h.Path) ?? "USB 设备";
                var d = new DeviceBattery();
                d.Id = "hid:" + h.Container;
                d.Name = name;
                d.Kind = Hid.KindFromCollections(all, h.Container, name);
                d.Level = level;
                d.Connection = "USB";
                output.Add(d);
            }
        }

        static bool IsBattery(Native.HIDP_VALUE_CAPS c)
        {
            if (c.IsRange != 0) return false;
            return (c.UsagePage == 0x06 && c.Usage == 0x20) ||
                   (c.UsagePage == 0x85 && (c.Usage == 0x65 || c.Usage == 0x66));
        }

        static int TryRead(HidInfo h, List<Native.HIDP_VALUE_CAPS> caps, int type, int len)
        {
            if (len <= 0) return -1;
            foreach (var c in caps)
            {
                if (!IsBattery(c)) continue;
                try
                {
                    using (var handle = Hid.OpenQuery(h.Path))
                    {
                        if (handle.IsInvalid) return -1;
                        IntPtr pp;
                        if (!Native.HidD_GetPreparsedData(handle, out pp)) return -1;
                        try
                        {
                            var buf = new byte[len];
                            buf[0] = c.ReportID;
                            bool ok = type == Native.HidP_Feature
                                ? Native.HidD_GetFeature(handle, buf, len)
                                : Native.HidD_GetInputReport(handle, buf, len);
                            if (!ok) continue;
                            uint v;
                            if (Native.HidP_GetUsageValue(type, c.UsagePage, c.LinkCollection, c.Usage, out v, pp, buf, (uint)len)
                                != Native.HIDP_STATUS_SUCCESS) continue;
                            int value = (int)v;
                            if (c.LogicalMax > c.LogicalMin)
                                value = (int)Math.Round((value - c.LogicalMin) * 100.0 / (c.LogicalMax - c.LogicalMin));
                            if (value >= 0 && value <= 100) return value;
                        }
                        finally { Native.HidD_FreePreparsedData(pp); }
                    }
                }
                catch { }
            }
            return -1;
        }
    }
}
