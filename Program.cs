using Fleck;
using System.Text.Json;
using System.ServiceProcess;
using DPUruNet;

static void StopCompetingServices()
{
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Could not stop {name}: {ex.Message}");
            Console.WriteLine($"    → Run this program as Administrator.");
        }
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

static string J(object obj) => JsonSerializer.Serialize(obj);

static Reader OpenFirstReader()
{
    var readers = ReaderCollection.GetReaders();
    if (readers.Count == 0)
        throw new Exception("No fingerprint reader found. Make sure the scanner is plugged in.");

    var reader = readers[0];
    var result = reader.Open(Constants.CapturePriority.DP_PRIORITY_COOPERATIVE);
    if (result != Constants.ResultCode.DP_SUCCESS)
        throw new Exception($"Failed to open reader: {result}");

    reader.GetStatus();
    if (reader.Status.Status == Constants.ReaderStatuses.DP_STATUS_NEED_CALIBRATION)
        reader.Calibrate();

    return reader;
}

// CAPTURE: scan one finger → return base64 FMD
static void HandleCapture(IWebSocketConnection socket)
{
    Reader? reader = null;
    try
    {
        reader = OpenFirstReader();
        var tcs = new TaskCompletionSource<Fmd?>();

        Reader.CaptureCallback callback = result =>
        {
            if (result.Data == null || result.ResultCode != Constants.ResultCode.DP_SUCCESS)
            {
                if (result.Quality != Constants.CaptureQuality.DP_QUALITY_CANCELED)
                    tcs.TrySetException(new Exception($"Capture quality error: {result.Quality}"));
                return;
            }

            var fmdResult = FeatureExtraction.CreateFmdFromFid(result.Data, Constants.Formats.Fmd.ANSI);
            if (fmdResult.ResultCode == Constants.ResultCode.DP_SUCCESS)
                tcs.TrySetResult(fmdResult.Data);
            else
                tcs.TrySetException(new Exception($"Feature extraction failed: {fmdResult.ResultCode}"));
        };

        reader.On_Captured += callback;
        reader.CaptureAsync(Constants.Formats.Fid.ANSI, Constants.CaptureProcessing.DP_IMG_PROC_DEFAULT, reader.Capabilities.Resolutions[0]);

        Console.WriteLine("[*] Waiting for finger (CAPTURE)...");
        var fmd = tcs.Task.GetAwaiter().GetResult();

        socket.Send(J(new { type = "FMD_READY", data = Convert.ToBase64String(fmd!.Bytes) }));
        Console.WriteLine("[<] Sent FMD");
    }
    catch (Exception ex)
    {
        socket.Send(J(new { type = "ERROR", message = ex.Message }));
        Console.WriteLine($"[!] Capture error: {ex.Message}");
    }
    finally
    {
        reader?.CancelCapture();
        reader?.Dispose();
    }
}

