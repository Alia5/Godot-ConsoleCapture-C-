using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

public class ConsoleCapture
{
    public static UInt32 MaxLen { get; set; } = 1024 * 16;
    public static string StdOutBuffer { get; private set; } = "";
    public static string StdErrBuffer { get; private set; } = "";

    private static bool _isInitialized = false;
    public static bool IsInitialized => _isInitialized;
    private static readonly List<ConsoleListener> Listeners = new List<ConsoleListener>();

    public static void AddListener(ConsoleListener listener)
    {
        Listeners.Add(listener);
    }
    public static void RemoveListener(ConsoleListener listener)
    {
        Listeners.Remove(listener);
    }

    public static void Initialize()
    {
        if (!_isInitialized)
        {
            NativeOutputRedirector.RedirectOutput();
            _isInitialized = true;
        }
    }


    public static void AppendStdOut(string str)
    {
        var totalLen = StdOutBuffer.Length + str.Length;
        if (totalLen >= MaxLen)
        {
            StdOutBuffer =
                StdOutBuffer.Remove(0, (int)((totalLen) - MaxLen) + 1)
                + str;
        }
        else
        {
            StdOutBuffer += str;
        }

        foreach (var l in Listeners)
        {
            l.CallThreadSafe("EmitStdOut", str);
        }
    }

    public static void AppendStdErr(string str)
    {
        var totalLen = StdErrBuffer.Length + str.Length;
        if (totalLen >= MaxLen)
        {
            StdErrBuffer =
                StdErrBuffer.Remove(0, (int)((totalLen) - MaxLen) + 1)
                + str;
        }
        else
        {
            StdErrBuffer += str;
        }
        foreach (var l in Listeners)
        {
            l.CallThreadSafe("EmitStdErr", str);
        }
    }

    public class NativeOutputRedirector
    {

#pragma warning disable CA2255
        // [ModuleInitializer]
        // public static void init() {
        //     RedirectOutput();
        // }
#pragma warning restore CA2255

#if GODOT_WINDOWS
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int STD_OUTPUT_HANDLE = -11;
        private const int STD_ERROR_HANDLE = -12;
        private const int DUPLICATE_SAME_ACCESS = 2;

        private static FileStream originalStdout;
        private static FileStream originalStderr;
#else
        [DllImport("libc", SetLastError = true)]
        private static extern int dup2(int oldfd, int newfd);

        [DllImport("libc", SetLastError = true)]
        private static extern int pipe(int[] pipefd);

        private const int STDOUT_FILENO = 1;
        private const int STDERR_FILENO = 2;

        private static FileStream originalStdout;
        private static FileStream originalStderr;
#endif

#if GODOT_WINDOWS
        private static bool _isConsoleShown = false;
        public static bool IsConsoleShown() {
            return _isConsoleShown;
        }

        public static void ToggleConsole() {
            if (!ConsoleCapture._isInitialized) {
                ConsoleCapture.Initialize();
            }
            IntPtr consoleWindow = GetConsoleWindow();
            if (_isConsoleShown) {
                ShowWindow(consoleWindow, SW_HIDE);
                _isConsoleShown = false;
            } else {
                ShowWindow(consoleWindow, SW_SHOW);
                _isConsoleShown = true;
            }
        }
#endif

