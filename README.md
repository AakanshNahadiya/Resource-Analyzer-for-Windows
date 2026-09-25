# Resource Analyzer for Windows

> A lightweight, screen-reader-first system monitor, process manager, network data usage tracker, battery telemetry analyzer, and port scanner designed specifically for Windows.

---

I was struggling to use the default Windows Task Manager with a screen reader. The complicated tables made it frustrating to check basic things like CPU and RAM usage.

Also, finding out which app is eating your internet data or draining your laptop battery in Windows is one of the most frustrating problems. The default Windows settings pages are slow, buried, and confusing with screen readers.

This is why I have created a tool called **Resource Analyzer for Windows**.

This tool gives you a clean, single-line overview of your computer health, processes, network data usage, battery sessions, and active network ports without cluttered tables.

In this guide, I'm going to show you how you can use this tool step-by-step.

Now let's understand what Resource Analyzer is.

---

## What is Resource Analyzer?

Resource Analyzer is an all-in-one solution for monitoring system resources, managing processes, tracking network data usage, inspecting battery energy drain, and auditing open network ports.

* **No Cluttered Tables:** Speaks everything as simple, natural single-line sentences.
* **100% Screen Reader Friendly:** Built and optimized from the ground up for NVDA, JAWS, and Windows Narrator.
* **Solves Windows Data Usage:** Gives an instant breakdown of how much internet data each application has consumed.
* **Real-Time Battery & Port Telemetry:** Tracks battery discharge logs, live per-app milliwatt drain, and active listening ports.
* **Ultra-Lightweight:** Runs on only **18 MB to 35 MB of RAM** (compared to 150 MB+ on standard monitors).

Now it's time to set up Resource Analyzer step-by-step.

---

## Requirements I Recommend

1. **Operating System:** Windows 10 or Windows 11 (64-bit).
2. **Screen Reader:** Fully tested with NVDA, but the same behavior can be expected from other screen readers such as JAWS and Windows Narrator.
3. **No extra dependencies:** Ready-to-run package; no separate .NET installation needed.
4. **Standard Privileges:** Installs to your user profile without needing Administrator rights (optional elevation available anytime via <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>A</kbd>).

---

## Step 1: Download and Install Resource Analyzer

1. Locate the setup file: `ResourceAnalyzer_Setup_v1.0.1.exe`.
2. Press <kbd>Enter</kbd> on the setup file to open the installation wizard.
3. Press <kbd>Enter</kbd> on the **Next** button.
4. Use the <kbd>Tab</kbd> key to review installation options:
   * **Create a desktop shortcut:** Press <kbd>Space</kbd> to check or uncheck.
   * **Start with Windows:** Press <kbd>Space</kbd> if you want it to run quietly in your system tray on boot.
5. Press <kbd>Tab</kbd> to locate the **Install** button and press <kbd>Enter</kbd>.
6. Once installation is completed, press <kbd>Enter</kbd> on **Finish** to launch the app.

---

## Step 2: Check Your System Resources

When you open Resource Analyzer, you land on **Tab 1: Resources** by default.

Use the <kbd>Down Arrow</kbd> and <kbd>Up Arrow</kbd> keys to move through your hardware items:

* **CPU:** Speaks your overall load, logical core count, and processor model.
* **RAM:** Automatically switches between MB and GB based on usage (e.g., `820 MB used` or `5.42 GB of 16.0 GB used`).
* **Disk:** Shows the free and total capacity of your drive.
* **Network:** Shows your active Wi-Fi or Ethernet adapter and real-time speeds.
* **Battery:** Displays your battery percentage and charging status (automatically hidden on desktop PCs).

### Deep Technical Hardware Specifications
If you want deep technical specifications, press <kbd>Enter</kbd> on any hardware item:

