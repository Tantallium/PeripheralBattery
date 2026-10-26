# 依赖与协议参考

本文件记录源码快照可核对的依赖和来源线索；它不是完整版权审计结论。

## 构建依赖

三个 csproj 均没有显式第三方 PackageReference，也没有 vendored 厂商驱动或 JavaScript。应用依赖 .NET 10、WPF、Windows Forms（托盘）、Windows SDK .NET 投影及 Windows HID/SetupAPI/WinRT 系统接口。SDK 会隐式还原所需引用与运行时包，不能据“没有 PackageReference”断言构建没有依赖。

.NET Runtime 与 Windows Desktop 的许可证、第三方声明以所使用发行版本为准，见 [dotnet/runtime](https://github.com/dotnet/runtime)、[dotnet/wpf](https://github.com/dotnet/wpf)、[dotnet/winforms](https://github.com/dotnet/winforms)。Windows SDK 投影包采用其自身包内许可条款。源码快照不附带这些运行时二进制；如果另行分发 self-contained 应用，应携带对应许可证与声明。

本次本地包元数据核对：Microsoft.NET.ILLink.Tasks、Microsoft.NETCore.App.Runtime.win-x64 与 Microsoft.WindowsDesktop.App.Runtime.win-x64 为 10.0.13，包内声明 MIT；Microsoft.Windows.SDK.NET.Ref 为 10.0.19041.57，包内指向 [Windows SDK 许可条款](https://aka.ms/WinSDKLicenseURL)。这是构建依赖的包元数据核查，不是所有传递组件的版权审计。

## 协议实现来源线索

- Logitech：源码注释和原 PROTOCOLS.md 记录了 [Logitech/cpg-docs](https://github.com/Logitech/cpg-docs/blob/master/hidpp20/README.rst)、[Solaar 设备记录](https://github.com/pwr-Solaar/Solaar/issues/3005)及 [fwupd Logitech HID++](https://github.com/fwupd/fwupd/tree/main/plugins/logitech-hidpp)作为报文、特征和设备事实参考。
- MCHOSE：源码注释记录参考官方 M HUB 网页脚本及其 SHA256，具体名称和历史摘要保留在 PROTOCOLS.md。当前公开快照不含该脚本。
- Xbox：通过 Windows BLE API 访问标准 GATT Battery Service；参考 [Bluetooth SIG Battery Service](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/BAS_v1.1/out/en/index-en.html)和 PROTOCOLS.md 所列 Microsoft 文档。

原源码包含“独立编写、未复制 GPL 项目文件”的开发声明。此次核查确认当前文件清单未携带这些项目的源文件，但未逐段追溯全部开发历史或完成代码相似性、著作权归属及许可兼容性审计，因此保留其为开发声明，不把它视为已完成版权审计。协议参考资料的公开可访问性不等于其代码可以按 MIT 重新授权。

## 图标

PNG、ICO 及转换脚本随源码保留。原项目记录 PNG 由 imagegen 于 2026-10-25 生成，ICO 由项目脚本转换；来源说明见 [Assets/README.md](src/PeripheralBattery/Assets/README.md)。这是一项项目来源记录，不是额外的商标或独创性审计。
