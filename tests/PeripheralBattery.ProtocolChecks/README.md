# Offline protocol checks

This is a .NET 10 console program with no NuGet test framework or third-party dependencies. It links the current production model, HID transport, Logitech provider and MCHOSE provider sources. Its `NuGet.Config` clears all package feeds.

Run from the repository root with an installed .NET 10 SDK:

```powershell
dotnet run --project tests/PeripheralBattery.ProtocolChecks/PeripheralBattery.ProtocolChecks.csproj --configuration Release
```

The checks invoke private pure helpers by reflection. They do **not** call `ReadAsync`, HID enumeration/opening, or any P/Invoke method. A test-only uninitialized Logitech session is used only as metadata for the pure result builder; its hardware-opening constructor is never called.

## Coverage

- Exact MCHOSE 20-byte model/battery request vectors and their `0x19` / `0x5E` checksums.
- MCHOSE model and battery reply framing, extension fields up to the 14-byte payload limit, malformed lengths, report/command/page/sequence mismatches, and damaged checksums.
- The **actual compiler-generated battery acceptance lambda** inside MCHOSE `ReadAsync`, including the additional `<= 100` guard. It is discovered by reflection rather than copied into the check program.
- The K99 V2 signature constant and the distinction between a framing-valid response and a model match.
- Logitech short/long collection grouping using synthetic receiver paths with the Windows path structure, exact endpoint selection, and rejection of ordinary keyboard/mouse collections.
- HID++ reply correlation by slot, feature, function and software ID; wrong replies, unsolicited notifications, transient BUSY, and matched hard errors.
- Pure battery result semantics: nullable percentage and charging, coarse/full without a fabricated percentage, initial unknown readings, and last-known values/timestamps after a failed refresh.

## Limits

Matched HID++ hard errors intentionally pass the correlation matcher so the I/O request layer can raise an exception promptly. These checks verify that correlation behavior. They do not invoke the error-to-exception dispatch inside the real exchange.

MCHOSE model rejection and charging-nibble decoding, plus Logitech capability-bit/status parsing, remain embedded in hardware-reading methods. The tests verify their accessible framing/filter/result boundaries, not those complete parser branches. A future extraction into pure decoders would allow more direct checks without a hardware simulation.

Successful offline checks do not establish USB transport compatibility, driver cancellation behavior, current battery accuracy, coexistence with G HUB/M HUB, hotplug handling, or actual device firmware behavior. Those require acceptance tests on the user's Windows hardware.

The process exits with 0 on success and 1 on any failed assertion or harness failure.
