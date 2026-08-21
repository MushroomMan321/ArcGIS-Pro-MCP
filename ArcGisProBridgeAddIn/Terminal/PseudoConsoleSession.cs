using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static ArcGisProBridgeAddIn.Terminal.NativeMethods;

namespace ArcGisProBridgeAddIn.Terminal;

internal sealed record PseudoConsoleStartInfo(
    string CommandLine,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string?> EnvironmentOverrides,
    short Columns,
    short Rows);

/// <summary>
/// Runs a console program under a Windows pseudo console (ConPTY) and exposes
/// its VT stream as events. This is what lets the dock pane host a real,
/// fully interactive Claude Code session rather than a scripted subprocess.
/// </summary>
internal sealed class PseudoConsoleSession : IDisposable
{
    private readonly object _writeLock = new();
    private readonly ProcessWaitHandle _processExitEvent;
    private readonly FileStream _output;
    private readonly FileStream _input;
    private readonly Thread _readerThread;

    private IntPtr _pseudoConsole;
    private IntPtr _processHandle;
    private RegisteredWaitHandle? _registeredWait;
    private Task _writeChain = Task.CompletedTask;
    private int _exitCode;
    private int _exitRaised;
    private int _pseudoConsoleClosed;
    private bool _disposed;

    private PseudoConsoleSession(IntPtr pseudoConsole, IntPtr processHandle, FileStream input, FileStream output)
    {
        _pseudoConsole = pseudoConsole;
        _processHandle = processHandle;
        _input = input;
        _output = output;

        // Wrap the process handle so exit can be awaited without polling.
        _processExitEvent = new ProcessWaitHandle(processHandle);
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _processExitEvent,
            (_, _) => OnProcessExited(),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        _readerThread = new Thread(PumpOutput)
        {
            IsBackground = true,
            Name = "Claude pane ConPTY reader"
        };
        _readerThread.Start();
    }

    /// <summary>Raised on a background thread with decoded terminal output.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>
    /// Raised once, after the child has exited and its remaining output has been
    /// drained, so the last thing the process printed is never lost behind the
    /// exit notice.
    /// </summary>
    public event Action<int>? Exited;

    public static PseudoConsoleSession Start(PseudoConsoleStartInfo startInfo)
    {
        var size = new COORD
        {
            X = Math.Max((short)1, startInfo.Columns),
            Y = Math.Max((short)1, startInfo.Rows)
        };

        var attributes = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>()
        };

        if (!CreatePipe(out var inputRead, out var inputWrite, ref attributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the pseudo console input pipe.");
        }

        if (!CreatePipe(out var outputRead, out var outputWrite, ref attributes, 0))
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the pseudo console output pipe.");
        }

