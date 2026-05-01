# FingerprintBridge — Client PC Setup Guide

## What This Is

A small Windows app (`FingerprintBridge.exe`) that sits between the **Buku Nilai** web app and the **U.are.U 4500** fingerprint scanner. The web app talks to it over WebSocket (`ws://localhost:9002`).

---

## Step 1 — Install the DigitalPersona Driver (first time only)

The scanner needs DigitalPersona's own USB driver (`dpfpusbm`). Windows sometimes auto-installs the wrong one (WBF/`WUDFRd`), which blocks the bridge from accessing the scanner.

**Install the RTE (Runtime Environment) — not the SDK:**

```
UareUWin300_20170223.1115_2\RTE\x64\setup.exe
```

> If you get "incompatible products installed" during RTE setup, it means the full SDK was already installed — that's fine, the SDK includes the driver. Just reboot and skip to Step 2.

**After install, reboot the laptop.**

**Verify:** Open Device Manager → the scanner should appear under **Biometric devices** as "U.are.U 4500 Fingerprint Reader" with no warning icon.

---

## Step 2 — Copy the Bridge Files

Build the self-contained exe on your dev machine:
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
Output: `bin\Release\net8.0-windows\win-x64\publish\`

Copy these files to dosen's PC (skip `FingerprintBridge.pdb`):

```
📦 copy to dosen's PC
├── FingerprintBridge.exe   ← self-contained, no .NET install needed
├── dpfj.dll
├── dpfpdd.dll
├── dpfpdd_4k.dll
├── dpfpdd5000.dll
├── dpfpdd_ptapi.dll
├── dpfr6.dll
├── dpfr7.dll
└── index.html              ← test page
```

All files must stay in the same folder.

---

## Step 3 — Run as Administrator

The bridge must stop two Windows services that hold the scanner (`WbioSrvc`, `DpHost`). This requires admin rights.

**Right-click `FingerprintBridge.exe` → Run as administrator**

You should see:
```
[*] Stopped service: WbioSrvc
[*] Stopped service: DpHost
[*] Fingerprint bridge running on ws://localhost:9002
```

> When the bridge closes, it automatically restarts those services so Windows Hello works again.

---

## Step 4 — Verify with the Test Page

1. Open `index.html` in Chrome or Edge
2. Status turns **green** ("Connected")
3. Click **Enroll (4 scans)** → scan finger 4 times
4. Click **Verify finger** → scan again → should show ✔ MATCH

---

## Step 5 — Auto-start on Login (Optional)

So dosen doesn't have to manually run it every time:

```powershell
schtasks /create /tn "FingerprintBridge" /tr "C:\path\to\FingerprintBridge.exe" /sc onlogon /rl highest /f
```

Replace `C:\path\to\` with the actual folder. Run this command once as Administrator. From then on, the bridge starts automatically on every login with no UAC prompt.

To remove it:
```powershell
schtasks /delete /tn "FingerprintBridge" /f
```

---

## Troubleshooting

### Step 0 — Run DIAG first
When something doesn't work, always start here:
1. Run `FingerprintBridge.exe` as Administrator
2. Open `index.html` in a browser → wait for green "Connected"
3. Click **Run DIAG**
4. Read the output — it tells you exactly what's wrong

---

### "No fingerprint reader found" — WBF driver (most common on fresh laptops)

**Symptom in DIAG:**
```
Service: WUDFRd   ← wrong driver
dpfpusbm:         NOT FOUND
Count: 0
```

Windows auto-installed the wrong driver. Fix it:

**Option A — Install RTE first (clean laptop, no SDK yet):**
```
UareUWin300_20170223.1115_2\RTE\x64\setup.exe
```
Reboot after install. Done.

**Option B — SDK already installed (RTE gives "incompatible products" error):**

The SDK installs the driver files but doesn't switch the device automatically. Force it via Device Manager:

1. Open **Device Manager** (right-click Start → Device Manager)
2. Find **"U.are.U 4500 Fingerprint Reader"** under **Biometric devices**
3. Right-click → **Update driver**
4. **Browse my computer for drivers**
5. **Let me pick from a list of available drivers on my computer**
6. Select the **DigitalPersona** entry (not the WBF one)
7. Click Next → install
8. Unplug and replug the USB scanner

Run DIAG again — should now show `dpfpusbm` and `Count: 1`.

---

### "Failed to open reader: DP_DEVICE_FAILURE"

Bridge is not running as Administrator — it couldn't stop `WbioSrvc`.

**Fix:** Right-click `FingerprintBridge.exe` → **Run as administrator**

**Confirm it worked** — console should show:
```
[*] Stopped service: WbioSrvc
[*] Stopped service: DpHost
```

---

### "No fingerprint reader found" — scanner not plugged in
Check the USB cable. Try a different USB port. Check Device Manager for any yellow warning icons.

### DIAG shows `DPUruNet.dll ✗ [app dir]`
Normal — `DPUruNet.dll` is bundled inside the single-file exe. Ignore this line.

### "Disconnected" / browser keeps retrying
Bridge isn't running or crashed. Check the console window for error messages.

### Windows Hello stops working while bridge is running
Expected — bridge pauses `WbioSrvc`. Restores it automatically when bridge exits.

---

## Architecture

```
Nuxt Frontend (browser)
    ↕ WebSocket ws://localhost:9002
FingerprintBridge.exe (dosen's PC, run as Admin)
    ↕ DPUruNet.dll → dpfpdd.dll → dpfpusbm driver
U.are.U 4500 Scanner
    ↕ HTTP POST (base64 FMD)
Laravel Backend
```

## WebSocket Protocol

**Frontend → Bridge:**
```json
{ "type": "CAPTURE" }
{ "type": "ENROLL" }
{ "type": "VERIFY", "enrolledFmd": "<base64>" }
{ "type": "DIAG" }
```

**Bridge → Frontend:**
```json
{ "type": "FMD_READY",     "data": "<base64 FMD>" }
{ "type": "ENROLL_READY",  "data": "<base64 FMD>" }
{ "type": "SCAN_PROGRESS", "step": 1, "total": 4 }
{ "type": "VERIFY_RESULT", "match": true, "score": 1234 }
{ "type": "DIAG_REPORT",   "report": "..." }
{ "type": "ERROR",         "message": "<reason>" }
```

**Commands:**
- **CAPTURE** — scans one finger, returns raw FMD. For debugging.
- **ENROLL** — scans 4 fingers, returns enrollment template to store in Laravel.
- **VERIFY** — receives stored FMD from frontend, scans live finger, compares, returns match/no-match.
- **DIAG** — prints full system diagnostics (admin status, driver, DLLs, reader list).

**Why comparison runs in the bridge (not Laravel):**
The `dpfj.dll` comparison algorithm is a native Windows DLL — PHP can't call it. So the frontend fetches the stored FMD from Laravel, sends it to the bridge, and the bridge does the comparison locally.

**Match threshold:** score `< 21474` = match (1-in-100,000 false accept rate).
