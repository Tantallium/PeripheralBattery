# PeripheralBattery 0.2.3

[简体中文](README.md) | [English](README.en.md)

Windows 桌面外设电量查看器，使用 C# / .NET 10 / WPF。面向下面三款设备及指定连接方式提供实验性适配：罗技 GPW2、迈从 K99 V2（2.4G）和 Xbox Series X 手柄（BLE）。**不承诺支持其他型号、其他接收器或所有固件。**

本版包含本地修复后的电量条绑定、程序图标、每设备独立 5 秒刷新和可保存的自动刷新开关。用户已反馈 **0.2.3 实测可用**；历史 Windows 验收记录支持窗口、刷新、开关及保存行为。这些证据不等于所有设备组合、固件、官方驱动共存及长期稳定性均已验证，仍作为实验版本发布。具体检查范围见 [VALIDATION.txt](VALIDATION.txt)。

## 设备与协议

| 设备及连接方式 | 实际实现 | 范围与限制 |
| --- | --- | --- |
| 罗技 GPW2 / 原版 PRO X SUPERLIGHT，LIGHTSPEED 2.4G | Windows 厂商 HID collection 上的 HID++ 2.0；接收器 `046D:C547`，usage `FF00:0001`（7 字节）与 `FF00:0002`（20 字节）。ROOT.GetFeature 动态发现 `0x1004` UnifiedBattery，兼容 `0x1000` Battery Unified Level Status。 | 查询接收器槽位 1–6；根据能力位决定是否展示百分比，未知值不当作 0%。代码另含 `046D:C094` 有线候选，未确认实机兼容，不能算作已验证支持。 |
| 迈从 K99 V2，2.4G 接收器 | 厂商 HID 查询，`3554:FA09`、usage `FF02:0002`，20 字节报告，Report ID `0x13`；命令 `0x05` 校验型号后才发送 `0x4A` 电量查询。 | 必须匹配公开型号值 `03 00 00 00 01 DF`；VID/PID 本身不能证明是 K99 V2。仅此接收器路线，不包含蓝牙、有线或其他键盘型号；多个匹配接收器时停止选择。 |
| Xbox Series X 手柄，已配对 BLE | Windows WinRT BLE / GATT 标准 Battery Service `180F`、Battery Level `2A19`；实际电量使用 `BluetoothCacheMode.Uncached`，接受一个 0–100 的字节。 | 名称包含 Xbox 或同一身份字段匹配 `045E:0B13` 才进入候选；名称筛选不是型号认证。不提供 USB、Xbox 无线适配器或 XInput 回退；充电状态保持未知。 |

协议字段、回复校验和来源见 [PROTOCOLS.md](PROTOCOLS.md)。蓝牙读取电池服务不代表读取手柄按键。HID 路线需要发送型号/电量查询报告，不发送灯效、宏、配对或固件设置命令。

读数来自设备的电量估计，可能分档、延迟或被接收器缓存；界面时间表示软件收到有效报告的时间，不保证是电芯的新采样时间。接收器插着不等于无线外设在线。失败后保留的值标记为历史读数，不触发新的低电量提醒。

## 功能和运行环境

- 三张设备卡片、托盘状态及低电量提醒；未知电量显示“—”。
- 每设备独立 5 秒自动刷新；已有读取未结束时不叠加请求。开关默认开启并保存状态；关闭后可手动刷新，已在执行的读取仍可结束。
- 关闭窗口隐藏到托盘；通过“退出”结束程序。
- 登录启动默认关闭，用户在设置中开启后才配置启动项。
- 目标为 Windows x64、Windows 10 2004 / build 19041 及以上；ARM64 原生包与 32 位系统未提供。

## 构建

需要已有的 .NET 10 SDK。在仓库根目录运行：

```powershell
dotnet publish src/PeripheralBattery/PeripheralBattery.csproj -c Release -r win-x64 --self-contained true -o out
```

也可运行 `Build-Windows.cmd`。首次还原需要 NuGet 引用包及运行时包，或已备齐的本地缓存。构建后运行 `out\PeripheralBattery.exe`。本仓库提供源码，不附带已签名的二进制发行包；自行分发 self-contained 构建时须随附所包含运行时的许可证和第三方声明。

## 离线检查

```powershell
dotnet run --project tests/PeripheralBattery.ProtocolChecks -c Release
dotnet run --project tests/PeripheralBattery.CoordinatorChecks -c Release
```

检查直接链接生产源码，不依赖第三方测试框架，不打开 HID、蓝牙或真实外设。协调器检查包含真实 5 秒周期和 15 秒截止时间，运行约一分钟。协议检查覆盖范围及未覆盖分支见 [测试说明](tests/PeripheralBattery.ProtocolChecks/README.md)。这些程序不等同于三款外设的端到端测试。

## 本机数据和问题反馈

设置与错误日志写入 `%LOCALAPPDATA%\PeripheralBattery`。代码不包含网络上传或遥测；诊断报告仅在用户点击导出后保存到所选位置。诊断 JSON 可能包含设备路径、蓝牙地址及系统信息，请先脱敏，再分享相关片段，避免提交原始报告。

## 许可证与来源

本项目采用 [MIT License](LICENSE)，署名 `2026 Tantallium`。依赖、协议参考与来源核查边界见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。上游代码、文档和商标仍受各自条款约束。
