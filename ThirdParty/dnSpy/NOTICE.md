# dnSpy UI source attribution

Source: https://github.com/dnSpy/dnSpy

Pinned revision: `2b6dcfaf602fb8ca6462b8b6237fdfc0c74ad994`

Copyright (C) 2014-2019 de4dot@gmail.com and the upstream contributors.
dnSpy is licensed under GNU GPL version 3 or, at your option, any later version.
See LICENSE.txt, GPLv3.txt, OtherLicenses.txt and CREDITS.txt in this directory.
The upstream XAML also identifies its original WPF Aero/Aero2 template sources.

## Original files

The following files are unmodified copies from the pinned revision. SHA256SUMS
records their hashes for comparison with upstream:

- `wpf.styles.templates.xaml`: `dnSpy/dnSpy/Themes/wpf.styles.templates.xaml`
- `dark.dntheme`: `dnSpy/dnSpy/Themes/dark.dntheme`
- `ColorInfos.cs`, `ColorInfo.cs`: `dnSpy/dnSpy/Themes/`
- `TabButton.cs`: `dnSpy/dnSpy/Controls/TabButton.cs` (compiled directly)
- `LICENSE.txt`, `GPLv3.txt`, `OtherLicenses.txt`, `CREDITS.txt`: `dnSpy/dnSpy/LicenseInfo/`

## Connect adaptations (2026-09-20)

`Import-Theme.ps1` produces `Themes/dnSpy/Controls.xaml` and `Themes/dnSpy/Dark.xaml`
offline from these originals. Both generated files are derivative works under
GPL-3.0-or-later. The extraction script selects the standard buttons, toolbar,
document tabs, TabButton, tooltip, toolbar combo box/dropdown items, text boxes,
ListView/GridView column headers and item templates, scrollbars and scroll viewer, and resolves their
colors/brushes/gradient stops through the original ColorInfos mappings and dark theme.
The debugging status bar uses the original StatusBarDebuggingBackground orange.

The port makes these changes to fit this tool:

- Removes dependencies on dnSpy's document service, drag/drop attached properties
  and image service. There is no floating/docking or decompiler integration.
- Maps tab headers and selection to standard WPF TabItem properties, and the
  active/inactive colors to the containing Window.IsActive property.
- Hides the close button for the fixed development tab; the original TabButton
  class and template remain in use as part of the document-tab template.
- Keeps selected tabs keyboard focusable.
- Binds the document-tab resting background and border to the local workspace
  style, adding a subtle outline and fill to unselected tabs.
- Corrects the upstream swapped horizontal/vertical ScrollViewer template part
  names so WPF scroll commands can address the proper scrollbar.
- Omits commented-out templates. Uses the .NET Framework Aero assembly for the
  original scrollbar glyph attached properties.
- Maps the column-header sort-indicator attached property to a small local
  adapter in `Controls/GridViewColumnSorter.cs` for the remaining tabular views.
- Imports the original ExpandCollapseToggleStyle and tree theme brushes. The OSS
  hierarchy uses a local WPF TreeView/TreeViewItem adapter and lazy loading rather
  than dnSpy's assembly-specific SharpTreeView node implementation.

The application-specific layout defaults remain in `Themes/Dark.xaml`.
The main window and firmware dialog use local WPF WindowChrome adapters with the original
dnSpy main-window/dialog caption, border and caption-button palettes; dnSpy's MetroWindow
implementation and image services are not imported.
No upstream theme or control templates are overwritten in this directory.

## AirCard adaptations (2026-09-22)

The AirCard .NET Framework port reuses these ConnectDeveloperTool resources.
Its application layout is new WPF XAML. `Themes/WhiteText.xaml` overrides text
brushes to opaque white at the user's request; `SecondaryTextBrush` in
`Themes/Dark.xaml` is also white. Original dnSpy source copies and provenance
are retained. Apple device operations are independent C# code.
The theme import script uses the AirCard.Controls namespace so regenerated
templates do not depend on the original ConnectDeveloperTool assembly.

## Distribution

This Developer Tool now incorporates GPL-covered dnSpy code. When distributing
the combined tool, comply with the accompanying GPL terms, including applicable
corresponding-source requirements. These notices do not relicense unrelated
Client or Server projects. The build copies this notice and the upstream license
files alongside the executable under `ThirdParty/dnSpy`.
