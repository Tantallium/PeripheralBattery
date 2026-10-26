# 应用图标

- `PeripheralBattery.png`：imagegen 生成的透明底原始图。
- `PeripheralBattery.ico`：供 Windows 可执行文件及 WPF 窗口使用，包含 16、20、24、32、40、48、64、128、256 像素版本。
- 重新生成 ICO：在项目目录运行 `powershell -NoProfile -File tools\Build-Icon.ps1`。转换只调整 ICO 所需尺寸并保留透明度。

生成方式：内置 imagegen，2026-10-25。图标用于 EXE、窗口和任务栏；托盘的动态电量指示沿用现有行为。

生成提示词：

> Create one polished Windows desktop application icon for a personal utility named PeripheralBattery (外设电量), which monitors wireless mouse, keyboard and game-controller battery levels. Use case: logo-brand. Asset type: app icon for an EXE, taskbar and window, delivered as a single square 1024x1024 PNG with a genuinely transparent outside background. Match the existing app's cobalt blue #4063DA, with deep blue shading and a restrained teal energy accent. A rounded-square blue tile with a very clear, thick, white horizontal battery symbol in the center; inside the battery use three bold energy segments. Integrate one small, very simple wireless signal motif above the battery to suggest connected peripherals. Refined, balanced Windows utility design with subtle depth, clean edges and strong small-size legibility at 16 to 48 pixels. Fill most of the canvas but leave a consistent 6 percent transparent margin. Use large simple forms, no tiny detail, no surrounding scene, no text, no letters, no numbers, no brand logos, no watermark. One icon only, no variations or contact sheet.
