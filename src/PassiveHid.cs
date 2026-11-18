using System;
using System.Collections.Generic;
using System.Threading;

namespace BatteryLevel
{
    /// <summary>
    /// 被动监听：只读打开设备的厂商自定义 HID 通道，解析接收器主动上报的电量帧。
    /// 不向设备发送任何数据。
    ///
    /// 已知帧格式（矽力 Shinetek 等方案的 2.4G 接收器，如雷神 ML7，约每 10 秒上报一次）：
    ///   AA 55 CC 33 FD [电量%] [状态] 00 …
    /// </summary>
    static class PassiveHid
    {
        /// <summary>收到的电量有变化时触发（在监听线程上）。</summary>
        public static event Action Changed;

        static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);   // 超过 5 分钟未上报视为离线
        static readonly object sync = new object();
        static readonly Dictionary<string, Listener> listeners = new Dictionary<string, Listener>(StringComparer.OrdinalIgnoreCase);

        sealed class Listener
        {
            public HidInfo Info;
            public volatile bool Stop, Dead;
            public int Level = -1;
            public DateTime Updated;
        }

        public static void Read(List<HidInfo> all, List<DeviceBattery> output, HashSet<Guid> handled)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (sync)
            {
                foreach (var h in all)
                {
                    if (!IsCandidate(h) || handled.Contains(h.Container)) continue;
                    present.Add(h.Path);
                    Listener l;
                    if (listeners.TryGetValue(h.Path, out l) && !l.Dead) continue;
                    l = new Listener();
                    l.Info = h;
                    listeners[h.Path] = l;
                    var t = new Thread(Run);
                    t.IsBackground = true;
                    t.Name = "PassiveHid";
                    t.Start(l);
                }

                // 设备已拔出：停止对应的监听线程
                var gone = new List<string>();
                foreach (var kv in listeners)
                    if (!present.Contains(kv.Key)) { kv.Value.Stop = true; gone.Add(kv.Key); }
                foreach (var k in gone) listeners.Remove(k);

                var done = new HashSet<Guid>();
                foreach (var l in listeners.Values)
                {
                    if (l.Level <= 0 || DateTime.Now - l.Updated > MaxAge) continue;
                    if (!done.Add(l.Info.Container)) continue;
                    var d = new DeviceBattery();
                    d.Id = "pv:" + l.Info.Container;
                    d.Kind = Hid.KindFromCollections(all, l.Info.Container, null);
                    d.Name = NameFor(l.Info, d.Kind);
                    d.Level = l.Level;
                    d.Connection = "2.4G 接收器";
                    output.Add(d);
                }
            }
        }

        public static int ListenerCount { get { lock (sync) return listeners.Count; } }

        /// <summary>
        /// 诊断用：只读监听所有厂商自定义通道 seconds 秒，记录收到的原始数据。
        /// 用于为其他品牌的接收器分析电量帧格式。
        /// </summary>
        public static List<string> Sniff(int seconds)
        {
            var log = new List<string>();
            var t0 = DateTime.Now;
            var stop = false;
            var threads = new List<Thread>();
            foreach (var h in Hid.EnumerateUsb())
            {
                if (h.UsagePage < 0xFF00 || h.InLen <= 0) continue;
                var info = h;
                string tag = string.Format("{0:X4}:{1:X4} {2:X4}:{3:X4}", info.Vid, info.Pid, info.UsagePage, info.Usage);
                lock (log) log.Add("监听 " + tag + "  " + (Hid.GetProduct(info.Path) ?? ""));
                var t = new Thread(delegate ()
                {
                    try
                    {
                        using (var ch = HidChannel.Open(info, true))
                        {
                            if (ch == null) { lock (log) log.Add("无法打开 " + tag); return; }
                            while (!stop)
                            {
                                if (!ch.BeginRead()) return;
                                if (Native.WaitForSingleObject(ch.ReadEvent, 500) != 0) continue;
                                var r = ch.EndRead();
                                if (r == null) return;
                                lock (log) log.Add(string.Format("{0,7:F2}s {1}  {2}", (DateTime.Now - t0).TotalSeconds, tag, BitConverter.ToString(r)));
                            }
                        }
                    }
                    catch { }
                });
                t.IsBackground = true;
                t.Start();
                threads.Add(t);
            }
            Thread.Sleep(seconds * 1000);
            stop = true;
            foreach (var t in threads) t.Join(1500);
            lock (log) return new List<string>(log);
        }

        static bool IsCandidate(HidInfo h)
        {
            return h.UsagePage >= 0xFF00 && h.InLen >= 8;
        }

        static string NameFor(HidInfo h, DeviceKind kind)
        {
            var p = Hid.GetProduct(h.Path);
            if (!string.IsNullOrEmpty(p) && p.IndexOf("Composite", StringComparison.OrdinalIgnoreCase) < 0
                && !p.Equals("USB Receiver", StringComparison.OrdinalIgnoreCase))
                return p;
            switch (kind)
            {
                case DeviceKind.Mouse: return "无线鼠标";
                case DeviceKind.Keyboard: return "无线键盘";
                default: return "无线设备";
            }
        }

        static void Run(object state)
        {
            var l = (Listener)state;
            try
            {
                using (var ch = HidChannel.Open(l.Info, true))
                {
                    if (ch == null) return;
                    while (!l.Stop)
                    {
                        if (!ch.BeginRead()) return;
                        if (Native.WaitForSingleObject(ch.ReadEvent, 1000) != 0) continue;
                        var r = ch.EndRead();
                        if (r == null) return;            // 设备已断开
                        int level;
                        if (!TryDecode(r, out level)) continue;
                        bool changed;
                        lock (sync)
                        {
                            changed = l.Level != level;
                            l.Level = level;
                            l.Updated = DateTime.Now;
                        }
                        if (changed)
                        {
                            var h = Changed;
                            if (h != null) h();
                        }
                    }
                }
            }
            catch { }
            finally { l.Dead = true; }
        }

        /// <summary>识别已知的电量帧格式。r[0] 为报告 ID。</summary>
        public static bool TryDecode(byte[] r, out int level)
        {
            level = -1;
            for (int o = 0; o <= 1 && o + 6 < r.Length; o++)
            {
                if (r[o] == 0xAA && r[o + 1] == 0x55 && r[o + 2] == 0xCC && r[o + 3] == 0x33 && r[o + 4] == 0xFD)
                {
                    int v = r[o + 5];
                    if (v >= 1 && v <= 100) { level = v; return true; }
                    return false;
                }
            }
            return false;
        }
    }
}