// VERIFY: receive stored FMD from frontend, scan live finger, compare
static void HandleVerify(IWebSocketConnection socket, string enrolledFmdBase64)
{
    Reader? reader = null;
    try
    {
        // Reconstruct Fmd object from stored base64 bytes
        byte[] enrolledBytes = Convert.FromBase64String(enrolledFmdBase64);
        var importResult = Importer.ImportFmd(enrolledBytes, Constants.Formats.Fmd.ANSI, Constants.Formats.Fmd.ANSI);
        if (importResult.ResultCode != Constants.ResultCode.DP_SUCCESS)
            throw new Exception($"Failed to deserialize enrolled FMD: {importResult.ResultCode}");
        var enrolledFmd = importResult.Data;

        reader = OpenFirstReader();
        var tcs = new TaskCompletionSource<Fmd?>();

        Reader.CaptureCallback callback = result =>
        {
            if (result.Data == null || result.ResultCode != Constants.ResultCode.DP_SUCCESS)
            {
                if (result.Quality != Constants.CaptureQuality.DP_QUALITY_CANCELED)
                    tcs.TrySetException(new Exception($"Capture quality error: {result.Quality}"));
                return;
            }

            var fmdResult = FeatureExtraction.CreateFmdFromFid(result.Data, Constants.Formats.Fmd.ANSI);
            if (fmdResult.ResultCode == Constants.ResultCode.DP_SUCCESS)
                tcs.TrySetResult(fmdResult.Data);
            else
                tcs.TrySetException(new Exception($"Feature extraction failed: {fmdResult.ResultCode}"));
        };

        reader.On_Captured += callback;
        reader.CaptureAsync(Constants.Formats.Fid.ANSI, Constants.CaptureProcessing.DP_IMG_PROC_DEFAULT, reader.Capabilities.Resolutions[0]);

        Console.WriteLine("[*] Waiting for finger (VERIFY)...");
        var liveFmd = tcs.Task.GetAwaiter().GetResult();

        // Compare: score 0 = identical, higher = worse match
        // Threshold = PROBABILITY_ONE / 100_000 → 1-in-100,000 false accept rate
        const int PROBABILITY_ONE = 0x7fffffff;
        const int threshold = PROBABILITY_ONE / 100_000;

        CompareResult cmp = Comparison.Compare(enrolledFmd, 0, liveFmd!, 0);
        if (cmp.ResultCode != Constants.ResultCode.DP_SUCCESS)
            throw new Exception($"Comparison failed: {cmp.ResultCode}");

        bool matched = cmp.Score < threshold;
        socket.Send(J(new { type = "VERIFY_RESULT", match = matched, score = cmp.Score }));
        Console.WriteLine($"[<] Verify result: {(matched ? "MATCH" : "NO MATCH")} (score={cmp.Score}, threshold={threshold})");
    }
    catch (Exception ex)
    {
        socket.Send(J(new { type = "ERROR", message = ex.Message }));
        Console.WriteLine($"[!] Verify error: {ex.Message}");
    }
    finally
    {
        reader?.CancelCapture();
        reader?.Dispose();
    }
}

// ENROLL: scan 4 fingers → create enrollment FMD → return base64
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
            if (result.Data == null || result.ResultCode != Constants.ResultCode.DP_SUCCESS)
            {
                // Finger lifted too quickly — ignore and wait for next scan
                if (result.Quality != Constants.CaptureQuality.DP_QUALITY_CANCELED)
                {
                    captureError = $"Capture quality error: {result.Quality}";
                    sem.Release();
                }
                return;
            }

            var fmdResult = FeatureExtraction.CreateFmdFromFid(result.Data, Constants.Formats.Fmd.ANSI);
            if (fmdResult.ResultCode == Constants.ResultCode.DP_SUCCESS)
                lock (fmds) { fmds.Add(fmdResult.Data); }
            else
                captureError = $"Feature extraction failed: {fmdResult.ResultCode}";

            sem.Release();
        };

        reader.On_Captured += callback;
        reader.CaptureAsync(Constants.Formats.Fid.ANSI, Constants.CaptureProcessing.DP_IMG_PROC_DEFAULT, reader.Capabilities.Resolutions[0]);

        while (fmds.Count < required)
        {
            Console.WriteLine($"[*] Waiting for finger ({fmds.Count + 1}/{required})...");
            sem.Wait();

            if (captureError != null)
                throw new Exception(captureError);

            socket.Send(J(new { type = "SCAN_PROGRESS", step = fmds.Count, total = required }));
            Console.WriteLine($"[*] Sample {fmds.Count}/{required} captured");
        }

        reader.CancelCapture();

        var enrollResult = DPUruNet.Enrollment.CreateEnrollmentFmd(Constants.Formats.Fmd.ANSI, fmds);
        if (enrollResult.ResultCode != Constants.ResultCode.DP_SUCCESS)
            throw new Exception($"Enrollment failed: {enrollResult.ResultCode}");

        socket.Send(J(new { type = "ENROLL_READY", data = Convert.ToBase64String(enrollResult.Data.Bytes) }));
        Console.WriteLine("[<] Sent enrollment FMD");
    }
    catch (Exception ex)
    {
        socket.Send(J(new { type = "ERROR", message = ex.Message }));
        Console.WriteLine($"[!] Enroll error: {ex.Message}");
    }
    finally
    {
        reader?.CancelCapture();
        reader?.Dispose();
    }
}

// ── Startup ──────────────────────────────────────────────────────────────────

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
    socket.OnOpen = () => Console.WriteLine("[+] Frontend connected");
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
            default:
                Console.WriteLine($"[!] Unknown command: {type}");
                break;
        }
    };
});

Console.WriteLine("[*] Fingerprint bridge running on ws://localhost:9002");
Console.WriteLine("[*] Press ENTER to stop...");
Console.ReadLine();
