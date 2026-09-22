# Apple Mobile Drivers Installer

Upstream: https://github.com/NelloKudo/Apple-Mobile-Drivers-Installer
Script: https://raw.githubusercontent.com/NelloKudo/Apple-Mobile-Drivers-Installer/main/AppleDrivInstaller.ps1
Author: NelloKudo and contributors. License: GPL-3.0 (see LICENSE.txt).
Retrieved: 2026-09-22.
Upstream script SHA-256: 7635503069E3510AF357AAC804B522C1B3EC486126D4E06764B24E3BC3E3A0A3

AppleDrivInstaller.upstream.ps1 is the unmodified source snapshot.
AppleDrivInstaller.ps1 is the Air Card adaptation, embedded into AirCard.exe.
Modified on 2026-09-22: noninteractive app integration, isolated staging directory,
Apple executable/MSI signature validation, checked process exit codes, reboot
reporting, UTF-8 log output, and scoped cleanup. Original download URLs retained.
Runtime never fetches or executes an updated script from GitHub.

Only an explicit user selection starts installation and requests UAC elevation.
The full iTunes installer is downloaded to extract AppleMobileDeviceSupport64.msi;
iTunes itself is not installed. USB/Ethernet packages come from Microsoft Update.
