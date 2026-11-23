using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace BatteryLevel
{
    /// <summary>
    /// 雷蛇：通过 90 字节特性报告查询电量（命令类 0x07，命令 0x80 电量 / 0x84 充电状态）。
    /// 只发送这两条查询指令。协议参考开源项目 openrazer。
    /// </summary>
    static class Razer
    {
        const ushort Vid = 0x1532;
        const int ReportLen = 91;   // 报告 ID(0) + 90 字节
        // 不同型号使用的事务 ID 不同，依次尝试；成功后记住
        static readonly byte[] TransactionIds = { 0x1F, 0xFF, 0x3F };
        static readonly Dictionary<Guid, byte> knownTid = new Dictionary<Guid, byte>();

        public static void ClearCache() { lock (knownTid) knownTid.Clear(); }

        public static void Read(List<HidInfo> all, List<DeviceBattery> output)
        {
            var done = new HashSet<Guid>();
            foreach (var h in all)
            {
                if (h.Vid != Vid || h.FeatLen != ReportLen || done.Contains(h.Container)) continue;
                try
                {
                    using (var handle = Hid.OpenQuery(h.Path))
                    {
                        if (handle.IsInvalid) continue;
                        int level = -1; bool charging = false;
                        byte tidHint;
                        var tids = knownTid.TryGetValue(h.Container, out tidHint) ? new[] { tidHint } : TransactionIds;
                        foreach (var tid in tids)
                        {
                            var r = Query(handle, tid, 0x07, 0x80);
                            if (r == null) continue;
                            knownTid[h.Container] = tid;
                            level = (int)Math.Round(r[10] * 100 / 255.0);
                            var c = Query(handle, tid, 0x07, 0x84);
                            charging = c != null && c[10] != 0;
                            break;
                        }
                        if (level <= 0) continue;   // 0 通常表示设备休眠/关机
                        done.Add(h.Container);

                        var name = Hid.GetProduct(h.Path) ?? "雷蛇设备";
                        var lower = name.ToLowerInvariant();
                        bool wireless = lower.Contains("dongle") || lower.Contains("receiver") || lower.Contains("wireless") || lower.Contains("hyperspeed");
                        foreach (var s in new[] { " Dongle", " Receiver", " (Wireless)", " Wireless Receiver" })
                            if (name.EndsWith(s, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - s.Length);

                        var d = new DeviceBattery();
                        d.Id = "rz:" + h.Container;
                        d.Container = h.Container;
                        d.Name = name;
                        d.Kind = Hid.KindFromCollections(all, h.Container, name);
                        d.Level = Math.Min(100, level);
                        d.Charging = charging;
                        d.Connection = wireless ? "2.4G 接收器" : "USB";
                        output.Add(d);
                    }
                }
                catch { }
            }
        }

        /// <summary>发送查询并返回应答（下标与发送缓冲一致：[1]状态 [2]事务ID [7]类 [8]命令 [9..]参数）。</summary>
        static byte[] Query(SafeFileHandle h, byte tid, byte cls, byte cmd)
        {
            var buf = new byte[ReportLen];
            buf[2] = tid;
            buf[6] = 0x02;      // 参数长度
            buf[7] = cls;
            buf[8] = cmd;
            byte crc = 0;
            for (int i = 3; i <= 88; i++) crc ^= buf[i];
            buf[89] = crc;
            if (!Native.HidD_SetFeature(h, buf, ReportLen)) return null;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                Thread.Sleep(attempt == 0 ? 60 : 100);
                var r = new byte[ReportLen];
                if (!Native.HidD_GetFeature(h, r, ReportLen)) return null;
                byte status = r[1];
                if (status == 0x01) continue;                    // 忙，稍后重试
                if (status != 0x02) return null;                 // 失败/超时/不支持
                if (r[2] != tid || r[7] != cls || r[8] != cmd) return null;
                return r;
            }
            return null;
        }
    }
}
