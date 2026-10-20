# Resource Analyzer for Windows

A lightweight, screen reader friendly system monitor, process manager, data usage tracker and port scanner for Windows.

I was struggling to use the default Task Manager with a screen reader. The tables made it frustrating to check basic things like CPU and RAM, and finding out which app is eating my internet data was even worse. So I built this tool. It speaks everything as simple single-line sentences, with no cluttered tables.

It is tested with NVDA. JAWS and Windows Narrator should behave the same.
## Requirements

Windows 10 or Windows 11 (64-bit). No separate .NET install is needed. It installs to your user profile without Administrator rights, and you can restart it as Administrator any time with `Ctrl + Shift + A`.

## Install

Run `ResourceAnalyzer_Setup_v2.0.0.exe`. Press `Tab` to choose options (desktop shortcut, start with Windows, check or uncheck for daily update, Review privacy policy), then press Install and Finish.

## How to use it

Switch tabs with `Ctrl + 1` to `Ctrl + 5`. Press `F1` at any time to hear all keyboard shortcuts, and `F5` to refresh.

**Resources (Ctrl + 1):** Use Up and Down Arrow to move through CPU, RAM, GPU, display, disk, network, audio and battery. Press `Enter` on any item for technical details, and `Escape` to close. If you turn on Live Internet Ping in Settings, the Network details also speak your latency, for example "Internet Latency (Ping): 28 ms (Fast / Low Latency)". The labels are Fast under 35 ms, Good up to 80 ms, Moderate up to 150 ms, and High Latency above that. If the router blocks ping or the internet is down, you hear "Request Timed Out" and everything else still works. Press `Ctrl + Shift + C` anywhere to copy a plain-text system snapshot. It includes your IP address, MAC address and Wi-Fi name, so check it before posting it publicly.

**Processes (Ctrl + 2):** Every process is one sentence, and multi-instance apps like Edge are grouped. Use Right and Left Arrow to expand or collapse a group, `Ctrl + F` to filter, and `Ctrl + M`, `Ctrl + P` or `Ctrl + N` to sort by memory, CPU or name. Press `Delete` to end a task, `Shift + Delete` to end a whole process tree, and `Ctrl + R` to restart one. Critical Windows system processes are protected.

**Data Usage (Ctrl + 3):** See how much data each app has used. Choose the network, the time period, and filter by app name.

**Network Ports (Ctrl + 4):** See listening and established ports. Filter by protocol or state, search by port, address or process name, and sort with `Ctrl + P`, `Ctrl + N` or `Ctrl + S`. Press `Delete` to end the process that owns a port.

**Settings (Ctrl + 5):** Confirmation before ending tasks, how process names are spoken, resource overuse alerts, refresh rate, theme (including High Contrast Black), tray and startup behavior, update checks, live internet ping (off by default), Help menu and a reset button.

**From anywhere in Windows:** Press `Ctrl + Win + I` once to hear your live network speed, or twice quickly to hear today's data usage. Press `Ctrl + Win + T` to show or hide the window.

## Privacy

The app does not collect personal data and has no telemetry. It never sends anything in the background without your permission. There are only two optional network features. The first is the update check to GitHub. The second is Live Internet Ping, which is off by default. When you enable it, the app sends one ICMP echo packet to 1.1.1.1 (Cloudflare) with a 400 ms timeout, and only at the moment you open the Network details. It does not ping continuously, and no data is sent with it. As with any ping or website visit, GitHub and Cloudflare can see your IP address. Your settings are stored locally on your PC. Please also read more about in privacy Policy.

## Good to know

On some processes like `nvda.exe`, Open File Location says the file location is unavailable. That is normal, because NVDA runs with special accessibility permissions that Windows protects.

## Disclaimer

This app was created with Google's Antigravity AI assistant, so some issues may occur. If you have questions, suggestions or ideas, please open an issue on GitHub.