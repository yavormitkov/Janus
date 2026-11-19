using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace JanusClearingEngine.Infrastructure;

public sealed class FileTopologyManager
{
    private readonly string _inboundPath;
    private readonly string _workingPath;
    private readonly string _okPath;
    private readonly string _errorPath;
    private FileSystemWatcher? _watcher;
    private readonly int _watcherDelayMilliseconds;

    public event Action<string>? OnFileDropped;

    public FileTopologyManager(IConfiguration configuration)
    {
        _inboundPath = configuration["EasyPayConfiguration:InboundPath"] ?? throw new InvalidOperationException("Missing InboundPath.");
        _workingPath = configuration["EasyPayConfiguration:WorkingPath"] ?? throw new InvalidOperationException("Missing WorkingPath.");
        _okPath = configuration["EasyPayConfiguration:OkPath"] ?? throw new InvalidOperationException("Missing OkPath.");
        _errorPath = configuration["EasyPayConfiguration:ErrorPath"] ?? throw new InvalidOperationException("Missing ErrorPath.");
        var delayConfig = configuration.GetSection("ClearingSettings")["WatcherDelayMilliseconds"];
        _watcherDelayMilliseconds = string.IsNullOrEmpty(delayConfig) ? 100 : int.Parse(delayConfig);

        Directory.CreateDirectory(_inboundPath);
        Directory.CreateDirectory(_workingPath);
        Directory.CreateDirectory(_okPath);
        Directory.CreateDirectory(_errorPath);
    }

    public void InitializeWatcher()
    {
        _watcher = new FileSystemWatcher(_inboundPath, "*.dat.sign")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };

        _watcher.Created += OnSignFileCreated;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnSignFileCreated(object sender, FileSystemEventArgs e)
    {
        string signFilePath = Path.GetFullPath(e.FullPath);
        string datFilePath = Path.ChangeExtension(signFilePath, null);

        Task.Delay(_watcherDelayMilliseconds).Wait();

        if (File.Exists(datFilePath))
        {
            OnFileDropped?.Invoke(datFilePath);
        }
    }

    public string EnsureInWorking(string filePath)
    {
        if (Path.GetDirectoryName(filePath)?.Equals(Path.GetFullPath(_workingPath), StringComparison.OrdinalIgnoreCase) == true)
        {
            return filePath;
        }

        string fileName = Path.GetFileName(filePath);
        string sourceSignPath = filePath + ".sign";
        string targetDatPath = Path.Combine(_workingPath, fileName);
        string targetSignPath = Path.Combine(_workingPath, fileName + ".sign");

        if (File.Exists(targetSignPath)) File.Delete(targetSignPath);
        if (File.Exists(targetDatPath)) File.Delete(targetDatPath);

        if (File.Exists(sourceSignPath)) File.Move(sourceSignPath, targetSignPath);
        if (File.Exists(filePath)) File.Move(filePath, targetDatPath);

        return targetDatPath;
    }

    public IEnumerable<string> GetCrashRecoveredWorkingFiles()
    {
        return Directory.GetFiles(_workingPath, "*.dat");
    }

    public string GetNextAvailableFile()
    {
        var inboundSignFiles = Directory.GetFiles(_inboundPath, "*.dat.sign");
        if (inboundSignFiles.Length > 0)
        {
            string sourceSignPath = inboundSignFiles[0];
            string sourceDatPath = sourceSignPath.Substring(0, sourceSignPath.Length - 5);
            if (File.Exists(sourceDatPath))
            {
                return EnsureInWorking(sourceDatPath);
            }
        }

        return string.Empty;
    }

    public void MoveToOk(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        string targetPath = Path.Combine(_okPath, fileName);

        if (File.Exists(targetPath)) File.Delete(targetPath);
        if (File.Exists(filePath)) File.Move(filePath, targetPath);
    }

    public void MoveToError(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        string targetPath = Path.Combine(_errorPath, fileName);

        if (File.Exists(targetPath)) File.Delete(targetPath);
        if (File.Exists(filePath)) File.Move(filePath, targetPath);
    }

    public void WriteErrorLog(string sourceFilePath, string errorDetails)
    {
        string fileName = Path.GetFileName(sourceFilePath) + ".error";
        string targetPath = Path.Combine(_errorPath, fileName);

        if (File.Exists(targetPath)) File.Delete(targetPath);
        File.WriteAllText(targetPath, errorDetails, System.Text.Encoding.UTF8);
    }

    public void DisposeWatcher()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnSignFileCreated;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
