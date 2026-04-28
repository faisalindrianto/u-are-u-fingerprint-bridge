# FingerprintBridge — Client PC Setup Guide

## What This Is

A small Windows app (`FingerprintBridge.exe`) that sits between the **Buku Nilai** web app and the **U.are.U 4500** fingerprint scanner. The web app talks to it over WebSocket (`ws://localhost:9002`).

---

## Prerequisites

| Item | Notes |
|------|-------|
| Windows 10/11 x64 | Required |
| U.are.U 4500 scanner | Plugged in via USB |
| DigitalPersona U.are.U SDK | Installed (provides `dpfj.dll`, `dpfpdd.dll`) |
| HID USB driver | Installed (bundled with the SDK installer) |

---

## Step 1 — Install the DigitalPersona SDK

1. Run the SDK installer (`install-driver.exe` or the full SDK setup).
2. Accept defaults. It will install to `C:\Program Files\DigitalPersona\`.
3. It registers two things Windows needs:
   - USB driver for the scanner (HID Biometric device)
   - Two native DLLs in `C:\Windows\System32\`: `dpfj.dll` and `dpfpdd.dll`

**Verify:** Open Device Manager → look for "U.are.U 4500 Fingerprint Reader" under **Biometric devices**. Status should be "Device is working properly".

---

## Step 2 — Copy the Bridge Files

Copy this folder to the client PC. The minimum required files:

```
FingerprintBridge\
├── FingerprintBridge.exe      ← the bridge (self-contained, no .NET needed)
├── libs\
│   └── DPUruNet.dll           ← DigitalPersona managed SDK wrapper
└── index.html                 ← test page (optional, for verification)
```

> **Build the self-contained exe first** (on your dev machine):
> ```powershell
> dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
> ```
> Output goes to `bin\Release\net8.0-windows\win-x64\publish\`.

---

## Step 3 — Run as Administrator

The bridge needs to temporarily stop two Windows services that monopolize the scanner:
- **WbioSrvc** (Windows Biometric Service — part of Windows Hello)
- **DpHost** (DigitalPersona Authentication Service)

It does this automatically on startup and restores them when you close it.

**Because of this, the bridge must always be run as Administrator:**

1. Right-click `FingerprintBridge.exe`
2. Select **"Run as administrator"**
3. Allow the UAC prompt

You should see:
```
[*] Stopping competing services...
[*] Stopped service: WbioSrvc
[*] Stopped service: DpHost
[*] Fingerprint bridge running on ws://localhost:9002
[*] Press ENTER to stop...
```

---

## Step 4 — Verify with the Test Page

1. Open `index.html` in a browser (double-click it, or drag into Chrome/Edge).
2. The status indicator should turn **green** ("Connected").
3. Click **Capture (verify)** → place your finger → you should see a base64 FMD appear.
4. Click **Enroll (4 scans)** → place your finger 4 times → you should see "Enrollment FMD received".

If both work, the bridge is correctly installed.

---

## Step 5 — Auto-start on Windows Login (Optional)

So the dosen doesn't have to manually start the bridge:

1. Create a shortcut to `FingerprintBridge.exe`.
2. Right-click the shortcut → **Properties** → **Advanced** → check **"Run as administrator"**.
3. Press `Win + R`, type `shell:startup`, press Enter.
4. Move the shortcut into that Startup folder.

Windows will prompt UAC once at each login.

---

## Troubleshooting

### "No fingerprint reader found"
- Check Device Manager: is the U.are.U 4500 listed under Biometric devices?
- If it shows a yellow warning icon, reinstall the driver.
- Try a different USB port.

### "Failed to open reader: DP_DEVICE_FAILURE"
- The bridge is **not running as Administrator** — the service stop failed silently.
- Solution: right-click → "Run as administrator".

### "Connected" in test page but Capture fails immediately
- Same as above — services weren't stopped.
- Check the console window: did it print `[*] Stopped service: WbioSrvc`?

### Browser shows "Disconnected" / keeps retrying
- The bridge isn't running, or it crashed.
- Check the console window for error messages.
- Make sure port 9002 isn't blocked by a firewall.

### Windows Hello fingerprint login stops working while bridge is running
- Expected. The bridge stops WbioSrvc temporarily.
- When you close the bridge (press ENTER), WbioSrvc restarts and Windows Hello works again.

---

## Architecture Reference

```
Nuxt Frontend (browser)
    ↕ WebSocket ws://localhost:9002
FingerprintBridge.exe (running on dosen's PC, as Admin)
    ↕ DPUruNet.dll → dpfpdd.dll (USB)
U.are.U 4500 Scanner
    ↕ HTTP POST (base64 FMD)
Laravel Backend (stores templates, does matching)
```

### WebSocket Protocol

**Frontend → Bridge:**
```json
{ "type": "CAPTURE" }
{ "type": "ENROLL" }
```

**Bridge → Frontend:**
```json
{ "type": "FMD_READY",      "data": "<base64 FMD>" }
{ "type": "ENROLL_READY",   "data": "<base64 FMD>" }
{ "type": "SCAN_PROGRESS",  "step": 1, "total": 4  }
{ "type": "VERIFY_RESULT",  "match": true, "score": 1234 }
{ "type": "ERROR",          "message": "<reason>"  }
```

### Command details

- **CAPTURE** → scans one finger, returns raw `FMD_READY`. Useful for debugging.
- **ENROLL** → scans 4 fingers, sends `SCAN_PROGRESS` after each, returns `ENROLL_READY` with the enrollment template to store in Laravel.
- **VERIFY** → receives the stored enrollment FMD from the frontend (fetched from Laravel), scans a live finger, compares them, returns `VERIFY_RESULT`.

### Why comparison happens in the bridge (not Laravel)

The comparison algorithm is in `dpfj.dll` — a native Windows DLL from DigitalPersona. PHP (Laravel) cannot call it. So the flow is:

1. Frontend fetches stored FMD from Laravel: `GET /api/fingerprint/enrolled-fmd`
2. Frontend sends `{ "type": "VERIFY", "enrolledFmd": "<base64>" }` to bridge
3. Bridge captures live scan, compares, returns `{ "type": "VERIFY_RESULT", "match": true/false, "score": N }`
4. Frontend sends result to Laravel or unlocks grading form directly

### Match threshold

Score `< 21474` → match (1-in-100,000 false accept rate — meaning a random person's finger has a 0.001% chance of passing).