* **CPU:** Base clock speed, physical cores, threads, L2/L3 cache, system uptime, and firmware virtualization status.
* **RAM:** Memory clock speed (e.g. `3200 MHz`), slots used (e.g. `2 of 2 slots`), form factor (SODIMM/DIMM), hardware reserved memory, committed memory, cached memory, and individual module part numbers.
* **Disk:** Drive model (e.g. `NVMe Intel SSD`), interface type, partition count, and breakdown of all connected drives.
* **Network:** Controller name, physical MAC address, Wi-Fi SSID, Wi-Fi standard (802.11ac/ax), radio band (5 GHz / 2.4 GHz), Wi-Fi channel, signal strength %, IP addresses, gateway, and DNS servers.
* **GPU:** Graphics adapter, driver version, driver release date, dedicated VRAM, and display resolution.
* **Battery:** Charge %, power source, battery device name, and battery chemistry.

> [!TIP]
> Inside the Technical Details dialog, use <kbd>Down Arrow</kbd> and <kbd>Up Arrow</kbd> to read each specification line-by-line. Press <kbd>Ctrl</kbd> + <kbd>C</kbd> (or <kbd>Tab</kbd> to **Copy to Clipboard**) to copy the full technical report, and press <kbd>Escape</kbd> to close.

### Copy System Diagnostic Snapshot (<kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>C</kbd>)
Whenever you need technical support, press <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>C</kbd> anywhere in the application. It immediately generates a clean, plain-text summary of your OS, processor, RAM, graphics, storage, network, and battery, and copies it to your clipboard ready to paste into an email or forum post.

---

## Step 3: Manage Your Processes & Groups

Press <kbd>Ctrl</kbd> + <kbd>2</kbd> (or <kbd>Ctrl</kbd> + <kbd>Tab</kbd>) to switch to **Tab 2: Processes**.

Every running process is shown as a clean, single-line sentence. Multi-instance apps like Google Chrome or Microsoft Edge are grouped together automatically.

### Managing Processes:
1. **Expand and Collapse Groups:**
   * On any grouped process, press <kbd>Right Arrow</kbd> to expand the group.
   * Use <kbd>Down Arrow</kbd> to explore each individual child process with its Process ID (PID).
   * Press <kbd>Left Arrow</kbd> to collapse the group back.
   * Press <kbd>Ctrl</kbd> + <kbd>G</kbd> to toggle application grouping on or off.
2. **Search for an App:**
   * Press <kbd>Ctrl</kbd> + <kbd>F</kbd> to focus the search box.
   * Type the app name (e.g., `notepad`) and press <kbd>Down Arrow</kbd> to enter the filtered list.
   * Press <kbd>Escape</kbd> to clear the search.
3. **Sort the List:**
   * <kbd>Ctrl</kbd> + <kbd>M</kbd>: Sort by highest Memory (RAM).
   * <kbd>Ctrl</kbd> + <kbd>P</kbd>: Sort by highest CPU usage.
   * <kbd>Ctrl</kbd> + <kbd>N</kbd>: Sort alphabetically by Name.
4. **End an Unresponsive Task:**
   * Select the process you want to close and press <kbd>Delete</kbd>.
   * If confirmation is enabled, press <kbd>Enter</kbd> on **Yes**.
   * Press <kbd>Shift</kbd> + <kbd>Delete</kbd> to end an entire process tree.
   * You can also press <kbd>Shift</kbd> + <kbd>F10</kbd> (or the Application key) to open the context menu for **Open File Location** or **Search on Web**.

---

## Step 4: Track Application Data Usage

In Windows, checking which application is consuming your internet data is a huge headache. The default Windows Data Usage settings page is slow and hard to navigate with speech.

Resource Analyzer solves this with a dedicated, real-time **Data Usage** tab.

Press <kbd>Ctrl</kbd> + <kbd>3</kbd> to switch to **Tab 3: Data Usage**:

1. **Select Network Connection:** Press <kbd>Tab</kbd> to reach the Network dropdown. Choose **Current Connected Network** or **All Networks**.
2. **Choose Time Period:** Press <kbd>Tab</kbd> to reach the Period dropdown. Filter by **Today**, **Last 24 Hours**, **Last Week (7 Days)**, **Last Month (30 Days)**, or **Full (All Recorded)**.
3. **Search by Name:** Press <kbd>Tab</kbd> to reach the Filter box. Type any app name (like Chrome or Steam) to see exactly how much data it used.
4. **Explore Application Data:** Press <kbd>Tab</kbd> to enter the list. Each item speaks the app name, data received, data sent, and combined total in MB or GB. Updates scan in real-time without moving your cursor.