        public static void RedirectOutput()
        {
#if GODOT_WINDOWS
            // Allocate a console for this app
            if (AllocConsole()) {
                if (!Debugger.IsAttached) {
                    // Hide the console window
                    IntPtr consoleWindow = GetConsoleWindow();
                    ShowWindow(consoleWindow, SW_HIDE);
                    _isConsoleShown = false;
                }
                _isConsoleShown = true;
            }

            // Create pipes for stdout and stderr
            if (!CreatePipe(out IntPtr hReadPipeOut, out IntPtr hWritePipeOut, IntPtr.Zero, 0) ||
                !CreatePipe(out IntPtr hReadPipeErr, out IntPtr hWritePipeErr, IntPtr.Zero, 0)) {
                throw new InvalidOperationException("Failed to create pipes for stdout and stderr.");
            }

            // Duplicate the original stdout and stderr handles
            IntPtr currentProcess = GetCurrentProcess();
            if (!DuplicateHandle(currentProcess, GetStdHandle(STD_OUTPUT_HANDLE), currentProcess, out IntPtr originalStdoutHandle, 0, true, DUPLICATE_SAME_ACCESS) ||
                !DuplicateHandle(currentProcess, GetStdHandle(STD_ERROR_HANDLE), currentProcess, out IntPtr originalStderrHandle, 0, true, DUPLICATE_SAME_ACCESS)) {
                throw new InvalidOperationException("Failed to duplicate stdout or stderr handle.");
            }

            // Save the original stdout and stderr
            originalStdout = new FileStream(new SafeFileHandle(originalStdoutHandle, false), System.IO.FileAccess.Write);
            originalStderr = new FileStream(new SafeFileHandle(originalStderrHandle, false), System.IO.FileAccess.Write);

            // Redirect stdout and stderr to the write ends of the pipes
            if (!SetStdHandle(STD_OUTPUT_HANDLE, hWritePipeOut) || !SetStdHandle(STD_ERROR_HANDLE, hWritePipeErr)) {
                throw new InvalidOperationException("Failed to redirect stdout or stderr.");
            }

            // Create custom streams to capture output
            var customStreamOut = new FileStream(new SafeFileHandle(hReadPipeOut, false), System.IO.FileAccess.Read);
            var customStreamErr = new FileStream(new SafeFileHandle(hReadPipeErr, false), System.IO.FileAccess.Read);

            // Start background tasks to read from the custom streams and write to the original streams and custom function
            Task.Run(() => CaptureOutput(customStreamOut, originalStdout, true));
            Task.Run(() => CaptureOutput(customStreamErr, originalStderr, false));
#else
            // Create pipes for stdout and stderr
            int[] pipeOut = new int[2];
            int[] pipeErr = new int[2];
            if (pipe(pipeOut) == -1 || pipe(pipeErr) == -1)
            {
                throw new InvalidOperationException("Failed to create pipes for stdout and stderr.");
            }

            // Save the original stdout and stderr
            originalStdout = new FileStream(new SafeFileHandle((IntPtr)STDOUT_FILENO, false), System.IO.FileAccess.Write);
            originalStderr = new FileStream(new SafeFileHandle((IntPtr)STDERR_FILENO, false), System.IO.FileAccess.Write);

            // Redirect stdout and stderr to the write ends of the pipes
            if (dup2(pipeOut[1], STDOUT_FILENO) == -1 || dup2(pipeErr[1], STDERR_FILENO) == -1)
            {
                throw new InvalidOperationException("Failed to redirect stdout or stderr.");
            }

            // Create custom streams to capture output
            var customStreamOut = new FileStream(new SafeFileHandle((IntPtr)pipeOut[0], false), System.IO.FileAccess.Read);
            var customStreamErr = new FileStream(new SafeFileHandle((IntPtr)pipeErr[0], false), System.IO.FileAccess.Read);

            // Start background tasks to read from the custom streams and write to the original streams and custom function
            Task.Run(() => CaptureOutput(customStreamOut, originalStdout, true));
            Task.Run(() => CaptureOutput(customStreamErr, originalStderr, false));
#endif
        }

        private static void CaptureOutput(Stream customStream, FileStream originalStream, bool isStdout)
        {
            byte[] buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = customStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                // Write to the original stream
                originalStream.Write(buffer, 0, bytesRead);
                originalStream.Flush();

                // Convert the buffer to a string and write to the custom function
                string output = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                ConsoleHooksRedirector(output, isStdout);
            }
        }

        private static void ConsoleHooksRedirector(string output, bool isStdout)
        {
            if (isStdout)
            {
                ConsoleCapture.AppendStdOut(output);
            }
            else
            {
                ConsoleCapture.AppendStdErr(output);
            }
        }
    }

}
