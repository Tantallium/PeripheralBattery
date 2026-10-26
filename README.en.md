# PeripheralBattery 0.2.3

[简体中文](README.md) | [English](README.en.md)

A Windows desktop peripheral battery monitor built with C# / .NET 10 / WPF. Experimental support is limited to the following three devices and connection modes: Logitech GPW2, MCHOSE K99 V2 (2.4 GHz), and Xbox Series X controller (BLE). **Other models, receivers, and firmware versions are not guaranteed to work.**

This version includes the local fixes for the battery-bar binding, application icon, independent 5-second refresh timers per device, and a persistent automatic-refresh switch. The user reported that **0.2.3 worked in hands-on testing**. Historical Windows acceptance records cover window behavior, refresh, the switch, and persistence. This evidence does not establish coverage of every device combination, firmware version, coexistence with official drivers, or long-term stability. This remains an experimental release. See [VALIDATION.txt](VALIDATION.txt) for the exact scope of checks.

## Devices and protocols

| Device and connection | Actual implementation | Scope and limitations |
| --- | --- | --- |
| Logitech GPW2 / original PRO X SUPERLIGHT, LIGHTSPEED 2.4 GHz | HID++ 2.0 over Windows vendor HID collections; receiver `046D:C547`, usages `FF00:0001` (7 bytes) and `FF00:0002` (20 bytes). ROOT.GetFeature dynamically discovers `0x1004` UnifiedBattery, with support for `0x1000` Battery Unified Level Status. | Queries receiver slots 1–6. Capability bits determine whether a percentage is displayed; unknown readings are not treated as 0%. The code also contains a wired `046D:C094` candidate, whose hardware compatibility has not been confirmed and is not verified support. |
| MCHOSE K99 V2, 2.4 GHz receiver | Vendor HID queries using `3554:FA09`, usage `FF02:0002`, 20-byte reports, and Report ID `0x13`. Command `0x05` validates the model before the `0x4A` battery query is sent. | Must match the public model value `03 00 00 00 01 DF`; VID/PID alone does not identify a K99 V2. Only this receiver path is implemented, excluding Bluetooth, wired operation, and other keyboard models. Selection stops when multiple matching receivers are present. |
| Xbox Series X controller, paired BLE | Windows WinRT BLE / GATT standard Battery Service `180F` and Battery Level `2A19`. The battery read uses `BluetoothCacheMode.Uncached` and accepts exactly one byte in the range 0–100. | A device becomes a candidate if its name contains Xbox or one identity field matches `045E:0B13`; name filtering is not model authentication. There is no USB, Xbox Wireless Adapter, or XInput fallback. Charging status remains unknown. |

See [PROTOCOLS.md](PROTOCOLS.md) for protocol fields, response validation, and sources. Reading the Bluetooth battery service does not mean reading controller button inputs. The HID paths send model/battery query reports; they do not send lighting, macro, pairing, or firmware-setting commands.

Readings are estimates supplied by the device and may be coarse, delayed, or cached by the receiver. The displayed time is when the software received a valid report, not necessarily when the battery was sampled. A plugged-in receiver does not establish that the wireless peripheral is online. Values retained after a failed read are marked as historical and do not trigger new low-battery notifications.

## Features and runtime requirements

- Three device cards, tray status, and low-battery notifications; unknown battery levels display “—”.
- Independent 5-second automatic refresh per device, without overlapping an unfinished read. The switch is enabled by default and persisted. Manual refresh remains available when it is off; an in-progress read can still finish.
- Closing the window hides it to the tray; use “Exit” to terminate the application.
- Start at login is off by default and is configured only when the user enables it in settings.
- Targets Windows x64, Windows 10 2004 / build 19041 or later. No native ARM64 package or 32-bit support is provided.

## Build

An existing .NET 10 SDK is required. Run from the repository root:

```powershell
dotnet publish src/PeripheralBattery/PeripheralBattery.csproj -c Release -r win-x64 --self-contained true -o out
```

Alternatively, run `Build-Windows.cmd`. The initial restore needs NuGet reference and runtime packages, or a complete local cache. Run `out\PeripheralBattery.exe` after building. This repository provides source code and does not include signed binary releases. When distributing your own self-contained build, include the licenses and third-party notices for the bundled runtime.

## Offline checks

```powershell
dotnet run --project tests/PeripheralBattery.ProtocolChecks -c Release
dotnet run --project tests/PeripheralBattery.CoordinatorChecks -c Release
```

The checks link production source directly, require no third-party test framework, and do not open HID, Bluetooth, or physical peripherals. Coordinator checks include real 5-second intervals and a 15-second deadline and take about a minute. See the [test notes](tests/PeripheralBattery.ProtocolChecks/README.md) for protocol coverage and untested branches. These programs are not end-to-end tests of the three physical devices.

## Local data and issue reports

Settings and error logs are stored under `%LOCALAPPDATA%\PeripheralBattery`. The code contains no network upload or telemetry. Diagnostic reports are saved to a chosen location only when the user clicks Export. Diagnostic JSON may contain device paths, Bluetooth addresses, and system information. Redact it before sharing relevant excerpts, and avoid submitting raw reports.

## License and sources

This project uses the [MIT License](LICENSE), copyright `2026 Tantallium`. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for dependencies, protocol references, and the limits of the source review. Upstream code, documentation, and trademarks remain subject to their respective terms.
