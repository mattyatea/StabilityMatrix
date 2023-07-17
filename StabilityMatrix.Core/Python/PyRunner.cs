﻿using System.Diagnostics.CodeAnalysis;
using NLog;
using Python.Runtime;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Processes;
using StabilityMatrix.Core.Python.Interop;

namespace StabilityMatrix.Core.Python;

[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global")]
public record struct PyVersionInfo(int Major, int Minor, int Micro, string ReleaseLevel, int Serial);

[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
public class PyRunner : IPyRunner
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    
    // Set by ISettingsManager.TryFindLibrary()
    public static DirectoryPath HomeDir { get; set; } = string.Empty;
    
    // This is same for all platforms
    public const string PythonDirName = "Python310";
    
    public static string PythonDir => Path.Combine(GlobalConfig.LibraryDir, "Assets", PythonDirName);
    public static string PythonDllPath { get; }
    public static string PythonExePath { get; }
    public static string PipExePath { get; }
    
    public static string GetPipPath => Path.Combine(PythonDir, "get-pip.pyc");
    // public static string PipExePath => Path.Combine(PythonDir, "Scripts", "pip" + Compat.ExeExtension);
    public static string VenvPath => Path.Combine(PythonDir, "Scripts", "virtualenv" + Compat.ExeExtension);

    public static bool PipInstalled => File.Exists(PipExePath);
    public static bool VenvInstalled => File.Exists(VenvPath);

    private static readonly SemaphoreSlim PyRunning = new(1, 1);

    public PyIOStream? StdOutStream;
    public PyIOStream? StdErrStream;

    // Initialize paths based on platform
    static PyRunner()
    {
        if (Compat.IsWindows)
        {
            PythonDllPath = Path.Combine(PythonDir, "python310.dll");
            PythonExePath = Path.Combine(PythonDir, "python.exe");
            PipExePath = Path.Combine(PythonDir, "Scripts", "pip.exe");
        }
        else if (Compat.IsLinux)
        {
            PythonDllPath = Path.Combine(PythonDir, "lib", "libpython3.10.so");
            PythonExePath = Path.Combine(PythonDir, "bin", "python3.10");
            PipExePath = Path.Combine(PythonDir, "bin", "pip3.10");
        }
        else
        {
            throw new PlatformNotSupportedException();
        }
    }
    
    /// <summary>$
    /// Initializes the Python runtime using the embedded dll.
    /// Can be called with no effect after initialization.
    /// </summary>
    /// <exception cref="FileNotFoundException">Thrown if Python DLL not found.</exception>
    public async Task Initialize()
    {
        if (PythonEngine.IsInitialized) return;

        // On Windows, PythonHome is the root path, on Unix, it's the bin path
        var pythonHome = Compat.IsWindows ? PythonDir : Path.Combine(PythonDir, "bin");
        
        Logger.Info("Setting PYTHONHOME and PATH to {PythonHome}", pythonHome);
        Environment.SetEnvironmentVariable("PYTHONHOME", pythonHome, EnvironmentVariableTarget.Process);
        
        // Get existing PATH
        var currentEnvPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);
        // Append Python path to PATH
        Environment.SetEnvironmentVariable("PATH", $"{pythonHome};{currentEnvPath}", EnvironmentVariableTarget.Process);

        Logger.Info("Initializing Python runtime with DLL: {DllPath}", PythonDllPath);
        // Check PythonDLL exists
        if (!File.Exists(PythonDllPath))
        {
            Logger.Error("Python linked library not found");
            throw new FileNotFoundException("Python linked library not found", PythonDllPath);
        }
        
        Runtime.PythonDLL = PythonDllPath;
        PythonEngine.PythonHome = pythonHome;
        PythonEngine.Initialize();
        PythonEngine.BeginAllowThreads();

        // Redirect stdout and stderr
        StdOutStream = new PyIOStream();
        StdErrStream = new PyIOStream();
        await RunInThreadWithLock(() =>
        {
            dynamic sys = Py.Import("sys");
            sys.stdout = StdOutStream;
            sys.stderr = StdErrStream;
        });
    }

    /// <summary>
    /// One-time setup for get-pip
    /// </summary>
    public async Task SetupPip()
    {
        if (!File.Exists(GetPipPath))
        {
            throw new FileNotFoundException("get-pip not found", GetPipPath);
        }
        var p = ProcessRunner.StartProcess(PythonExePath, "-m get-pip");
        await ProcessRunner.WaitForExitConditionAsync(p);
    }

    /// <summary>
    /// Install a Python package with pip
    /// </summary>
    public async Task InstallPackage(string package)
    {
        if (!File.Exists(PipExePath))
        {
            throw new FileNotFoundException("pip not found", PipExePath);
        }
        var p = ProcessRunner.StartProcess(PipExePath, $"install {package}");
        await ProcessRunner.WaitForExitConditionAsync(p);
    }

    /// <summary>
    /// Run a Function with PyRunning lock as a Task with GIL.
    /// </summary>
    /// <param name="func">Function to run.</param>
    /// <param name="waitTimeout">Time limit for waiting on PyRunning lock.</param>
    /// <param name="cancelToken">Cancellation token.</param>
    /// <exception cref="OperationCanceledException">cancelToken was canceled, or waitTimeout expired.</exception>
    public async Task<T> RunInThreadWithLock<T>(Func<T> func, TimeSpan? waitTimeout = null, CancellationToken cancelToken = default)
    {
        // Wait to acquire PyRunning lock
        await PyRunning.WaitAsync(cancelToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using (Py.GIL())
                {
                    return func();
                }
            }, cancelToken);
        }
        finally
        {
            PyRunning.Release();
        }
    }

    /// <summary>
    /// Run an Action with PyRunning lock as a Task with GIL.
    /// </summary>
    /// <param name="action">Action to run.</param>
    /// <param name="waitTimeout">Time limit for waiting on PyRunning lock.</param>
    /// <param name="cancelToken">Cancellation token.</param>
    /// <exception cref="OperationCanceledException">cancelToken was canceled, or waitTimeout expired.</exception>
    public async Task RunInThreadWithLock(Action action, TimeSpan? waitTimeout = null, CancellationToken cancelToken = default)
    {
        // Wait to acquire PyRunning lock
        await PyRunning.WaitAsync(cancelToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using (Py.GIL())
                {
                    action();
                }
            }, cancelToken);
        }
        finally
        {
            PyRunning.Release();
        }
    }

    /// <summary>
    /// Evaluate Python expression and return its value as a string
    /// </summary>
    /// <param name="expression"></param>
    public async Task<string> Eval(string expression)
    {
        return await Eval<string>(expression);
    }

    /// <summary>
    /// Evaluate Python expression and return its value
    /// </summary>
    /// <param name="expression"></param>
    public Task<T> Eval<T>(string expression)
    {
        return RunInThreadWithLock(() =>
        {
            using var scope = Py.CreateScope();
            var result = scope.Eval(expression);

            // For string, cast with __str__()
            if (typeof(T) == typeof(string))
            {
                return result.GetAttr("__str__").Invoke().As<T>();
            }
            return result.As<T>();
        });
    }

    /// <summary>
    /// Execute Python code without returning a value
    /// </summary>
    /// <param name="code"></param>
    public Task Exec(string code)
    {
        return RunInThreadWithLock(() =>
        {
            using var scope = Py.CreateScope();
            scope.Exec(code);
        });
    }

    /// <summary>
    /// Return the Python version as a PyVersionInfo struct
    /// </summary>
    public async Task<PyVersionInfo> GetVersionInfo()
    {
        var version = await RunInThreadWithLock(() =>
        {
            dynamic info = PythonEngine.Eval("tuple(__import__('sys').version_info)");
            return new PyVersionInfo(
                info[0].As<int>(),
                info[1].As<int>(),
                info[2].As<int>(),
                info[3].As<string>(),
                info[4].As<int>()
            );
        });
        return version;
    }
}
