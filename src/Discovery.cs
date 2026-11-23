using System;
using System.Collections.Generic;

namespace BatteryLevel
{
    /// <summary>
    /// 自动发现：找出所有已连接的外接鼠标/键盘（USB、2.4G 接收器、蓝牙），
    /// 即使读不到电量，也按设备类型列出来，让用户知道它已被识别。
    /// 判断依据是 Windows 为设备加载的驱动类别（鼠标类 / 键盘类），与品牌无关。
    /// </summary>
    static class Discovery
    {
        public sealed class BtDevice
        {
            public string Name;
            public bool Connected;
            public DeviceKind Kind;
        }

        static readonly Guid MouseClass = new Guid("4D36E96F-E325-11CE-BFC1-08002BE10318");
        static readonly Guid KeyboardClass = new Guid("4D36E96B-E325-11CE-BFC1-08002BE10318");
        // 本机容器：笔记本内置键盘、触控板等，不属于外接设备
        static readonly Guid LocalMachine = new Guid("00000000-0000-0000-FFFF-FFFFFFFFFFFF");

        const int FlagMouse = 1, FlagKeyboard = 2;

        public static void Read(List<HidInfo> hids, Dictionary<Guid, BtDevice> bt, List<DeviceBattery> output)
        {
            var known = new HashSet<Guid>();
            foreach (var d in output)
                if (d.Container != Guid.Empty) known.Add(d.Container);

            var flags = new Dictionary<Guid, int>();
            Collect(MouseClass, FlagMouse, flags);
            Collect(KeyboardClass, FlagKeyboard, flags);

            foreach (var kv in flags)
            {
                var c = kv.Key;
                if (c == Guid.Empty || c == LocalMachine || known.Contains(c)) continue;

                string product = null, connection;
                DeviceKind kind;
                BtDevice b;
                if (bt.TryGetValue(c, out b))
                {
                    if (!b.Connected) continue;
                    product = b.Name;
                    connection = "蓝牙";
                }
                else
                {
                    foreach (var h in hids)
                        if (h.Container == c) { product = Hid.GetProduct(h.Path); if (product != null) break; }
                    connection = LooksWireless(product) ? "2.4G 接收器" : "USB";
                }

                kind = KindFromName(product);
                if (kind == DeviceKind.Other)
                {
                    bool mouse = (kv.Value & FlagMouse) != 0, keyboard = (kv.Value & FlagKeyboard) != 0;
                    // 游戏鼠标常附带键盘接口（宏按键），因此同时具备时优先判为鼠标
                    kind = mouse ? DeviceKind.Mouse : keyboard ? DeviceKind.Keyboard : DeviceKind.Other;
                }

                var d = new DeviceBattery();
                d.Id = "dev:" + c;
                d.Container = c;
                d.Kind = kind;
                d.Name = IsGeneric(product) ? DefaultName(kind, connection) : product;
                d.Level = -1;
                d.Connection = connection;
                output.Add(d);
                known.Add(c);
            }
        }

        static void Collect(Guid cls, int flag, Dictionary<Guid, int> into)
        {
            IntPtr set = Native.SetupDiGetClassDevsW(ref cls, null, IntPtr.Zero, Native.DIGCF_PRESENT);
            if (set == Native.INVALID_HANDLE_VALUE || set == IntPtr.Zero) return;
            try
            {
                var data = Native.NewDevInfo();
                for (uint i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    var g = Native.GetGuid(set, ref data, Native.KeyContainer);
                    if (g == Guid.Empty) continue;
                    int f;
                    into.TryGetValue(g, out f);
                    into[g] = f | flag;
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
        }

        static bool LooksWireless(string product)
        {
            var p = (product ?? "").ToLowerInvariant();
            foreach (var k in new[] { "receiver", "wireless", "dongle", "2.4g", "nano", "unifying", "lightspeed", "hyperspeed", "无线", "接收器" })
                if (p.Contains(k)) return true;
            return false;
        }

        static bool IsGeneric(string product)
        {
            if (string.IsNullOrEmpty(product)) return true;
            var p = product.ToLowerInvariant();
            return p.Contains("composite") || p == "usb device" || p == "usb receiver" || p.StartsWith("hid ")
                || p.Contains("usb input device") || p == "usb 输入设备";
        }

        static string DefaultName(DeviceKind kind, string connection)
        {
            string k = kind == DeviceKind.Mouse ? "鼠标" : kind == DeviceKind.Keyboard ? "键盘" : "输入设备";
            if (connection == "2.4G 接收器") return "无线" + k;
            if (connection == "蓝牙") return "蓝牙" + k;
            return "USB " + k;
        }

        static DeviceKind KindFromName(string name)
        {
            var n = (name ?? "").ToLowerInvariant();
            if (n.Contains("mouse") || n.Contains("鼠标")) return DeviceKind.Mouse;
            if (n.Contains("keyboard") || n.Contains("键盘")) return DeviceKind.Keyboard;
            if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("手柄")) return DeviceKind.Gamepad;
            return DeviceKind.Other;
        }
    }
}
