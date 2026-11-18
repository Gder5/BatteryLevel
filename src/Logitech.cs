using System;
using System.Collections.Generic;
using System.Text;

namespace BatteryLevel
{
    /// <summary>
    /// 罗技 HID++ 2.0：Unifying / Bolt / Lightspeed 接收器及有线 HID++ 设备。
    /// 只发送查询类指令（Root.GetFeature、电量、名称），不修改任何设备设置。
    /// 协议参考开源项目 Solaar。
    /// </summary>
    static class Logitech
    {
        const ushort Vid = 0x046D;
        const byte SwId = 0x0D;            // 软件 ID，用于区分自己的应答与 G HUB/Options 的应答
        const int TimeoutMs = 600;

        const ushort FeatDeviceName = 0x0005;
        const ushort FeatBatteryStatus = 0x1000;
        const ushort FeatBatteryVoltage = 0x1001;
        const ushort FeatUnifiedBattery = 0x1004;

        sealed class Info
        {
            public byte Unified, Status, Voltage;
            public string Name;
            public DeviceKind Kind;
        }

        // 缓存每个设备的特性索引与名称，之后每次刷新只需一条电量查询
        static readonly Dictionary<string, Info> cache = new Dictionary<string, Info>();

        /// <summary>HID++ 端口：短报告(0x10, 7 字节)与长报告(0x11, 20 字节)在 Windows 下是两个集合，应答可能出现在任意一个上。</summary>
        sealed class Port : IDisposable
        {
            public HidChannel Short, Long;

            public void Dispose()
            {
                if (Short != null) Short.Dispose();
                if (Long != null) Long.Dispose();
            }

            public byte[] Receive(int timeoutMs)
            {
                var chans = new List<HidChannel>();
                if (Short != null && Short.BeginRead()) chans.Add(Short);
                if (Long != null && Long.BeginRead()) chans.Add(Long);
                if (chans.Count == 0) return null;
                var evts = new IntPtr[chans.Count];
                for (int i = 0; i < evts.Length; i++) evts[i] = chans[i].ReadEvent;
                uint w = Native.WaitForMultipleObjects((uint)evts.Length, evts, false, (uint)Math.Max(0, timeoutMs));
                if (w >= evts.Length) return null;
                return chans[(int)w].EndRead();
            }
        }

        public static void Read(List<HidInfo> all, List<DeviceBattery> output)
        {
            var seen = new HashSet<Guid>();
            foreach (var h in all)
            {
                if (h.Vid != Vid || seen.Contains(h.Container)) continue;
                HidInfo longInfo = null, shortInfo = null;
                foreach (var x in all)
                {
                    if (x.Vid != Vid || x.Container != h.Container) continue;
                    if (x.UsagePage != 0xFF00 && x.UsagePage != 0xFF43) continue;
                    if (x.InLen == 20 && x.OutLen == 20 && longInfo == null) longInfo = x;
                    else if (x.InLen == 7 && x.OutLen == 7 && shortInfo == null) shortInfo = x;
                }
                if (longInfo == null) continue;
                seen.Add(h.Container);

                bool receiver = (h.Pid & 0xFF00) == 0xC500;
                try
                {
                    using (var port = new Port())
                    {
                        port.Long = HidChannel.Open(longInfo);
                        port.Short = HidChannel.Open(shortInfo);
                        if (port.Long == null) continue;
                        if (receiver)
                        {
                            for (byte idx = 1; idx <= 6; idx++)
                            {
                                var d = Query(port, h.Container, idx, "2.4G 接收器");
                                if (d != null) output.Add(d);
                            }
                        }
                        else
                        {
                            var d = Query(port, h.Container, 0xFF, "有线");
                            if (d != null) output.Add(d);
                        }
                    }
                }
                catch { }
            }
        }

        static DeviceBattery Query(Port port, Guid container, byte idx, string connection)
        {
            string key = container + "|" + idx;
            Info info;
            if (!cache.TryGetValue(key, out info))
            {
                info = Discover(port, idx);
                if (info == null) return null;     // 空槽位或设备休眠，下次再试
                cache[key] = info;
            }
            if (info.Unified == 0 && info.Status == 0 && info.Voltage == 0) return null;

            int level; bool charging;
            if (!ReadBattery(port, idx, info, out level, out charging))
            {
                cache.Remove(key);                 // 设备离线或换绑，下次重新识别
                return null;
            }
            var d = new DeviceBattery();
            d.Id = "lg:" + key;
            d.Name = info.Name;
            d.Kind = info.Kind;
            d.Level = level;
            d.Charging = charging;
            d.Connection = connection;
            return d;
        }

        static Info Discover(Port port, byte idx)
        {
            int unified = GetFeatureIndex(port, idx, FeatUnifiedBattery);
            if (unified < 0) return null;
            var info = new Info();
            info.Unified = (byte)unified;
            if (unified == 0)
            {
                info.Status = (byte)Math.Max(0, GetFeatureIndex(port, idx, FeatBatteryStatus));
                if (info.Status == 0) info.Voltage = (byte)Math.Max(0, GetFeatureIndex(port, idx, FeatBatteryVoltage));
            }
            info.Name = "罗技设备";
            info.Kind = DeviceKind.Other;
            int fn = GetFeatureIndex(port, idx, FeatDeviceName);
            if (fn > 0)
            {
                var name = ReadName(port, idx, (byte)fn);
                if (!string.IsNullOrEmpty(name)) info.Name = name;
                var t = Request(port, idx, (byte)fn, 2);
                if (t != null) info.Kind = MapType(t[0]);
            }
            if (info.Kind == DeviceKind.Other) info.Kind = GuessFromName(info.Name);
            return info;
        }

