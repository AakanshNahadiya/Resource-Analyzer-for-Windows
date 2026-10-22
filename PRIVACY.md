# Accessible Tools Privacy Policy

Effective date: October 18, 2026

Last updated: October 21, 2026

Publisher: Accessible Tools

Applications covered:

- Disk Partitioner for Windows: https://github.com/aakanshnahadiya/Disk-Partitioner-for-Windows
- Resource Analyzer for Windows: https://github.com/aakanshnahadiya/resource-analyzer-for-windows

## Table of Contents

- [Part 1: Principles shared by both apps](#part-1-principles-shared-by-both-apps)
  - [1. No personal data collection](#1-no-personal-data-collection)
  - [2. Local first](#2-local-first)
  - [3. Optional network features](#3-optional-network-features)
  - [4. No ads and no third-party tracking libraries](#4-no-ads-and-no-third-party-tracking-libraries)
  - [5. Source available](#5-source-available)
  - [6. Privacy laws and children](#6-privacy-laws-and-children)
- [Part 2: App-specific details](#part-2-app-specific-details)
  - [A. Disk Partitioner for Windows](#a-disk-partitioner-for-windows)
  - [B. Resource Analyzer for Windows](#b-resource-analyzer-for-windows)
- [Part 3: Security, changes and contact](#part-3-security-changes-and-contact)

## Part 1: Principles shared by both apps

### 1. No personal data collection

Neither app asks for or records your name, email address, phone number, postal address or any account details. Neither app contains analytics, tracking or telemetry of any kind, and neither sends your hardware, process, disk or network information to us or to anyone else. The apps do not log your keystrokes and do not read your personal files. We do not sell, rent or share data with advertisers or data brokers, because we do not receive any.

Information the apps can read on your PC. To do their job, the apps read information from your own computer, and some of it can count as personal information under privacy laws. Resource Analyzer reads your local IP address, MAC address, Wi-Fi network name, running process names, window titles and file paths. Disk Partitioner reads your drive and partition details. This information is used only to show it to you on your screen and through your screen reader. It is kept in memory while the app is open, it is not sent to us or to any third party, and it is not saved to disk, except that a crash log may contain file paths. A copy can leave your PC only if you make it yourself, for example by copying the system snapshot to your clipboard and pasting it somewhere.

### 2. Local first

All hardware queries, disk commands and diagnostics run on your own PC, and the results stay there. The only optional network features are the ones listed in section 3.

### 3. Optional network features

Both apps work fully offline. The only requests they can make are these.

Updates. The Microsoft Store version of Resource Analyzer does not check for updates by itself. Its updates are delivered by the Microsoft Store. The standalone versions, which you can download from GitHub, have an optional update check. When you choose Check for Updates, or if you turn on the optional once-per-day check, the app contacts the public GitHub Releases API over HTTPS to compare version numbers. GitHub receives your IP address, as any website does when you visit it. Accessible Tools does not receive or log it.

Update download (standalone versions only). An update is downloaded only after you confirm it. It is downloaded over HTTPS only and saved in your temporary folder. Before it runs, it is checked against its published SHA-256 hash. If the check fails, the file is deleted without running. Leftover installer files are cleaned up automatically.

Live Internet Ping (Resource Analyzer only). This is off by default. If you turn it on in Settings, then each time you open the Network details, the app sends one ICMP echo (ping) packet to 1.1.1.1, a public Cloudflare server, and waits up to 400 milliseconds for a reply. It never pings in the background, and the packet carries none of your data. As with any ping, Cloudflare can see your IP address.

Links. Links to documentation or release notes open in your default browser and are covered by those websites' own privacy policies.

If you install an app from the Microsoft Store, Microsoft's own terms and privacy statement apply to the Store itself, including how Microsoft downloads and installs updates.

### 4. No ads and no third-party tracking libraries

Both apps contain no advertising, no sponsored content and no third-party tracking or monetization libraries.

### 5. Source available

The source code is public on GitHub under the Accessible Tools Non-Commercial License, so anyone can read it and check the statements in this policy. Please read the license for the exact terms of use.

### 6. Privacy laws and children

Because the apps do not collect personal data, there is nothing held by us that could be accessed, corrected or deleted under laws such as the GDPR or the CCPA. For the same reason, the apps do not collect data from children. The optional network features involve GitHub and Cloudflare, which have their own privacy policies.

## Part 2: App-specific details

### A. Disk Partitioner for Windows

Purpose. Disk Partitioner for Windows is an accessible disk partition manager. It formats drives, manages partition tables, assigns drive letters and runs partition operations using the native Windows storage tools.

Administrator rights. The app asks for Administrator rights when it starts. Windows requires them to talk to storage controllers, run disk commands and use the native diskpart.exe and WMI. The rights are used only for the disk operations you authorize, never to read your personal folders or bypass system protections.

Local drive information. The app reads drive numbers, models, partition schemes (GPT or MBR), volume layouts, file systems and capacity from your local storage. This information is held in memory to show the interface and run your commands. It is not saved to log files and is not sent anywhere.

Screen readers. Speech goes to your screen reader through the local nvdaControllerClient.dll library and Windows UI Automation. Nothing is sent over the network.

Settings. The app stores two non-personal values, the daily update check preference and the time of the last check, in your user registry at HKEY_CURRENT_USER\Software\AccessibleTools\DiskPartitioner. They are removed when you uninstall.

### B. Resource Analyzer for Windows

Purpose. Resource Analyzer for Windows is a system monitor, task manager and network diagnostics tool built for screen reader users.

Standard user by default. The app runs without Administrator rights. You can choose Restart as Administrator at any time. Elevation is used only for local actions you request:

- Ending or restarting stubborn background processes owned by other sessions.
- Purging the standby memory cache using the Windows memory APIs.
- Reading BitLocker drive encryption status.

Critical Windows system processes stay protected from End Task, even in Administrator mode.

What it reads. The app samples CPU, RAM, GPU, disk and network adapter activity, battery health, running process names and IDs, and active network connections, using Windows performance counters and native APIs. These values live only in memory while the app is open and are discarded when it closes. Optional High RAM and High CPU alerts also work in memory only.

Screen readers. Speech goes through the local NVDA Controller Client and Windows UI Automation live regions for JAWS and Narrator. Windows SAPI is not used, so sighted users hear nothing when no screen reader is running.

Files on your PC. Settings are stored in %AppData%\ResourceAnalyzer\settings.json. They hold your theme, refresh interval, speech verbosity, alert thresholds and, in the standalone version only, your update preference. The Microsoft Store version has no update check. If the app crashes, details of the error are saved to %LocalAppData%\ResourceAnalyzer_crash.log. This log may include file paths from your PC, which can contain your Windows user name. It stays on your PC and is never sent anywhere. You can edit, reset or delete these files at any time.

System snapshot and clipboard. When you press Ctrl + Shift + C, or choose Copy System Diagnostic Snapshot, the app places a hardware summary on your clipboard. It can include your local IP address, MAC address and Wi-Fi network name. The app speaks a warning so you can review or remove these details before sharing the snapshot publicly. The app writes to the clipboard only when you ask it to. It never reads or records what is already on your clipboard.

## Part 3: Security, changes and contact

Security and retention. Because the apps do not send your data to any server of ours, your system information never leaves your PC through the apps. Settings files and registry keys are stored locally and can be removed by uninstalling the app or deleting them by hand.

Changes. We may update this policy when the apps change. Updates are published in our GitHub repositories with a new date at the top.

Contact. For questions, feedback or security concerns, please open an issue on GitHub:

- Disk Partitioner: https://github.com/aakanshnahadiya/Disk-Partitioner-for-Windows/issues
- Resource Analyzer: https://github.com/aakanshnahadiya/resource-analyzer-for-windows/issues
- Accessible Tools profile: https://github.com/aakanshnahadiya