        var hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out var pseudoConsole);

        // The pseudo console duplicates both handles into conhost, so these
        // copies are dead weight. They must be released here: while this
        // process still holds the output write side, the reader below would
        // never see end-of-stream when the child exits.
        inputRead.Dispose();
        outputWrite.Dispose();

        if (hr != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            Marshal.ThrowExceptionForHR(hr);
        }

        IntPtr processHandle;
        try
        {
            processHandle = StartProcess(startInfo, pseudoConsole);
        }
        catch
        {
            ClosePseudoConsole(pseudoConsole);
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }

        return new PseudoConsoleSession(
            pseudoConsole,
            processHandle,
            new FileStream(inputWrite, FileAccess.Write, bufferSize: 1, isAsync: false),
            new FileStream(outputRead, FileAccess.Read, bufferSize: 1, isAsync: false));
    }

    private static IntPtr StartProcess(PseudoConsoleStartInfo startInfo, IntPtr pseudoConsole)
    {
        var attributeListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);

        var attributeList = Marshal.AllocHGlobal(attributeListSize);
        var environment = IntPtr.Zero;

        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not initialise the process attribute list.");
            }

            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the process to the pseudo console.");
            }

            var startupInfo = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
                lpAttributeList = attributeList
            };

            environment = CreateEnvironmentBlock(startInfo.EnvironmentOverrides);

            // Handle inheritance stays off: the pseudo console handle travels
            // through the attribute list, so inheriting would only leak
            // unrelated ArcGIS Pro handles into the child.
            if (!CreateProcess(
                    lpApplicationName: null,
                    lpCommandLine: startInfo.CommandLine,
                    lpProcessAttributes: IntPtr.Zero,
                    lpThreadAttributes: IntPtr.Zero,
                    bInheritHandles: false,
                    dwCreationFlags: EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                    lpEnvironment: environment,
                    lpCurrentDirectory: startInfo.WorkingDirectory,
                    lpStartupInfo: ref startupInfo,
                    lpProcessInformation: out var processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the terminal process.");
            }

            CloseHandle(processInformation.hThread);
            return processInformation.hProcess;
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environment);
            }

            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    /// <summary>
    /// Builds a Unicode environment block from the current process environment
    /// plus the supplied overrides. A null override value removes the variable.
    /// </summary>
    private static IntPtr CreateEnvironmentBlock(IReadOnlyDictionary<string, string?> overrides)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                merged[key] = value;
            }
        }

        foreach (var (key, value) in overrides)
        {
            if (value is null)
            {
                merged.Remove(key);
            }
            else
            {
                merged[key] = value;
            }
        }

        var builder = new StringBuilder();
        foreach (var key in merged.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(key).Append('=').Append(merged[key]).Append('\0');
        }

        builder.Append('\0');
        return Marshal.StringToHGlobalUni(builder.ToString());
    }

    public void Write(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Write(Encoding.UTF8.GetBytes(text));
        }
    }

    /// <summary>
    /// Queues a write without blocking the caller. Keystrokes arrive on the UI
    /// thread, and a child that has stopped reading its input would otherwise
    /// stall ArcGIS Pro once the pipe buffer filled. Chaining the writes keeps
    /// them ordered.
    /// </summary>
    public void Write(byte[] data)
    {
        if (data.Length == 0 || _disposed)
        {
            return;
        }

        lock (_writeLock)
        {
            _writeChain = _writeChain.ContinueWith(
                _ => WriteCore(data),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private void WriteCore(byte[] data)
    {
        try
        {
            _input.Write(data, 0, data.Length);
            _input.Flush();
        }
        catch (Exception)
        {
            // The child has gone; its exit is reported through Exited.
        }
    }

    public void Resize(short columns, short rows)
    {
        if (_disposed || _pseudoConsole == IntPtr.Zero || columns < 1 || rows < 1)
        {
            return;
        }

        ResizePseudoConsole(_pseudoConsole, new COORD { X = columns, Y = rows });
    }

    private void PumpOutput()
    {
        var buffer = new byte[8192];

        // A single read can end mid-sequence for a multi-byte character, so the
        // decoder is kept across reads rather than decoding each block alone.
        var decoder = Encoding.UTF8.GetDecoder();
        var characters = new char[buffer.Length];

        try
        {
            while (true)
            {
                var read = _output.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                var count = decoder.GetChars(buffer, 0, read, characters, 0);
                if (count > 0)
                {
                    OutputReceived?.Invoke(new string(characters, 0, count));
                }
            }
        }
        catch (Exception)
        {
            // Expected when the pseudo console is torn down under us.
        }

        RaiseExited();
    }

    private void OnProcessExited()
    {
        if (GetExitCodeProcess(_processHandle, out var code))
        {
            _exitCode = code;
        }

        // Closing the pseudo console flushes the last of the child's output and
        // then ends the stream, which is what lets the reader finish and report
        // the exit in order.
        CloseConsoleOnce();
    }

    private void CloseConsoleOnce()
    {
        if (Interlocked.Exchange(ref _pseudoConsoleClosed, 1) == 0 && _pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }
    }

    private void RaiseExited()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _exitRaised, 1) == 0)
        {
            Exited?.Invoke(_exitCode);
        }
    }

    /// <summary>
    /// A wait handle over a process handle this class does not own.
    /// Assigning SafeWaitHandle on a ManualResetEvent would work too, but it
    /// leaks the event object the constructor already created.
    /// </summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr processHandle)
        {
            SafeWaitHandle = new SafeWaitHandle(processHandle, ownsHandle: false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _registeredWait?.Unregister(null);
        _registeredWait = null;

        // Close the console first: that is the polite shutdown, and most
        // console programs exit on their own once it goes away.
        CloseConsoleOnce();

        if (_processHandle != IntPtr.Zero)
        {
            if (!_processExitEvent.WaitOne(TimeSpan.FromSeconds(2)))
            {
                TerminateProcess(_processHandle, 0);
            }
        }

        try
        {
            _readerThread.Join(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // Nothing useful to do while ArcGIS Pro is shutting down.
        }

        _processExitEvent.Dispose();

        if (_processHandle != IntPtr.Zero)
        {
            CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }

        _input.Dispose();
        _output.Dispose();
    }
}