---

## Step 5: Monitor Battery Sessions & App Energy Drain

Press <kbd>Ctrl</kbd> + <kbd>4</kbd> to switch to **Tab 4: Battery Usage** (on laptops and battery-equipped tablets):

1. **Filter by Time Range:** Choose **Full**, **Last Month**, **Last Week**, **Last 24 Hours**, or **Today**.
2. **Since Last Charge:** Check the *"Since Last Charge"* box to calculate discharge energy and active time starting from the moment you unplugged your charger.
3. **Accuracy Disclaimer:** A clear, non-technical explanation notes that individual app energy draw is estimated from processor and display activity. You can check *"Don't show again"* and press *"Dismiss"* to hide it.
4. **Dual Accessible Lists:**
   * **List 1 (Battery Sessions):** Shows historical battery discharge and charging sessions with start time, duration, energy consumed (mWh), and discharge %.
   * **List 2 (Application Battery Usage):** Live telemetry scanning running applications to show their energy impact, estimated power draw in milliwatts (mW), and percentage of battery consumed.
   * Press <kbd>Tab</kbd> to move between List 1 and List 2. Inside List 2, press <kbd>Delete</kbd> to end any high-drain app.

---

## Step 6: Scan Open Network Ports

Press <kbd>Ctrl</kbd> + <kbd>5</kbd> to switch to **Tab 5: Network Ports**:

1. **Filter by Protocol:** Filter between **All Protocols**, **TCP Only**, or **UDP Only**.
2. **Filter by State:** Filter between **All States**, **Listening Only**, or **Established Only**.
3. **Search:** Press <kbd>Ctrl</kbd> + <kbd>F</kbd> or tab to the search box to find ports by port number, IP address, or process name.
4. **Sort Ports:**
   * <kbd>Ctrl</kbd> + <kbd>P</kbd>: Sort by Port Number.
   * <kbd>Ctrl</kbd> + <kbd>N</kbd>: Sort by Process Name.
   * <kbd>Ctrl</kbd> + <kbd>S</kbd>: Sort by Connection State.
5. **Close Connection Process:** Press <kbd>Delete</kbd> on any port item to terminate the process listening on that port.

---

## Step 7: Check Network Speed from Anywhere (<kbd>Ctrl</kbd> + <kbd>Win</kbd> + <kbd>I</kbd>)

You don't need to open Resource Analyzer every time you want to check your internet connection:

* Press <kbd>Ctrl</kbd> + <kbd>Win</kbd> + <kbd>I</kbd> from **anywhere in Windows** (even while inside a web browser, editor, or full-screen game).
* Your screen reader will immediately speak your live download and upload throughput without moving focus away from your work.

---

## Step 8: Configure Your Settings

Press <kbd>Ctrl</kbd> + <kbd>6</kbd> to reach **Tab 6: Settings**.

Use <kbd>Tab</kbd> and <kbd>Arrow keys</kbd> to customize your options:

1. **Process Manager Settings:**
   * Decide whether to hide Windows system processes, and whether to prompt for confirmation before ending tasks.
   * Choose how process names are spoken (showing/hiding `.exe` extensions and PIDs).
   * Choose between grouped applications or a flat list.
2. **Resource Overuse Alerts:**
   * Enable speech and notification alerts when an app exceeds your chosen limit.
   * Alert on High RAM, High CPU, or both with custom thresholds (e.g. 2 GB RAM or 80% CPU).
3. **Visible Resources in Resource Monitor:**
   * Choose which hardware items appear in the Resources tab.
   * Pick presets (**All Resources**, **Essential**, **Minimal**) or use **Custom Selection** to individually toggle CPU, RAM, GPU, Network, Disk, or Battery.
4. **Auto-Refresh Rate:**
   * Background update intervals: **1s**, **2s (Default)**, **3s**, **5s**, or **Paused** (manual <kbd>F5</kbd> only).
5. **Appearance and Color Theme:**
   * Select your visual theme: **System Default** (follows Windows dark/light mode), **Dark Theme**, **Light Theme**, or **High Contrast Black**.
