# FingerprintBridge

A Windows bridge app that connects a **U.are.U 4500** fingerprint scanner to a web app over WebSocket. Built for the **Buku Nilai** project — lecturers must scan their fingerprint before submitting grades.

## How it works

```
Browser (Nuxt)  ──WebSocket ws://localhost:9002──  FingerprintBridge.exe  ──USB──  U.are.U 4500
                                                          ↕
                                                    dpfj.dll / dpfpdd.dll
                                                    (DigitalPersona SDK)
```

The bridge handles enrollment (capturing a template) and verification (comparing a live scan against a stored template). All biometric comparison happens locally in the bridge using the DigitalPersona SDK — the Laravel backend only stores and retrieves the raw FMD bytes.

## Requirements

- Windows 10/11 x64
- U.are.U 4500 scanner plugged in via USB
- [DigitalPersona U.are.U SDK](https://www.hidglobal.com/products/software/digitalpersona) installed (provides `dpfj.dll`, `dpfpdd.dll` in System32)
- .NET 8 SDK (dev) or just the runtime (client)
- Run as **Administrator** (bridge stops WbioSrvc/DpHost on startup to get exclusive device access)

## WebSocket Protocol

**Frontend → Bridge**
```json
{ "type": "CAPTURE" }
{ "type": "ENROLL" }
{ "type": "VERIFY", "enrolledFmd": "<base64>" }
```

**Bridge → Frontend**
```json
{ "type": "FMD_READY",     "data": "<base64 FMD>" }
{ "type": "ENROLL_READY",  "data": "<base64 FMD>" }
{ "type": "SCAN_PROGRESS", "step": 1, "total": 4  }
{ "type": "VERIFY_RESULT", "match": true, "score": 1234 }
{ "type": "ERROR",         "message": "..." }
```

- `ENROLL` captures 4 scans and returns a single enrollment template.
- `VERIFY` takes a stored enrollment FMD (fetched from Laravel), captures a live scan, and returns match/no-match with a dissimilarity score. Score `< 21474` = match (1-in-100,000 false accept rate).

## Running (dev)

```powershell
# In an Administrator terminal
dotnet run
```

## Build for distribution

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output in `bin/Release/net8.0-windows/win-x64/publish/`. Copy `FingerprintBridge.exe` and `libs/DPUruNet.dll` to the client PC.

See [`SETUP.md`](SETUP.md) for full client PC installation steps.

## Test page

Open `index.html` in a browser while the bridge is running:

1. **Enroll** — scans 4 times, stores the enrollment FMD in the page
2. **Verify** — scans once, compares against the stored FMD, shows MATCH / NO MATCH
3. **Capture** — raw scan with no comparison (debug)

## Project structure

```
FingerprintBridge/
├── Program.cs          # WebSocket server + SDK integration
├── libs/
│   └── DPUruNet.dll    # DigitalPersona managed SDK wrapper
├── index.html          # Browser test page
├── SETUP.md            # Client PC installation guide
└── FingerprintBridge.csproj
```