        static string ReadName(Port port, byte idx, byte feat)
        {
            var c = Request(port, idx, feat, 0);
            if (c == null || c[0] == 0) return null;
            int len = Math.Min((int)c[0], 64);
            var bytes = new List<byte>();
            while (bytes.Count < len)
            {
                var p = Request(port, idx, feat, 1, (byte)bytes.Count);
                if (p == null) break;
                int before = bytes.Count;
                for (int i = 0; i < p.Length && bytes.Count < len; i++)
                {
                    if (p[i] == 0) { len = bytes.Count; break; }
                    bytes.Add(p[i]);
                }
                if (bytes.Count == before) break;
            }
            return Encoding.UTF8.GetString(bytes.ToArray()).Trim();
        }

        static bool ReadBattery(Port port, byte idx, Info info, out int level, out bool charging)
        {
            level = -1; charging = false;
            if (info.Unified != 0)
            {
                // UNIFIED_BATTERY.get_status: [0]=电量%, [1]=等级标志, [2]=充电状态, [3]=外部电源
                var p = Request(port, idx, info.Unified, 1);
                if (p == null) return false;
                level = p[0];
                if (level == 0)
                {
                    if ((p[1] & 8) != 0) level = 90;
                    else if ((p[1] & 4) != 0) level = 50;
                    else if ((p[1] & 2) != 0) level = 20;
                    else if ((p[1] & 1) != 0) level = 5;
                }
                charging = p[2] == 1 || p[2] == 2 || p[2] == 3;
            }
            else if (info.Status != 0)
            {
                // BATTERY_STATUS.get_level: [0]=电量%, [1]=下一档, [2]=状态
                var p = Request(port, idx, info.Status, 0);
                if (p == null) return false;
                level = p[0];
                charging = p[2] >= 1 && p[2] <= 4;
            }
            else if (info.Voltage != 0)
            {
                // BATTERY_VOLTAGE.get_voltage: [0..1]=毫伏, [2]=标志
                var p = Request(port, idx, info.Voltage, 0);
                if (p == null) return false;
                int mv = (p[0] << 8) | p[1];
                level = VoltageToPercent(mv);
                charging = (p[2] & 0x80) != 0;
            }
            return level > 0 && level <= 100;
        }

        static readonly int[,] VoltageCurve =
        {
            { 4186, 100 }, { 4067, 90 }, { 3989, 80 }, { 3922, 70 }, { 3859, 60 }, { 3811, 50 },
            { 3778, 40 }, { 3751, 30 }, { 3717, 20 }, { 3671, 10 }, { 3646, 5 }, { 3579, 2 }, { 3500, 0 }
        };

        static int VoltageToPercent(int mv)
        {
            if (mv >= VoltageCurve[0, 0]) return 100;
            for (int i = 1; i < VoltageCurve.GetLength(0); i++)
            {
                int v1 = VoltageCurve[i, 0];
                if (mv >= v1)
                {
                    int v0 = VoltageCurve[i - 1, 0], p0 = VoltageCurve[i - 1, 1], p1 = VoltageCurve[i, 1];
                    return p1 + (p0 - p1) * (mv - v1) / (v0 - v1);
                }
            }
            return 0;
        }

        /// <summary>Root.GetFeature：返回特性索引；0=不支持；-1=无应答或出错。</summary>
        static int GetFeatureIndex(Port port, byte idx, ushort feature)
        {
            var p = Request(port, idx, 0x00, 0, (byte)(feature >> 8), (byte)(feature & 0xFF));
            return p == null ? -1 : p[0];
        }

        static byte[] Request(Port port, byte idx, byte feat, byte func, params byte[] args)
        {
            var msg = new byte[20];
            msg[0] = 0x11; msg[1] = idx; msg[2] = feat; msg[3] = (byte)((func << 4) | SwId);
            Array.Copy(args, 0, msg, 4, Math.Min(args.Length, 16));
            if (!port.Long.Write(msg, TimeoutMs)) return null;

            int deadline = Environment.TickCount + TimeoutMs;
            while (true)
            {
                int remain = deadline - Environment.TickCount;
                if (remain <= 0) return null;
                var r = port.Receive(remain);
                if (r == null) return null;
                if (r.Length < 7 || r[1] != idx || (r[0] != 0x10 && r[0] != 0x11)) continue;
                if (r[2] == feat && r[3] == msg[3])
                {
                    var p = new byte[16];
                    Array.Copy(r, 4, p, 0, Math.Min(16, r.Length - 4));
                    return p;
                }
                // HID++ 2.0 错误(0xFF) / HID++ 1.0 错误(0x8F，如接收器报告设备未连接)
                if ((r[2] == 0xFF || r[2] == 0x8F) && r[3] == feat && r[4] == msg[3]) return null;
            }
        }

        static DeviceKind MapType(byte t)
        {
            switch (t)
            {
                case 0: case 2: return DeviceKind.Keyboard;
                case 3: case 4: case 5: return DeviceKind.Mouse;
                case 8: return DeviceKind.Headset;
                case 11: case 12: return DeviceKind.Gamepad;
                case 14: return DeviceKind.Speaker;
                default: return DeviceKind.Other;
            }
        }

        static DeviceKind GuessFromName(string name)
        {
            var n = (name ?? "").ToLowerInvariant();
            if (n.Contains("mouse") || n.StartsWith("mx ") || n.Contains("lift") || n.Contains("trackball")) return DeviceKind.Mouse;
            if (n.Contains("keyboard") || n.Contains("keys") || n.StartsWith("k")) return DeviceKind.Keyboard;
            if (n.Contains("headset") || n.Contains("pro x")) return DeviceKind.Headset;
            return DeviceKind.Other;
        }
    }
}
