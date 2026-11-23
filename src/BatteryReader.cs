using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace BatteryLevel
{
    enum DeviceKind { Mouse, Keyboard, Headset, Speaker, Gamepad, Phone, Pen, Laptop, Other }

    sealed class DeviceBattery
    {
        public string Id;
        public string Name;          // 显示名（可能是用户设置的别名）
        public string DefaultName;   // 设备自身报告的名称
        public DeviceKind Kind;
        public Guid Container;     // Windows 设备容器 ID：同一物理设备/接收器的所有节点共享
        public int Level;          // 0-100；-1 表示已发现但读不到电量
        public bool HasBattery { get { return Level >= 0; } }
        public bool Charging;
        public bool IsSystem;      // 本机电池
        public string Connection;  // 蓝牙 / 2.4G 接收器 / USB / 有线
    }

    /// <summary>
    /// 汇总各来源的电量：
    ///  1) 蓝牙 —— Windows 蓝牙栈写入的 DEVPKEY_Bluetooth_Battery（与系统设置同源）；
    ///  2) 罗技 HID++ —— Unifying / Bolt / Lightspeed 接收器及有线设备；
    ///  3) 雷蛇 —— 私有特性报告协议；
    ///  4) 标准 HID 电量字段 —— 不分品牌；
    ///  5) 本机电池。
    /// </summary>
    static class BatteryReader
    {
        static readonly Native.DEVPROPKEY KeyBattery = new Native.DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        static readonly Native.DEVPROPKEY KeyFriendlyName = new Native.DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        static readonly Native.DEVPROPKEY KeyDeviceDesc = new Native.DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
        static readonly Native.DEVPROPKEY KeyConnected = new Native.DEVPROPKEY("83DA6326-97A6-4088-9453-A1923F573B29", 15);
        static readonly Native.DEVPROPKEY KeyCategory = new Native.DEVPROPKEY("78C34FC8-104A-4ACA-9EA4-524D52996E57", 90);
        static readonly Native.DEVPROPKEY KeyClassOfDevice = new Native.DEVPROPKEY("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A", 10);

        static readonly Guid MouseClass = new Guid("4D36E96F-E325-11CE-BFC1-08002BE10318");
        static readonly Guid KeyboardClass = new Guid("4D36E96B-E325-11CE-BFC1-08002BE10318");

        sealed class Node
        {
            public string InstanceId;
            public string Name;
            public Guid Container;
            public int Battery = -1;
            public int Connected = -1;   // -1 未知, 0 否, 1 是
            public string Category;
            public uint ClassOfDevice;
            public bool IsDeviceNode;    // BTHENUM\DEV_xxx 或 BTHLE\DEV_xxx
        }

        public static List<DeviceBattery> ReadAll()
        {
            var result = new List<DeviceBattery>();
            var bt = new Dictionary<Guid, Discovery.BtDevice>();
            List<HidInfo> hids = null;
            try { result.AddRange(ReadBluetooth(bt)); } catch { }
            try { hids = Hid.EnumerateUsb(); ReadUsbHid(hids, result); } catch { }
            // 最后补上读不到电量的鼠标/键盘，至少让用户知道它们已被识别
            try { Discovery.Read(hids ?? new List<HidInfo>(), bt, result); } catch { }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            try
            {
                var sys = ReadSystemBattery();
                if (sys != null) result.Insert(0, sys);
            }
            catch { }
            return result;
        }

        static void ReadUsbHid(List<HidInfo> hids, List<DeviceBattery> output)
        {
            var vendorHandled = new HashSet<Guid>();
            foreach (var h in hids)
                if (h.Vid == 0x046D || h.Vid == 0x1532) vendorHandled.Add(h.Container);

            try { Logitech.Read(hids, output); } catch { }
            try { Razer.Read(hids, output); } catch { }
            try { StandardHid.Read(hids, output, vendorHandled); } catch { }
            try { PassiveHid.Read(hids, output, vendorHandled); } catch { }
        }

        static DeviceBattery ReadSystemBattery()
        {
            var ps = SystemInformation.PowerStatus;
            var st = ps.BatteryChargeStatus;
            if ((st & BatteryChargeStatus.NoSystemBattery) != 0 || st == BatteryChargeStatus.Unknown) return null;
            var lvl = ps.BatteryLifePercent;
            if (lvl < 0 || lvl > 1) return null;
            var d = new DeviceBattery();
            d.Id = "system";
            d.Name = "本机电池";
            d.Kind = DeviceKind.Laptop;
            d.Level = (int)Math.Round(lvl * 100);
            d.Charging = (st & BatteryChargeStatus.Charging) != 0 || ps.PowerLineStatus == PowerLineStatus.Online;
            d.IsSystem = true;
            d.Connection = "本机";
            return d;
        }

        // ───────────────────────────── 蓝牙 ─────────────────────────────

        static IEnumerable<DeviceBattery> ReadBluetooth(Dictionary<Guid, Discovery.BtDevice> btAll)
        {
            var nodes = new List<Node>();
            foreach (var en in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE" })
                Enumerate(en, nodes);

            var mice = CollectContainers(MouseClass);
            var keyboards = CollectContainers(KeyboardClass);

            var groups = new Dictionary<Guid, List<Node>>();
            foreach (var n in nodes)
            {
                List<Node> list;
                if (!groups.TryGetValue(n.Container, out list)) { list = new List<Node>(); groups[n.Container] = list; }
                list.Add(n);
            }

            var output = new List<DeviceBattery>();
            foreach (var kv in groups)
            {
                int battery = -1;
                bool anyConnected = false, anyDisconnected = false;
                Node dev = null;
                string category = null; uint cod = 0;
                foreach (var n in kv.Value)
                {
                    if (n.Battery > battery) battery = n.Battery;
                    if (n.Connected == 1) anyConnected = true;
                    if (n.Connected == 0) anyDisconnected = true;
                    if (n.IsDeviceNode && dev == null) dev = n;
                    if (category == null && !string.IsNullOrEmpty(n.Category)) category = n.Category;
                    if (cod == 0 && n.ClassOfDevice != 0) cod = n.ClassOfDevice;
                }

                string name = dev != null ? dev.Name : null;
                if (string.IsNullOrEmpty(name))
                    foreach (var n in kv.Value)
                        if (!string.IsNullOrEmpty(n.Name)) { name = CleanName(n.Name); break; }
                if (string.IsNullOrEmpty(name)) name = "蓝牙设备";

                // 记录所有蓝牙设备（含无电量的），供自动发现使用
                var info = new Discovery.BtDevice();
                info.Name = name;
                info.Connected = anyConnected || !anyDisconnected;
                info.Kind = GuessKind(category, cod, name);
                btAll[kv.Key] = info;

                if (battery < 0 || battery > 100) continue;
                if (anyDisconnected && !anyConnected) continue;   // 已配对但未连接，电量是旧值

                var d = new DeviceBattery();
                d.Id = kv.Key.ToString();
                d.Container = kv.Key;
                d.Name = name;
                d.Level = battery;
                d.Connection = "蓝牙";
                if (mice.Contains(kv.Key)) d.Kind = DeviceKind.Mouse;
                else if (keyboards.Contains(kv.Key)) d.Kind = DeviceKind.Keyboard;
                else d.Kind = GuessKind(category, cod, name);
                output.Add(d);
            }
            return output;
        }

        static void Enumerate(string enumerator, List<Node> nodes)
        {
            IntPtr set = Native.SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, Native.DIGCF_ALLCLASSES | Native.DIGCF_PRESENT);
            if (set == Native.INVALID_HANDLE_VALUE || set == IntPtr.Zero) return;
            try
            {
                var data = Native.NewDevInfo();
                var sb = new StringBuilder(512);
                for (uint i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    int req;
                    sb.Length = 0;
                    if (!Native.SetupDiGetDeviceInstanceIdW(set, ref data, sb, sb.Capacity, out req)) continue;
                    var n = new Node();
                    n.InstanceId = sb.ToString();
                    var up = n.InstanceId.ToUpperInvariant();
                    n.IsDeviceNode = up.StartsWith("BTHENUM\\DEV_") || up.StartsWith("BTHLE\\DEV_");

                    n.Container = Native.GetGuid(set, ref data, Native.KeyContainer);
                    if (n.Container == Guid.Empty) continue;

                    var b = Native.GetProp(set, ref data, KeyBattery);
                    if (b != null && b.Length >= 1) n.Battery = b[0];

                    var cn = Native.GetProp(set, ref data, KeyConnected);
                    if (cn != null && cn.Length >= 1) n.Connected = cn[0] != 0 ? 1 : 0;

                    if (n.IsDeviceNode || n.Battery >= 0)
                    {
                        n.Name = Native.GetString(set, ref data, KeyFriendlyName) ?? Native.GetString(set, ref data, KeyDeviceDesc);
                        n.Category = Native.GetString(set, ref data, KeyCategory);
                        var cod = Native.GetProp(set, ref data, KeyClassOfDevice);
                        if (cod != null && cod.Length >= 4) n.ClassOfDevice = BitConverter.ToUInt32(cod, 0);
                    }
                    nodes.Add(n);
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
        }

        static HashSet<Guid> CollectContainers(Guid classGuid)
        {
            var set = new HashSet<Guid>();
            IntPtr h = Native.SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, Native.DIGCF_PRESENT);
            if (h == Native.INVALID_HANDLE_VALUE || h == IntPtr.Zero) return set;
            try
            {
                var data = Native.NewDevInfo();
                for (uint i = 0; Native.SetupDiEnumDeviceInfo(h, i, ref data); i++)
                {
                    var g = Native.GetGuid(h, ref data, Native.KeyContainer);
                    if (g != Guid.Empty) set.Add(g);
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(h); }
            return set;
        }

        static string CleanName(string name)
        {
            foreach (var suffix in new[] { " Hands-Free AG", " Hands-Free", " Avrcp 传输", " Avrcp Transport", " Stereo", " Hands-Free HF Audio" })
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(0, name.Length - suffix.Length);
            return name;
        }

        static DeviceKind GuessKind(string category, uint cod, string name)
        {
            var c = (category ?? "").ToLowerInvariant();
            if (c.Contains("mouse")) return DeviceKind.Mouse;
            if (c.Contains("keyboard")) return DeviceKind.Keyboard;
            if (c.Contains("gaming") || c.Contains("gamepad") || c.Contains("joystick")) return DeviceKind.Gamepad;
            if (c.Contains("headset") || c.Contains("headphone")) return DeviceKind.Headset;
            if (c.Contains("speaker")) return DeviceKind.Speaker;
            if (c.Contains("phone")) return DeviceKind.Phone;
            if (c.Contains("pen")) return DeviceKind.Pen;
            if (c.Contains("audio")) return DeviceKind.Headset;

            if (cod != 0)
            {
                uint major = (cod >> 8) & 0x1F;
                if (major == 2) return DeviceKind.Phone;
                if (major == 4)
                {
                    uint minor = (cod >> 2) & 0x3F;
                    return minor == 5 ? DeviceKind.Speaker : DeviceKind.Headset;
                }
                if (major == 5)
                {
                    uint kb = (cod >> 6) & 0x3;
                    if (kb == 2) return DeviceKind.Mouse;
                    if (kb == 1 || kb == 3) return DeviceKind.Keyboard;
                    uint sub = (cod >> 2) & 0xF;
                    if (sub == 1 || sub == 2) return DeviceKind.Gamepad;
                }
            }

            var n = (name ?? "").ToLowerInvariant();
            if (n.Contains("mouse") || n.Contains("鼠标") || n.Contains("mx master") || n.Contains("trackpad")) return DeviceKind.Mouse;
            if (n.Contains("keyboard") || n.Contains("键盘") || n.Contains("keys")) return DeviceKind.Keyboard;
            if (n.Contains("controller") || n.Contains("手柄") || n.Contains("gamepad") || n.Contains("xbox") || n.Contains("dualsense")) return DeviceKind.Gamepad;
            if (n.Contains("bud") || n.Contains("pods") || n.Contains("耳机") || n.Contains("head") || n.Contains("wh-") || n.Contains("wf-")) return DeviceKind.Headset;
            if (n.Contains("speaker") || n.Contains("音箱") || n.Contains("音响")) return DeviceKind.Speaker;
            if (n.Contains("pen") || n.Contains("笔")) return DeviceKind.Pen;
            return DeviceKind.Other;
        }
    }
}
