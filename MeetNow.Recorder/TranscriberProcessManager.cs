using System.Diagnostics;
using System.IO;
using MeetNow.Recording.Core.Config;
using Serilog;

namespace MeetNow.Recorder;

public class TranscriberProcessManager : IDisposable
{
    private readonly RecorderConfig _config;
    private readonly string _transcriberDir;
    private Process? _process;
    private int _restartCount;
    private const int MaxRestarts = 5;
    private const int RestartDelayBaseMs = 2000;

    public TranscriberProcessManager(RecorderConfig config)
    {
        _config = config;
        // Transcriber lives alongside the recorder executable
        var exeDir = AppContext.BaseDirectory;
        _transcriberDir = Path.Combine(exeDir, "MeetNow.Recorder.Transcriber");
    }

    public async Task StartAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _restartCount < MaxRestarts)
        {
            try
            {
                StartProcess();
                await _process!.WaitForExitAsync(ct);

                if (ct.IsCancellationRequested)
                    break;

                Log.Warning("Transcriber process exited with code {ExitCode}. Restart {N}/{Max}",
                    _process.ExitCode, _restartCount + 1, MaxRestarts);

                _restartCount++;
                var delay = RestartDelayBaseMs * (1 << Math.Min(_restartCount, 5));
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start transcriber process");
                _restartCount++;
                await Task.Delay(RestartDelayBaseMs * _restartCount, ct);
            }
        }
    }

    private void StartProcess()
    {
        var args = new List<string>
        {
            "-m", "transcriber",
            "--model", _config.TranscriberModel,
            "--watch-dir", _config.OutputDir,
            "--device", _config.TranscriberDevice,
            "--poll-interval", _config.TranscriberPollIntervalSeconds.ToString()
        };

        if (_config.TranscriberLanguage != null)
        {
            args.Add("--language");
            args.Add(_config.TranscriberLanguage);
        }

        var psi = new ProcessStartInfo
        {
            FileName = _config.PythonPath,
            WorkingDirectory = _transcriberDir,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        _process = Process.Start(psi);

        if (_process != null)
        {
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Log.Information("[Transcriber] {Line}", e.Data);
            };
            _process.BeginErrorReadLine();
            Log.Information("Transcriber started (PID {Pid})", _process.Id);
        }
    }

    public void SignalStop()
    {
        if (_process == null || _process.HasExited)
            return;

        // Write stop flag
        var stopFlag = Path.Combine(_config.OutputDir, "stop.flag");
        File.WriteAllText(stopFlag, "stop");
        Log.Information("Stop flag written, waiting for transcriber to finish...");
    }

    public void Dispose()
    {
        if (_process != null && !_process.HasExited)
        {
            SignalStop();
            if (!_process.WaitForExit(5000))
            {
                Log.Warning("Transcriber did not stop gracefully, killing...");
                _process.Kill(entireProcessTree: true);
            }
            _process.Dispose();
        }

        // Clean up stop flag
        var stopFlag = Path.Combine(_config.OutputDir, "stop.flag");
        if (File.Exists(stopFlag))
            File.Delete(stopFlag);
    }
}