6. **Startup and System Tray:**
   * Configure window closing behavior (minimize to system tray or exit completely).
   * Toggle starting automatically with Windows.
7. **Windows System Tools:**
   * Open native **Windows Startup Apps Settings** (<kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>S</kbd>) directly.
   * Open native **Windows Services Manager** (`services.msc`) directly from an accessible button.
8. **Preferences and Reset:**
   * Choose whether to remember your process sort and filter preferences across restarts.
   * Press **Reset All Settings to Defaults** to restore original configurations anytime.

---

## A Quick Note on "File Location is Unavailable"

> [!NOTE]
> When you press <kbd>Shift</kbd> + <kbd>F10</kbd> on some processes like `nvda.exe` and select **Open File Location**, you might hear:  
> *"File location is unavailable for nvda.exe."*
> 
> This is completely normal. NVDA runs with special Windows accessibility permissions (`UIAccess`) so it can read secure lock screens and UAC prompts. Windows security blocks standard applications from opening the file paths of protected accessibility tools.

---

## Keyboard Shortcuts Reference

| Shortcut | Description |
| :--- | :--- |
| <kbd>Ctrl</kbd> + <kbd>1</kbd> | Switch to Tab 1 (Resources) |
| <kbd>Ctrl</kbd> + <kbd>2</kbd> | Switch to Tab 2 (Processes) |
| <kbd>Ctrl</kbd> + <kbd>3</kbd> | Switch to Tab 3 (Data Usage) |
| <kbd>Ctrl</kbd> + <kbd>4</kbd> | Switch to Tab 4 (Battery Usage) |
| <kbd>Ctrl</kbd> + <kbd>5</kbd> | Switch to Tab 5 (Network Ports) |
| <kbd>Ctrl</kbd> + <kbd>6</kbd> | Switch to Tab 6 (Settings) |
| <kbd>F1</kbd> or <kbd>Shift</kbd> + <kbd>/</kbd> | Open Keyboard Shortcuts Help dialog |
| <kbd>F5</kbd> | Refresh current tab data immediately |
| <kbd>Enter</kbd> | View detailed technical hardware specifications (Resources tab) |
| <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>C</kbd> | Copy complete plain-text System Diagnostic Snapshot to clipboard |
| <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>A</kbd> | Restart Resource Analyzer as Administrator |
| <kbd>Ctrl</kbd> + <kbd>F</kbd> | Search / filter processes, data usage, or network ports |
| <kbd>Ctrl</kbd> + <kbd>M</kbd> | Sort processes by Memory (RAM) usage |
| <kbd>Ctrl</kbd> + <kbd>P</kbd> | Sort processes by CPU usage / Sort ports by Port Number |
| <kbd>Ctrl</kbd> + <kbd>N</kbd> | Sort processes or ports alphabetically by Name |
| <kbd>Ctrl</kbd> + <kbd>S</kbd> | Sort network ports by Connection State |
| <kbd>Ctrl</kbd> + <kbd>G</kbd> | Toggle application grouping on or off (Processes tab) |
| <kbd>Right Arrow</kbd> | Expand grouped application to see sub-processes (Processes tab) |
| <kbd>Left Arrow</kbd> | Collapse grouped application (Processes tab) |
| <kbd>Delete</kbd> | End selected task or port owner process |
| <kbd>Shift</kbd> + <kbd>Delete</kbd> | End entire process tree (Processes tab) |
| <kbd>Ctrl</kbd> + <kbd>Win</kbd> + <kbd>I</kbd> | Global hotkey: Speak current network speed from anywhere |
| <kbd>Ctrl</kbd> + <kbd>Win</kbd> + <kbd>T</kbd> | Global hotkey: Show or restore Resource Analyzer window |

---

## In the End

This is how you can easily monitor your PC resources, track your data usage, inspect battery drain, audit network ports, and manage processes with a screen reader.

Finally, I will end here. If you have any questions, suggestions, ideas, or anything else, do let me know in the GitHub issues section!

> **Disclaimer:** This application is created by using Google's Antigravity AI assistant. Some issues may occur.

For now:  
**Thank you for reading,**  
**Have a great accessible experience!**
