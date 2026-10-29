using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace LocalLlm.Gui.Services;

// ConPTY transports terminal input and VT output as UTF-8, including Ctrl+C and cursor keys.
internal sealed class PowerShellConsole : IDisposable
{
    private readonly object gate = new();
    private readonly Channel<Input> inputQueue = Channel.CreateBounded<Input>(64);
    private readonly Func<byte[], Task> output;
    private readonly FileStream input;
    private readonly FileStream reader;
    private readonly SafeFileHandle process;
    private readonly SafeFileHandle job;
    private readonly Task exitTask;
    private IntPtr pseudoConsole;
    private bool disposed;
    public event Action<uint>? Exited;
    public string Directory { get; }
    private sealed record Input(byte[]? Data, short Columns = 0, short Rows = 0);

    public PowerShellConsole(string directory, int columns, int rows, Func<byte[], Task> output)
    {
        Directory = Path.GetFullPath(directory);
        this.output = output;
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        SafeFileHandle? child = null, childJob = null;
        IntPtr attributes = IntPtr.Zero;
        bool attributesInitialized = false;
        try {
            Check(CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0));
            Check(CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0));
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(Size(columns, rows), inputRead, outputWrite, 0, out pseudoConsole));
            nuint bytes = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(checked((int)bytes));
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes));
            attributesInitialized = true;
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016, pseudoConsole, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
            // Null standard handles with STARTF_USESTDHANDLES prevent redirected host I/O
            // from being copied into PowerShell instead of connecting it to ConPTY.
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100 }, AttributeList = attributes };
            childJob = CreateJobObject(IntPtr.Zero, null);
            if (childJob.IsInvalid) throw new Win32Exception();
            var limits = new JobLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            Check(SetInformationJobObject(childJob, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()));
            // Suspend until our kill-on-close job owns the entire child tree.
            Check(CreateProcess(ProcessRunner.PowerShell, new StringBuilder($"\"{ProcessRunner.PowerShell}\" -NoLogo -NoProfile"),
                IntPtr.Zero, IntPtr.Zero, false, 0x00080004, IntPtr.Zero, Directory, ref startup, out var created));
            child = new SafeFileHandle(created.Process, true);
            using (var thread = new SafeFileHandle(created.Thread, true)) {
                Check(AssignProcessToJobObject(childJob, child));
                if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
            }
            input = new FileStream(inputWrite, FileAccess.Write, 4096, false);
            reader = new FileStream(outputRead, FileAccess.Read, 4096, false);
            process = child; job = childJob;
            // All ConPTY pipes are synchronous: read, write, and shutdown run independently.
            _ = Task.Run(ReadAsync);
            _ = Task.Run(WriteAsync);
            exitTask = Task.Run(ObserveExit);
        }
        catch {
            if (child is { IsInvalid: false }) TerminateProcess(child, 1);
            child?.Dispose(); childJob?.Dispose();
            inputWrite?.Dispose(); outputRead?.Dispose();
            if (pseudoConsole != IntPtr.Zero) ClosePseudoConsole(pseudoConsole);
            throw;
        }
        finally {
            inputRead?.Dispose(); outputWrite?.Dispose();
            if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
        }
    }

    public ValueTask SendAsync(string data) => inputQueue.Writer.WriteAsync(new Input(Encoding.UTF8.GetBytes(data)));
    public void Resize(int columns, int rows) => inputQueue.Writer.TryWrite(new Input(null, Size(columns, rows).X, Size(columns, rows).Y));
    private static Coord Size(int columns, int rows) => new() { X = (short)Math.Clamp(columns, 2, 500), Y = (short)Math.Clamp(rows, 1, 200) };

    private async Task ReadAsync()
    {
        var bytes = new byte[16384];
        try {
            int count;
            while ((count = reader.Read(bytes, 0, bytes.Length)) > 0) {
                bool deliver; lock (gate) deliver = !disposed;
                // The frontend acknowledges writes, bounding memory during large output.
                if (deliver) {
                    try { await output(bytes.AsSpan(0, count).ToArray()); }
                    // A closed tab cancels frontend acknowledgements. Keep draining the
                    // pipe until ConPTY finishes shutting down, including its final frame.
                    catch (OperationCanceledException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private async Task WriteAsync()
    {
        try {
            await foreach (var item in inputQueue.Reader.ReadAllAsync()) {
                if (item.Data is { } data) { input.Write(data); input.Flush(); }
                else lock (gate) {
                    if (!disposed) Marshal.ThrowExceptionForHR(ResizePseudoConsole(pseudoConsole, new Coord { X = item.Columns, Y = item.Rows }));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or COMException) { }
    }

    private void ObserveExit()
    {
        WaitForSingleObject(process, uint.MaxValue);
        if (GetExitCodeProcess(process, out var code)) Exited?.Invoke(code);
    }

    public void Dispose()
    {
        lock (gate) {
            if (disposed) return;
            disposed = true;
            inputQueue.Writer.TryComplete();
        }
        // ClosePseudoConsole may block while emitting its final frame; keep draining output.
        _ = Task.Run(async () => {
            job.Dispose();
            ClosePseudoConsole(pseudoConsole);
            pseudoConsole = IntPtr.Zero;
            input.Dispose(); reader.Dispose();
            await exitTask;
            process.Dispose();
        });
    }

    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize; public IntPtr ReservedData, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits {
        public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobLimits info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
}
