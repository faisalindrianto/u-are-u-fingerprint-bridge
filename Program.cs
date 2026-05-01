using Fleck;
using System.Text.Json;
using System.ServiceProcess;
using System.Security.Principal;
using Microsoft.Win32;
using DPUruNet;

// ── Helpers ──────────────────────────────────────────────────────────────────

static string J(object obj) => JsonSerializer.Serialize(obj);

static bool IsAdmin() =>
    new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

static string ServiceStatus(string name)
{
    try { return new ServiceController(name).Status.ToString(); }
    catch { return "not found"; }
}

// ── Diagnostics ──────────────────────────────────────────────────────────────

static string RunDiagnostics()
{
    var sb = new System.Text.StringBuilder();
    void L(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }

    L("═══════════════════ FINGERPRINT BRIDGE DIAGNOSTICS ═══════════════════");

    // 1. Runtime
    L($"  Admin:    {(IsAdmin() ? "YES  ✓" : "NO   ✗  ← must be admin to stop WbioSrvc")}");
    L($"  OS:       {Environment.OSVersion}");
    L($"  .NET:     {Environment.Version}");
    L($"  PID:      {Environment.ProcessId}");

    // 2. Services
    L();
    L("  Services:");
    foreach (var svc in new[] { "WbioSrvc", "DpHost" })
        L($"    {svc,-12} {ServiceStatus(svc)}");

    // 3. DLL presence
    L();
    L("  DLLs:");
    var dllPaths = new[]
    {
        (Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dpfj.dll"),    "app dir"),
        (Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dpfpdd.dll"),  "app dir"),
        (Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DPUruNet.dll"),"app dir"),
        (@"C:\Windows\System32\dpfj.dll",    "System32"),
        (@"C:\Windows\System32\dpfpdd.dll",  "System32"),
    };
    foreach (var (path, loc) in dllPaths)
        L($"    {(File.Exists(path) ? "✓" : "✗")} [{loc}] {Path.GetFileName(path)}");

    // 4. Driver detection — SDK (dpfpusbm) vs WBF (WUDFRd)
    L();
    L("  Driver service (registry):");
    try
    {
        using var sdkSvc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\dpfpusbm");
        L($"    dpfpusbm (DP SDK driver): {(sdkSvc != null ? "FOUND ✓" : "NOT FOUND  ← SDK driver not installed")}");
    }
    catch (Exception ex) { L($"    dpfpusbm check failed: {ex.Message}"); }

    // 5. USB device enumeration (VID_05BA = DigitalPersona)
    L();
    L("  USB devices (VID_05BA = DigitalPersona):");
    try
    {
        using var usbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usbKey == null) { L("    Cannot open USB registry key"); }
        else
        {
            var dpVids = usbKey.GetSubKeyNames()
                .Where(k => k.StartsWith("VID_05BA", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (dpVids.Count == 0)
            {
                L("    None found — is the scanner plugged in?");
            }
            else
            {
                foreach (var vid in dpVids)
                {
                    using var vidKey = usbKey.OpenSubKey(vid);
                    if (vidKey == null) continue;
                    foreach (var inst in vidKey.GetSubKeyNames())
                    {
                        using var instKey = vidKey.OpenSubKey(inst);
                        if (instKey == null) continue;
                        var service = instKey.GetValue("Service") as string ?? "unknown";
                        var desc    = instKey.GetValue("DeviceDesc") as string ?? "unknown";
                        // DeviceDesc is typically "@oem123.inf,%DeviceDesc%;Actual Name"
                        var descClean = desc.Contains(';') ? desc.Split(';').Last() : desc;
                        L($"    {vid}\\{inst}");
                        L($"      Desc:    {descClean}");
                        L($"      Service: {service}");
                        if (service.Equals("WUDFRd", StringComparison.OrdinalIgnoreCase) ||
                            service.Equals("WinUsb", StringComparison.OrdinalIgnoreCase))
                            L("      ⚠  WBF/WinUSB driver in use — SDK needs dpfpusbm instead");
                        else if (service.Equals("dpfpusbm", StringComparison.OrdinalIgnoreCase))
                            L("      ✓  DP SDK driver (dpfpusbm) — correct");
                    }
                }
            }
        }
    }
    catch (Exception ex) { L($"    USB registry scan failed: {ex.Message}"); }

    // 6. SDK reader enumeration
    L();
    L("  SDK ReaderCollection.GetReaders():");
    try
    {
        var readers = ReaderCollection.GetReaders();
        L($"    Count: {readers.Count}");
        for (int i = 0; i < readers.Count; i++)
        {
            var r = readers[i];
            L($"    [{i}] Name:   {r.Description.Name}");
            L($"         Serial: {r.Description.SerialNumber}");
            L($"         Id:     {r.Description.Id}");

            // Try opening to test access
            var openResult = r.Open(Constants.CapturePriority.DP_PRIORITY_COOPERATIVE);
            L($"         Open:   {openResult}");
            if (openResult == Constants.ResultCode.DP_SUCCESS)
            {
                var statusResult = r.GetStatus();
                L($"         Status: {(statusResult == Constants.ResultCode.DP_SUCCESS ? r.Status.Status.ToString() : $"GetStatus failed: {statusResult}")}");
                r.Dispose();
            }
        }
    }
    catch (Exception ex) { L($"    GetReaders() threw: {ex.GetType().Name}: {ex.Message}"); }

    L("══════════════════════════════════════════════════════════════════════");
    return sb.ToString();
}

// ── Service management ───────────────────────────────────────────────────────

static void StopCompetingServices()
{
    bool anyFailed = false;
    foreach (var name in new[] { "WbioSrvc", "DpHost" })
    {
        try
        {
            var svc = new ServiceController(name);
            if (svc.Status == ServiceControllerStatus.Running)
            {
                svc.Stop();
                svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                Console.WriteLine($"[*] Stopped service: {name}");
            }
            else
            {
                Console.WriteLine($"[*] Service {name} already stopped ({svc.Status})");
            }
        }
        catch (Exception ex)
        {
            anyFailed = true;
            Console.WriteLine($"[!] Could not stop {name}: {ex.Message}");
        }
    }
    if (anyFailed)
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  WARNING: Could not stop competing services.             ║");
        Console.WriteLine("║  The fingerprint reader may be held by Windows.          ║");
        Console.WriteLine("║  → Close this app and re-run as Administrator.           ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.WriteLine();
    }
    else
    {
        // Give Windows a moment to release the device handle after services stop
        Thread.Sleep(500);
    }
}

static void StartCompetingServices()
{
    foreach (var name in new[] { "WbioSrvc", "DpHost" })
    {
        try
        {
            var svc = new ServiceController(name);
            if (svc.Status == ServiceControllerStatus.Stopped)
            {
                svc.Start();
                Console.WriteLine($"[*] Restarted service: {name}");
            }
        }
        catch { /* best-effort */ }
    }
}

// ── Reader helpers ───────────────────────────────────────────────────────────

static Reader OpenFirstReader()
{
    Console.WriteLine("[~] GetReaders...");
    var readers = ReaderCollection.GetReaders();
    Console.WriteLine($"[~] Found {readers.Count} reader(s)");
    if (readers.Count == 0)
        throw new Exception("No fingerprint reader found. Check: (1) scanner is plugged in, (2) SDK driver dpfpusbm is installed (not WBF), (3) run DIAG command for details.");

    var reader = readers[0];
    Console.WriteLine($"[~] Reader[0]: name={reader.Description.Name} serial={reader.Description.SerialNumber}");

    // Retry up to 3 times — device may need a moment after WbioSrvc stops
    Constants.ResultCode result = Constants.ResultCode.DP_DEVICE_FAILURE;
    for (int attempt = 1; attempt <= 3; attempt++)
    {
        Console.WriteLine($"[~] Open attempt {attempt}/3...");
        result = reader.Open(Constants.CapturePriority.DP_PRIORITY_COOPERATIVE);
        Console.WriteLine($"[~] Open result: {result}");
        if (result == Constants.ResultCode.DP_SUCCESS) break;
        if (attempt < 3) Thread.Sleep(800);
    }
    if (result != Constants.ResultCode.DP_SUCCESS)
        throw new Exception(
            $"Failed to open reader: {result}. " +
            $"Admin={IsAdmin()}, WbioSrvc={ServiceStatus("WbioSrvc")}, DpHost={ServiceStatus("DpHost")}. " +
            "Send DIAG command for full report.");

    Console.WriteLine("[~] GetStatus...");
    var statusResult = reader.GetStatus();
    Console.WriteLine($"[~] Status result: {statusResult}, device status: {reader.Status.Status}");
    if (statusResult != Constants.ResultCode.DP_SUCCESS)
        throw new Exception($"GetStatus failed: {statusResult}");

    if (reader.Status.Status == Constants.ReaderStatuses.DP_STATUS_NEED_CALIBRATION)
    {
        Console.WriteLine("[~] Calibrating...");
        reader.Calibrate();
        Console.WriteLine("[~] Calibration done");
    }

    Console.WriteLine($"[~] Resolutions available: {reader.Capabilities.Resolutions.Length}");
    if (reader.Capabilities.Resolutions.Length == 0)
        throw new Exception("Reader has no supported resolutions.");

    return reader;
}

static Constants.ResultCode StartCapture(Reader reader)
{
    var res = reader.CaptureAsync(
        Constants.Formats.Fid.ANSI,
        Constants.CaptureProcessing.DP_IMG_PROC_DEFAULT,
        reader.Capabilities.Resolutions[0]);
    Console.WriteLine($"[~] CaptureAsync: {res}");
    return res;
}

// ── Handlers ─────────────────────────────────────────────────────────────────

static void HandleCapture(IWebSocketConnection socket)
{
    Reader? reader = null;
    try
    {
        reader = OpenFirstReader();
        var tcs = new TaskCompletionSource<Fmd?>();

        Reader.CaptureCallback callback = result =>
        {
            Console.WriteLine($"[~] Capture callback: code={result.ResultCode} quality={result.Quality}");
            if (result.Data == null || result.ResultCode != Constants.ResultCode.DP_SUCCESS)
            {
                if (result.Quality != Constants.CaptureQuality.DP_QUALITY_CANCELED)
                    tcs.TrySetException(new Exception($"Capture quality error: {result.Quality}"));
                return;
            }
            var fmdResult = FeatureExtraction.CreateFmdFromFid(result.Data, Constants.Formats.Fmd.ANSI);
            Console.WriteLine($"[~] FeatureExtraction: {fmdResult.ResultCode}");
            if (fmdResult.ResultCode == Constants.ResultCode.DP_SUCCESS)
                tcs.TrySetResult(fmdResult.Data);
            else
                tcs.TrySetException(new Exception($"Feature extraction failed: {fmdResult.ResultCode}"));
        };

        reader.On_Captured += callback;
        var captureCode = StartCapture(reader);
        if (captureCode != Constants.ResultCode.DP_SUCCESS)
            throw new Exception($"CaptureAsync failed: {captureCode}");

        Console.WriteLine("[*] Waiting for finger (CAPTURE)...");
        var fmd = tcs.Task.GetAwaiter().GetResult();

        socket.Send(J(new { type = "FMD_READY", data = Convert.ToBase64String(fmd!.Bytes) }));
        Console.WriteLine($"[<] Sent FMD ({fmd.Bytes.Length} bytes)");
    }
    catch (Exception ex)
    {
        socket.Send(J(new { type = "ERROR", message = ex.Message }));
        Console.WriteLine($"[!] CAPTURE error: {ex.Message}");
    }
    finally
    {
        reader?.CancelCapture();
        reader?.Dispose();
    }
}

static void HandleVerify(IWebSocketConnection socket, string enrolledFmdBase64)
{
    Reader? reader = null;
    try
    {
        byte[] enrolledBytes = Convert.FromBase64String(enrolledFmdBase64);
        Console.WriteLine($"[~] Enrolled FMD: {enrolledBytes.Length} bytes");
        var importResult = Importer.ImportFmd(enrolledBytes, Constants.Formats.Fmd.ANSI, Constants.Formats.Fmd.ANSI);
        Console.WriteLine($"[~] ImportFmd: {importResult.ResultCode}");
        if (importResult.ResultCode != Constants.ResultCode.DP_SUCCESS)
            throw new Exception($"Failed to deserialize enrolled FMD: {importResult.ResultCode}");
        var enrolledFmd = importResult.Data;

        reader = OpenFirstReader();
        var tcs = new TaskCompletionSource<Fmd?>();

        Reader.CaptureCallback callback = result =>
        {
            Console.WriteLine($"[~] Capture callback: code={result.ResultCode} quality={result.Quality}");
            if (result.Data == null || result.ResultCode != Constants.ResultCode.DP_SUCCESS)
            {
                if (result.Quality != Constants.CaptureQuality.DP_QUALITY_CANCELED)
                    tcs.TrySetException(new Exception($"Capture quality error: {result.Quality}"));
                return;
            }
            var fmdResult = FeatureExtraction.CreateFmdFromFid(result.Data, Constants.Formats.Fmd.ANSI);
            Console.WriteLine($"[~] FeatureExtraction: {fmdResult.ResultCode}");
            if (fmdResult.ResultCode == Constants.ResultCode.DP_SUCCESS)
                tcs.TrySetResult(fmdResult.Data);
            else
                tcs.TrySetException(new Exception($"Feature extraction failed: {fmdResult.ResultCode}"));
        };

        reader.On_Captured += callback;
        var captureCode = StartCapture(reader);
        if (captureCode != Constants.ResultCode.DP_SUCCESS)
            throw new Exception($"CaptureAsync failed: {captureCode}");

        Console.WriteLine("[*] Waiting for finger (VERIFY)...");
        var liveFmd = tcs.Task.GetAwaiter().GetResult();

        const int PROBABILITY_ONE = 0x7fffffff;
        const int threshold = PROBABILITY_ONE / 100_000;

        CompareResult cmp = Comparison.Compare(enrolledFmd, 0, liveFmd!, 0);
        Console.WriteLine($"[~] Comparison: code={cmp.ResultCode} score={cmp.Score} threshold={threshold}");
        if (cmp.ResultCode != Constants.ResultCode.DP_SUCCESS)
            throw new Exception($"Comparison failed: {cmp.ResultCode}");

        bool matched = cmp.Score < threshold;
        socket.Send(J(new { type = "VERIFY_RESULT", match = matched, score = cmp.Score }));
        Console.WriteLine($"[<] Verify: {(matched ? "MATCH" : "NO MATCH")} (score={cmp.Score})");
    }
    catch (Exception ex)
    {
        socket.Send(J(new { type = "ERROR", message = ex.Message }));
        Console.WriteLine($"[!] VERIFY error: {ex.Message}");
    }
    finally
    {
        reader?.CancelCapture();
        reader?.Dispose();
    }
}

static void HandleEnroll(IWebSocketConnection socket)
{
    Reader? reader = null;
    try
    {
        reader = OpenFirstReader();
        const int required = 4;
        var fmds = new List<Fmd>();
        var sem = new SemaphoreSlim(0);
        string? captureError = null;

        Reader.CaptureCallback callback = result =>
        {
            Console.WriteLine($"[~] Capture callback: code={result.ResultCode} quality={result.Quality}");
            if (result.Data == null || result.ResultCode != Constants.ResultCode.DP_SUCCESS)
            {
                if (result.Quality != Constants.CaptureQuality.DP_QUALITY_CANCELED)
                {
                    captureError = $"Capture quality error: {result.Quality}";
                    sem.Release();
                }
                return;
            }
            var fmdResult = FeatureExtraction.CreateFmdFromFid(result.Data, Constants.Formats.Fmd.ANSI);
            Console.WriteLine($"[~] FeatureExtraction: {fmdResult.ResultCode}");
            if (fmdResult.ResultCode == Constants.ResultCode.DP_SUCCESS)
                lock (fmds) { fmds.Add(fmdResult.Data); }
            else
                captureError = $"Feature extraction failed: {fmdResult.ResultCode}";
            sem.Release();
        };

        reader.On_Captured += callback;
        var captureCode = StartCapture(reader);
        if (captureCode != Constants.ResultCode.DP_SUCCESS)
            throw new Exception($"CaptureAsync failed: {captureCode}");

        while (fmds.Count < required)
        {
            Console.WriteLine($"[*] Waiting for finger ({fmds.Count + 1}/{required})...");
            sem.Wait();
            if (captureError != null) throw new Exception(captureError);
            socket.Send(J(new { type = "SCAN_PROGRESS", step = fmds.Count, total = required }));
            Console.WriteLine($"[*] Sample {fmds.Count}/{required} captured");
        }

        reader.CancelCapture();

        var enrollResult = DPUruNet.Enrollment.CreateEnrollmentFmd(Constants.Formats.Fmd.ANSI, fmds);
        Console.WriteLine($"[~] CreateEnrollmentFmd: {enrollResult.ResultCode}");
        if (enrollResult.ResultCode != Constants.ResultCode.DP_SUCCESS)
            throw new Exception($"Enrollment failed: {enrollResult.ResultCode}");

        socket.Send(J(new { type = "ENROLL_READY", data = Convert.ToBase64String(enrollResult.Data.Bytes) }));
        Console.WriteLine($"[<] Sent enrollment FMD ({enrollResult.Data.Bytes.Length} bytes)");
    }
    catch (Exception ex)
    {
        socket.Send(J(new { type = "ERROR", message = ex.Message }));
        Console.WriteLine($"[!] ENROLL error: {ex.Message}");
    }
    finally
    {
        reader?.CancelCapture();
        reader?.Dispose();
    }
}

// ── Startup ──────────────────────────────────────────────────────────────────

Console.WriteLine($"[*] Fingerprint Bridge starting  (admin={IsAdmin()})");
RunDiagnostics();

Console.WriteLine("[*] Stopping competing services...");
StopCompetingServices();

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    Console.WriteLine("[*] Restoring services...");
    StartCompetingServices();
};

var server = new WebSocketServer("ws://0.0.0.0:9002");

server.Start(socket =>
{
    socket.OnOpen  = () => Console.WriteLine("[+] Frontend connected");
    socket.OnClose = () => Console.WriteLine("[-] Frontend disconnected");

    socket.OnMessage = message =>
    {
        Console.WriteLine($"[>] Received: {message}");

        var cmd = JsonSerializer.Deserialize<Dictionary<string, string>>(message);
        if (cmd == null) return;
        cmd.TryGetValue("type", out var type);

        switch (type)
        {
            case "CAPTURE":
                Task.Run(() => HandleCapture(socket));
                break;
            case "ENROLL":
                Task.Run(() => HandleEnroll(socket));
                break;
            case "VERIFY":
                cmd.TryGetValue("enrolledFmd", out var enrolledFmd);
                if (string.IsNullOrEmpty(enrolledFmd))
                {
                    socket.Send(J(new { type = "ERROR", message = "VERIFY requires enrolledFmd field" }));
                    break;
                }
                Task.Run(() => HandleVerify(socket, enrolledFmd));
                break;
            case "DIAG":
                Task.Run(() =>
                {
                    var report = RunDiagnostics();
                    socket.Send(J(new { type = "DIAG_REPORT", report }));
                });
                break;
            default:
                Console.WriteLine($"[!] Unknown command: {type}");
                break;
        }
    };
});

Console.WriteLine("[*] Fingerprint bridge running on ws://localhost:9002");
Console.WriteLine("[*] Commands: CAPTURE | ENROLL | VERIFY | DIAG");
Console.WriteLine("[*] Press ENTER to stop...");
Console.ReadLine();
