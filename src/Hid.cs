using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace BatteryLevel
{
    /// <summary>一个 HID 顶层集合（Windows 下每个集合是独立的设备接口）。</summary>
    sealed class HidInfo
    {
        public string Path;
        public ushort Vid, Pid;
        public ushort UsagePage, Usage;
        public int InLen, OutLen, FeatLen;
        public Guid Container;                       // 同一个物理设备/接收器的所有集合共享
        public List<Native.HIDP_VALUE_CAPS> InputCaps = new List<Native.HIDP_VALUE_CAPS>();
        public List<Native.HIDP_VALUE_CAPS> FeatureCaps = new List<Native.HIDP_VALUE_CAPS>();

        public bool Is(ushort page, ushort usage) { return UsagePage == page && Usage == usage; }
    }

    static class Hid
    {
        static readonly Regex VidPid = new Regex(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);

        /// <summary>枚举 USB 上的 HID 集合（蓝牙 HID 路径不含 vid_，自然被排除，由蓝牙读取逻辑负责）。</summary>
        public static List<HidInfo> EnumerateUsb()
        {
            var list = new List<HidInfo>();
            Guid g;
            Native.HidD_GetHidGuid(out g);
            IntPtr set = Native.SetupDiGetClassDevsW(ref g, null, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == Native.INVALID_HANDLE_VALUE || set == IntPtr.Zero) return list;
            try
            {
                var ifd = new Native.SP_DEVICE_INTERFACE_DATA();
                ifd.cbSize = (uint)Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref ifd); i++)
                {
                    var dev = Native.NewDevInfo();
                    int req;
                    Native.SetupDiGetDeviceInterfaceDetailW(set, ref ifd, IntPtr.Zero, 0, out req, ref dev);
                    if (req <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    string path;
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref ifd, buf, req, out req, ref dev)) continue;
                        path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    }
                    finally { Marshal.FreeHGlobal(buf); }

                    if (path == null || !path.StartsWith(@"\\?\hid#vid_", StringComparison.OrdinalIgnoreCase)) continue;
                    var m = VidPid.Match(path);
                    if (!m.Success) continue;

                    var info = new HidInfo();
                    info.Path = path;
                    info.Vid = Convert.ToUInt16(m.Groups[1].Value, 16);
                    info.Pid = Convert.ToUInt16(m.Groups[2].Value, 16);
                    info.Container = Native.GetGuid(set, ref dev, Native.KeyContainer);
                    if (ReadCaps(info)) list.Add(info);
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        static bool ReadCaps(HidInfo info)
        {
            using (var h = OpenQuery(info.Path))
            {
                if (h.IsInvalid) return false;
                IntPtr pp;
                if (!Native.HidD_GetPreparsedData(h, out pp)) return false;
                try
                {
                    Native.HIDP_CAPS c;
                    if (Native.HidP_GetCaps(pp, out c) != Native.HIDP_STATUS_SUCCESS) return false;
                    info.UsagePage = c.UsagePage; info.Usage = c.Usage;
                    info.InLen = c.InputReportByteLength;
                    info.OutLen = c.OutputReportByteLength;
                    info.FeatLen = c.FeatureReportByteLength;
                    AddValueCaps(pp, Native.HidP_Input, c.NumberInputValueCaps, info.InputCaps);
                    AddValueCaps(pp, Native.HidP_Feature, c.NumberFeatureValueCaps, info.FeatureCaps);
                    return true;
                }
                finally { Native.HidD_FreePreparsedData(pp); }
            }
        }

        static void AddValueCaps(IntPtr pp, int type, ushort count, List<Native.HIDP_VALUE_CAPS> into)
        {
            if (count == 0) return;
            var arr = new Native.HIDP_VALUE_CAPS[count];
            ushort n = count;
            if (Native.HidP_GetValueCaps(type, arr, ref n, pp) != Native.HIDP_STATUS_SUCCESS) return;
            for (int i = 0; i < n; i++) into.Add(arr[i]);
        }

        /// <summary>以“零访问权限”打开：可读特性报告和描述信息，鼠标/键盘集合也能打开。</summary>
        public static SafeFileHandle OpenQuery(string path)
        {
            return Native.CreateFileW(path, 0, Native.FILE_SHARE_RW, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        }

        public static string GetProduct(string path)
        {
            using (var h = OpenQuery(path))
            {
                if (h.IsInvalid) return null;
                var buf = new byte[256];
                if (!Native.HidD_GetProductString(h, buf, buf.Length)) return null;
                var s = Encoding.Unicode.GetString(buf);
                int z = s.IndexOf('\0');
                if (z >= 0) s = s.Substring(0, z);
                s = s.Trim();
                return s.Length == 0 ? null : s;
            }
        }

        /// <summary>根据同一设备下暴露的集合类型猜测设备种类（鼠标集合优先：游戏鼠标常附带键盘集合用于宏）。</summary>
        public static DeviceKind KindFromCollections(List<HidInfo> all, Guid container, string name)
        {
            var n = (name ?? "").ToLowerInvariant();
            if (n.Contains("mouse") || n.Contains("鼠标")) return DeviceKind.Mouse;
            if (n.Contains("keyboard") || n.Contains("键盘")) return DeviceKind.Keyboard;
            if (n.Contains("headset") || n.Contains("耳机")) return DeviceKind.Headset;
            if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("手柄")) return DeviceKind.Gamepad;
            bool mouse = false, kb = false, pad = false;
            foreach (var h in all)
            {
                if (h.Container != container) continue;
                if (h.Is(0x01, 0x02)) mouse = true;
                if (h.Is(0x01, 0x06)) kb = true;
                if (h.Is(0x01, 0x04) || h.Is(0x01, 0x05)) pad = true;
            }
            if (mouse) return DeviceKind.Mouse;
            if (kb) return DeviceKind.Keyboard;
            if (pad) return DeviceKind.Gamepad;
            return DeviceKind.Other;
        }
    }

    /// <summary>支持超时的异步 HID 读写通道（读写各自独立的 OVERLAPPED）。</summary>
    sealed class HidChannel : IDisposable
    {
        readonly SafeFileHandle h;
        readonly int inLen, outLen;
        readonly IntPtr rOv, rEvt, rBuf, wOv, wEvt, wBuf;
        bool readPending;

        public IntPtr ReadEvent { get { return rEvt; } }
        public int OutLen { get { return outLen; } }

        static int OvSize { get { return IntPtr.Size * 3 + 8; } }

        public static HidChannel Open(HidInfo info) { return Open(info, false); }

        /// <summary>readOnly=true 时只申请读权限，用于被动监听（无法写入）。</summary>
        public static HidChannel Open(HidInfo info, bool readOnly)
        {
            if (info == null) return null;
            uint access = readOnly ? Native.GENERIC_READ : (Native.GENERIC_READ | Native.GENERIC_WRITE);
            var h = Native.CreateFileW(info.Path, access, Native.FILE_SHARE_RW,
                IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return null; }
            return new HidChannel(h, info.InLen, info.OutLen);
        }

        HidChannel(SafeFileHandle handle, int inLen, int outLen)
        {
            h = handle; this.inLen = inLen; this.outLen = outLen;
            rEvt = Native.CreateEventW(IntPtr.Zero, true, false, null);
            wEvt = Native.CreateEventW(IntPtr.Zero, true, false, null);
            rOv = Marshal.AllocHGlobal(OvSize);
            wOv = Marshal.AllocHGlobal(OvSize);
            rBuf = Marshal.AllocHGlobal(Math.Max(inLen, 1));
            wBuf = Marshal.AllocHGlobal(Math.Max(outLen, 1));
        }

        static void PrepareOv(IntPtr ov, IntPtr evt)
        {
            Marshal.Copy(new byte[OvSize], 0, ov, OvSize);
            Marshal.WriteIntPtr(ov, IntPtr.Size * 2 + 8, evt);
            Native.ResetEvent(evt);
        }

        public bool Write(byte[] report, int timeoutMs)
        {
            if (report.Length != outLen) return false;
            Marshal.Copy(report, 0, wBuf, outLen);
            PrepareOv(wOv, wEvt);
            if (!Native.WriteFile(h, wBuf, outLen, IntPtr.Zero, wOv))
            {
                if (Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return false;
                if (Native.WaitForSingleObject(wEvt, (uint)timeoutMs) != 0) { Cancel(wOv); return false; }
            }
            int n;
            return Native.GetOverlappedResult(h, wOv, out n, false);
        }

        /// <summary>确保有一个挂起的读请求；完成时 ReadEvent 置位。</summary>
        public bool BeginRead()
        {
            if (readPending) return true;
            PrepareOv(rOv, rEvt);
            if (!Native.ReadFile(h, rBuf, inLen, IntPtr.Zero, rOv) && Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING)
                return false;
            readPending = true;
            return true;
        }

        /// <summary>ReadEvent 置位后取出报告。</summary>
        public byte[] EndRead()
        {
            readPending = false;
            int n;
            if (!Native.GetOverlappedResult(h, rOv, out n, false) || n <= 0) return null;
            var r = new byte[n];
            Marshal.Copy(rBuf, r, 0, n);
            return r;
        }

        void Cancel(IntPtr ov)
        {
            Native.CancelIoEx(h, ov);
            int n;
            Native.GetOverlappedResult(h, ov, out n, true);
        }

        public void Dispose()
        {
            if (readPending) { Cancel(rOv); readPending = false; }
            h.Dispose();
            Native.CloseHandle(rEvt); Native.CloseHandle(wEvt);
            Marshal.FreeHGlobal(rOv); Marshal.FreeHGlobal(wOv);
            Marshal.FreeHGlobal(rBuf); Marshal.FreeHGlobal(wBuf);
        }
    }
}
