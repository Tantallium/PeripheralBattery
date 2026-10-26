# 电量读取依据与边界

协议记录日期：2026-10-25；源码对照核查：2026-10-26（0.2.3）。这里记录当前实现与参考线索，不代表全设备兼容性认证。

## Windows HID 传输

使用 SetupAPI 枚举 HID，读取 HID attributes/caps；不调用可能很慢或返回无效字符串的制造商/产品名称接口。只打开已筛选的厂商自定义 collection，采用共享读写和异步 I/O，不打开普通键鼠输入 collection。Windows 输出报告包含 Report ID，长度使用报告描述符中的 OutputReportByteLength。

每个端点只有一个 reader；先注册回复匹配器，再写请求。超时或设备拔出会结束本轮等待。无法结束的底层原生调用不会被无限重复启动。

参考：[Microsoft — Sending HID Reports](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/sending-hid-reports)。

## 罗技 GPW2

当前实现筛选的接收器为 `046D:C547`，`FF00:0001` 的短报告为 7 字节，`FF00:0002` 的长报告为 20 字节。根据设备路径把同一接收器的短、长 collection 归组，双方同时监听回复。接收器无线槽位为 1–6；接收器自身的 `0xFF` 不作为鼠标电量目标。有线候选 `046D:C094` 才使用 `0xFF`。

ROOT.GetFeature 动态发现电量功能索引；优先 `0x1004` UnifiedBattery，读取 capabilities 后决定能否展示百分比，再读取状态。兼容 `0x1000` Battery Unified Level Status，该特性中的电量 0 按未知处理。匹配时检查设备槽位、特征索引、函数和非零 Software ID；拒绝与请求无关的主动通知。

- [Logitech 官方 HID++ 2.0 报文与 ROOT 说明](https://github.com/Logitech/cpg-docs/blob/master/hidpp20/README.rst)
- [Solaar 原版 PRO X SUPERLIGHT 设备实测记录](https://github.com/pwr-Solaar/Solaar/issues/3005)
- [fwupd Logitech HID++ 实现与定义](https://github.com/fwupd/fwupd/tree/main/plugins/logitech-hidpp)

原开发记录声明实现为独立编写，未复制 Solaar 等项目的源文件；本次未完成逐段来源或版权审计，不能仅凭该声明确认授权链。公开快照未携带这些项目的源文件。详见 [第三方来源记录](THIRD_PARTY_NOTICES.md)。尚未实测与 G HUB 并行工作的完整兼容性，也没有确认本机所有电量档位与充电状态。

## 迈从 K99 V2 的 2.4G 接收器

原开发记录中的官方网页驱动参考（以下脚本名和哈希是历史记录，本次未重新取得对应脚本验证）：

- 入口：[M HUB](https://www.mchose.com.cn/)
- 入口脚本：`https://www.mchose.com.cn/assets/main-QPbJ9s7e.js`
- 协议实现脚本：`https://www.mchose.com.cn/assets/purify.es-CxLZ1Htw.js`
- 实现脚本 SHA-256：`b64fdf6d0f85aa9548c8d7ddb43a7f1b39e4d662762d5e3f359e69a600fbdfae`
- [官方 K99 V2 FAQ](https://support.mchose.store/hc/en-us/articles/52491715289620-MCHOSE-K99-V3-K99-V2-K99-V1-FAQ-Troubleshooting-Guide)

官方网页首先接受 `3554:FA09` 接收器族，再通过静态六字节型号值区分具体设备。K99 V2 的型号标识是 `03 00 00 00 01 DF`。官方内部方法名为 `getPassWord`，实际用于匹配公开型号表，不是用户密码或身份验证。本项目只在该标识完全匹配后继续查询电量。

当前实现筛选 Windows HID 接口 `FF02:0002`，输入与输出各 20 字节。另一条 `FF04:0002` 的 8 字节 feature 接口不用于本项目查询。

| 查询 | 完整 Windows 报告（含 Report ID 和校验和） |
| --- | --- |
| 型号 | `13 05 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 19` |
| 电量 | `13 4A 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 5E` |

回复验证：总长度 20 字节；报告号 `0x13`；命令回显一致；总页数 1；页序号 0；型号 payload 至少 6 字节、电量 payload 至少 2 字节，最多 14 字节；前 19 字节累加模 256 等于末字节。官方解析器只需要这些前缀，因此允许固件附加尾字段。

payload 从 raw[5] 开始。电量回复 raw[5] 必须在 0–100。raw[6] 的低四位为 fullStatus，高四位为 chargeStatus。chargeStatus 0/1 分别表示未充电/充电，其他值保留未知。收到有效报告的时间不等于一次可证明的电芯采样时间：协议没有事务随机数或设备采样时间戳，无法从字段彻底排除同命令延迟回复和接收器缓存。

本项目没有携带厂商 JS 文件，也不发送配置、灯效、宏、配对或升级命令。实际接收器回复、休眠行为、充电时模式切换仍需实机核对。

## Xbox BLE

先从已配对蓝牙设备中筛选 Xbox 名称或相同身份字段中的 `045E:0B13`，再访问标准电池服务 `180F` 和电量特征 `2A19`。不会只按“有电池服务”去匹配，因此通常排除普通蓝牙耳机；名称包含 Xbox 仍只是启发式筛选，不能证明实际型号。

检查实际连接状态后，以 `BluetoothCacheMode.Uncached` 读取一个字节，值必须是 0–100。服务描述可以缓存，实际读数不能直接使用 Windows 缓存冒充本次查询。`BluetoothLEDevice.FromIdAsync` 在捕获的 UI 上下文发起。Windows 原生调用超时后保留唯一未完成任务并负责最终释放，避免叠加蓝牙连接请求。

`2A19` 只提供电量；本版本没有读取可选的充电状态特征，也没有根据电池包类型推断正在充电。本版本没有 XInput 回退，避免把四档状态显示成虚构百分比。

- [Microsoft — GattCharacteristic.ReadValueAsync](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattcharacteristic.readvalueasync)
- [Microsoft — BluetoothLEDevice.FromIdAsync](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothledevice.fromidasync)
- [Bluetooth SIG — Battery Service 1.1](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/BAS_v1.1/out/en/index-en.html)

## 统一状态语义

百分比未知、设备断开、暂未响应与 0% 分开处理。失败后只允许继承同一设备或暂时缺席设备的旧值；保留原始有效读数时间并清除旧充电状态。历史读数不触发低电量提醒。每个 provider 独立串行；一个设备挂起不会无限占用其他设备的刷新。
