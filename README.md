# BatteryLevel 设备电量

一个常驻托盘的轻量小工具，统一查看连接到电脑上的蓝牙设备和 2.4G 无线设备（鼠标、键盘、耳机、手柄等）的电量，不需要再分别打开各家驱动软件。

- 程序只有一个约 63 KB 的 exe，无需安装。依赖 Windows 10/11 自带的 .NET Framework 4.8，不需要另装运行时。
- 面板隐藏时常驻内存（工作集）约 2 MB。
- 界面跟随系统的深色/浅色主题，带圆角卡片和按电量变色的进度条（>50% 绿、≤50% 黄、≤20% 红）。
- 托盘图标是一个小电池，填充长度和颜色表示**电量最低**的那台设备；鼠标悬停可以看到各设备的电量。
- 设备电量 ≤20% 时弹出一次提醒。
- 每 60 秒自动刷新一次；设备连接或断开时也会立即刷新。

## 使用

1. 运行 `dist\BatteryLevel.exe`，右下角会弹出面板。
2. 之后**单击托盘图标**打开面板，点击面板以外的地方或按 `Esc` 关闭，按 `F5` 或点 ↻ 刷新。
3. **右键托盘图标**：打开面板 / 立即刷新 / 开机自启动 / 退出。
4. **重命名设备**：双击设备卡片，或右键卡片选“重命名…”，输入名称后回车保存（Esc 取消）。清空名称或右键选“恢复默认名称”，就会还原成设备自身报告的名称。别名保存在注册表 `HKCU\Software\BatteryLevel\Aliases`。

> Windows 11 默认会把新的托盘图标收进 `^` 溢出区。可以把它拖到任务栏上，或在「设置 → 个性化 → 任务栏 → 其他系统托盘图标」中把 BatteryLevel 打开，常驻显示。

已在运行时再次双击 exe，会直接弹出已有的面板，不会重复启动。

## 构建

不需要安装任何 SDK，用 Windows 自带的 C# 编译器即可：

```bash
build.bat
```

产物输出到 `dist\BatteryLevel.exe`。诊断用的命令行输出：

```bash
dist\BatteryLevel.exe --dump
```

## 能读到哪些设备

| 设备 | 是否支持 | 读取方式 |
| --- | --- | --- |
| 蓝牙鼠标、键盘、手柄等（BLE HID） | ✅ | Windows 蓝牙栈写入的电量属性 `DEVPKEY_Bluetooth_Battery`，和系统设置里显示的数字同源 |
| 蓝牙耳机、音箱（经典蓝牙 HFP） | ✅ | 同上 |
| **罗技** Unifying / Bolt / Lightspeed 接收器，以及有线 HID++ 设备 | ✅ | HID++ 2.0 协议，支持统一电量(0x1004)、电量状态(0x1000)、电压(0x1001)三种特性 |
| **雷蛇** 无线接收器 / 有线设备 | ✅ 大部分型号 | 雷蛇 90 字节特性报告协议（命令 0x07/0x80） |
| 在 HID 描述符中声明了标准电量字段的 USB/2.4G 设备 | ✅ | HID Battery Strength (0x06:0x20) 或 Battery System 页 (0x85) |
| **主动上报电量的 2.4G 接收器**（已实测：雷神 ML7，矽力 Shinetek 方案） | ✅ | 只读监听厂商通道，解析接收器约每 10 秒上报一次的电量帧 `AA 55 CC 33 FD [电量] [状态]` |
| 笔记本自身电池 | ✅ | 自动显示在第一行 |
| 其他品牌的私有 2.4G 接收器 | ❓ | 先用下面的数据抓取功能看看接收器会不会主动上报 |

### 适配新的 2.4G 接收器

被动监听模块（[PassiveHid.cs](src/PassiveHid.cs)）**不区分品牌**，只按帧格式识别。所以用同一套芯片方案的其他品牌接收器，不需要改代码就能直接显示。

遇到不认识的接收器，可以先抓一次数据：

```bash
dist\BatteryLevel.exe --sniff
```

工具会只读监听所有厂商自定义通道 60 秒，结果保存为 exe 同目录下的 `sniff-时间.txt`。抓取期间打开官方驱动，记下它显示的电量，然后在数据里找对应的字节：
- **接收器会主动上报**：找到电量所在的字节后，在 `PassiveHid.TryDecode` 里加一种帧格式即可。
- **数据里看不到电量**：说明这个接收器只在被查询时才回复，需要用 USBPcap 抓下官方驱动发出的查询指令再适配。工具不会盲目向设备发送指令试探。

对罗技和雷蛇设备，工具只发送**查询指令**（读取特性索引、电量、名称、充电状态），不会修改设备的任何设置，和 G HUB、Logi Options+、Synapse 同时运行也没有冲突。协议细节参考了开源项目 [Solaar](https://github.com/pwr-Solaar/Solaar) 和 [openrazer](https://github.com/openrazer/openrazer)。

罗技和雷蛇的读取代码还没有在真实硬件上验证过。如果你的设备没有显示，请把 `dist\BatteryLevel.exe --dump --hid` 的输出发给开发者。

## 项目结构

```
src/
  BatteryReader.cs  汇总各来源电量；蓝牙部分通过 SetupAPI 读取
  Hid.cs            USB HID 集合枚举、带超时的异步读写通道
  Logitech.cs       罗技 HID++ 2.0
  Razer.cs          雷蛇特性报告协议
  StandardHid.cs    标准 HID 电量字段
  PassiveHid.cs     被动监听接收器主动上报的电量帧；--sniff 数据抓取
  Aliases.cs        设备别名（重命名）的保存与套用
  Native.cs         Win32 P/Invoke 声明
  PopupForm.cs      自绘的弹出面板
  Theme.cs          浅色/深色配色，托盘电池图标绘制
  Program.cs        托盘常驻、定时刷新、低电量提醒、开机自启动、单实例
  app.manifest      DPI 感知、通用控件 v6
assets/app.ico      构建时自动生成
build.ps1 / build.bat
```
