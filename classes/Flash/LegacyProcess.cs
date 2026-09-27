using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.Flash
{
    /// <summary>
    /// Un Basilisk lancé pour un onglet Legacy, enfermé dans un « job » Windows.
    /// Le processus est créé suspendu et placé dans le job avant de démarrer : tous
    /// ses descendants (relance au démarrage, plugin-container de Flash) y entrent
    /// aussi. Fermer l'onglet ou le navigateur arrête tout le job, et Windows le
    /// détruit de lui-même si le navigateur plante (KILL_ON_JOB_CLOSE).
    /// </summary>
    public sealed class LegacyProcess : IDisposable
    {
        static readonly object LiveLock = new();
        static readonly HashSet<LegacyProcess> Live = new();

        readonly IntPtr _job;
        int _disposed;

        LegacyProcess(IntPtr job, int id)
        {
            _job = job;
            Id = id;
        }

        /// <summary>PID du processus lancé (Basilisk peut ensuite se relancer sous un autre PID).</summary>
        public int Id { get; }

        public bool IsRunning => Volatile.Read(ref _disposed) == 0 && ActiveProcessCount() > 0;

        /// <summary>Fenêtre principale trouvée au lancement : c'est elle qui reçoit la demande de fermeture.</summary>
        public IntPtr MainWindow { get; set; }

        public static LegacyProcess Start(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environmentOverrides = null)
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            IntPtr environment = IntPtr.Zero;
            try
            {
                ConfigureJob(job);

                if (environmentOverrides is { Count: > 0 })
                    environment = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(environmentOverrides));

                var commandLine = new StringBuilder(WindowsCommandLine.Build(executable, arguments));
                var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };

                if (!CreateProcess(
                        executable,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        bInheritHandles: false,
                        CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT,
                        environment,
                        Path.GetDirectoryName(executable),
                        ref startup,
                        out PROCESS_INFORMATION info))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    if (!AssignProcessToJobObject(job, info.hProcess))
                    {
                        int error = Marshal.GetLastWin32Error();
                        TerminateProcess(info.hProcess, 1);
                        throw new Win32Exception(error);
                    }

                    ResumeThread(info.hThread);
                }
                finally
                {
                    CloseHandle(info.hThread);
                    CloseHandle(info.hProcess);
                }

                var process = new LegacyProcess(job, info.dwProcessId);
                lock (LiveLock)
                    Live.Add(process);
                return process;
            }
            catch
            {
                CloseHandle(job);
                throw;
            }
            finally
            {
                if (environment != IntPtr.Zero)
                    Marshal.FreeHGlobal(environment);
            }
        }

        /// <summary>
        /// Fenêtre principale de Basilisk : la plus grande fenêtre de navigateur visible
        /// (classe MozillaWindowClass) appartenant à un processus du job.
        /// </summary>
        public (IntPtr Window, int ProcessId) FindMainWindow(bool allowAnyWindow = false)
        {
            HashSet<int> pids = ProcessIds().ToHashSet();
            if (pids.Count == 0)
                return (IntPtr.Zero, 0);

            IntPtr best = IntPtr.Zero;
            int bestPid = 0;
            long bestArea = 0;

            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out int pid);
                if (!pids.Contains(pid) || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out RECT rect))
                    return true;

                string className = ClassName(hwnd);
                if (className == "#32770" || (!allowAnyWindow && className != "MozillaWindowClass"))
                    return true;

                long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area > bestArea && rect.Right - rect.Left > 100 && rect.Bottom - rect.Top > 80)
                {
                    best = hwnd;
                    bestPid = pid;
                    bestArea = area;
                }
                return true;
            }, IntPtr.Zero);

            return (best, bestPid);
        }

        /// <summary>
        /// Texte d'une boîte de dialogue Windows affichée par un processus du job
        /// (« déjà en cours d'exécution », profil introuvable…), ou null.
        /// </summary>
        public string? FindDialogMessage()
        {
            HashSet<int> pids = ProcessIds().ToHashSet();
            string? message = null;

            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out int pid);
                if (!pids.Contains(pid) || !IsWindowVisible(hwnd) || ClassName(hwnd) != "#32770")
                    return true;

                var parts = new List<string>();
                EnumChildWindows(hwnd, (child, _) =>
                {
                    if (ClassName(child) == "Static" && WindowText(child) is { Length: > 0 } text)
                        parts.Add(text);
                    return true;
                }, IntPtr.Zero);

                message = parts.Count > 0 ? string.Join(" ", parts) : WindowText(hwnd);
                return false;
            }, IntPtr.Zero);

            return string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        }

        /// <summary>
        /// Demande à Basilisk de se fermer (il enregistre cookies et sauvegardes), puis
        /// arrête de force ce qui tourne encore après le délai.
        /// </summary>
        public async Task CloseAsync(TimeSpan grace)
        {
            if (!IsRunning)
            {
                Dispose();
                return;
            }

            RequestClose();

            DateTime deadline = DateTime.UtcNow + grace;
            while (IsRunning && DateTime.UtcNow < deadline)
                await Task.Delay(50).ConfigureAwait(false);

            if (IsRunning)
            {
                // Arrêt forcé, puis attente courte : le profil n'est libéré (parent.lock)
                // qu'une fois les processus réellement terminés.
                if (Volatile.Read(ref _disposed) == 0)
                    TerminateJobObject(_job, 1);
                DateTime exitDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                while (ActiveProcessCount() > 0 && DateTime.UtcNow < exitDeadline)
                    await Task.Delay(25).ConfigureAwait(false);
            }

            Dispose();
        }

        /// <summary>
        /// Fermeture du navigateur : tous les Basilisk en même temps, attente bornée,
        /// puis arrêt forcé du reste.
        /// </summary>
        public static void CloseAll(TimeSpan grace)
        {
            LegacyProcess[] processes;
            lock (LiveLock)
                processes = Live.ToArray();

            if (processes.Length == 0)
                return;

            foreach (LegacyProcess process in processes)
                process.RequestClose();

            DateTime deadline = DateTime.UtcNow + grace;
            while (processes.Any(p => p.IsRunning) && DateTime.UtcNow < deadline)
                Thread.Sleep(50);

            foreach (LegacyProcess process in processes)
                process.Dispose();
        }

        /// <summary>
        /// Onglet en arrière-plan : priorité plus basse et mode économie d'énergie
        /// (EcoQoS) pour que l'onglet affiché reste fluide.
        /// </summary>
        public void SetBackground(bool background)
        {
            foreach (int pid in ProcessIds())
            {
                IntPtr handle = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (handle == IntPtr.Zero)
                    continue;

                try
                {
                    SetPriorityClass(handle, background ? BELOW_NORMAL_PRIORITY_CLASS : NORMAL_PRIORITY_CLASS);

                    var throttling = new PROCESS_POWER_THROTTLING_STATE
                    {
                        Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                        ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                        StateMask = background ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
                    };
                    SetProcessInformation(handle, ProcessPowerThrottling, ref throttling, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }

        /// <summary>Arrête immédiatement tous les processus du job.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (LiveLock)
                Live.Remove(this);

            TerminateJobObject(_job, 1);
            CloseHandle(_job);
        }

        /// <summary>
        /// WM_CLOSE à la fenêtre principale et aux autres fenêtres visibles de Basilisk
        /// (fenêtres ouvertes par la page). La zone de page rendue par l'onglet, masquée,
        /// n'est jamais visée : Basilisk la détruit lui-même avec sa fenêtre.
        /// </summary>
        void RequestClose()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            IntPtr main = MainWindow;
            if (main != IntPtr.Zero)
                PostMessage(main, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            HashSet<int> pids = ProcessIds().ToHashSet();
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out int pid);
                if (hwnd != main && pids.Contains(pid) && IsWindowVisible(hwnd) && ClassName(hwnd) == "MozillaWindowClass")
                    PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }

        int ActiveProcessCount()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return 0;

            return QueryInformationJobObject(_job, JobObjectBasicAccountingInformation,
                    out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info,
                    Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
                ? (int)info.ActiveProcesses
                : 0;
        }

        public IReadOnlyList<int> ProcessIds()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return Array.Empty<int>();

            const int capacity = 128;
            int size = 8 + capacity * IntPtr.Size;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryInformationJobObject(_job, JobObjectBasicProcessIdList, buffer, size, IntPtr.Zero) &&
                    Marshal.GetLastWin32Error() != ERROR_MORE_DATA)
                {
                    return Array.Empty<int>();
                }

                int count = Math.Min(Marshal.ReadInt32(buffer, 4), capacity);
                var ids = new int[count];
                for (int i = 0; i < count; i++)
                    ids[i] = (int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size);
                return ids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        static void ConfigureJob(IntPtr job)
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    // Plus de fenêtre « Basilisk a cessé de fonctionner » : un plantage arrête le processus.
                    // BREAKAWAY_OK : le bac à sable du plugin Flash (mode protégé) peut créer son propre
                    // job ; il reste lié au plugin, lui-même arrêté avec ce job.
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION |
                                 JOB_OBJECT_LIMIT_BREAKAWAY_OK
                }
            };

            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // Une vieille page ou un SWF ne peut ni fermer la session Windows, ni modifier
            // les réglages système. (Les bureaux et l'affichage restent permis : le mode
            // protégé de Flash et le plein écran en ont besoin.)
            var ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS
            {
                UIRestrictionsClass = JOB_OBJECT_UILIMIT_EXITWINDOWS | JOB_OBJECT_UILIMIT_SYSTEMPARAMETERS
            };
            SetInformationJobObject(job, JobObjectBasicUIRestrictions, ref ui, Marshal.SizeOf<JOBOBJECT_BASIC_UI_RESTRICTIONS>());
        }

        /// <summary>Bloc d'environnement Unicode : celui du navigateur, plus les variables données.</summary>
        internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
        {
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                variables[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
            foreach ((string key, string value) in overrides)
                variables[key] = value;

            var block = new StringBuilder();
            foreach ((string key, string value) in variables)
                block.Append(key).Append('=').Append(value).Append('\0');
            return block.Append('\0').ToString();
        }

        static string WindowText(IntPtr hwnd)
        {
            var text = new StringBuilder(512);
            return GetWindowText(hwnd, text, text.Capacity) > 0 ? text.ToString() : string.Empty;
        }

        static string ClassName(IntPtr hwnd)
        {
            var name = new StringBuilder(64);
            return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
        }

        // ------------------------------------------------------------------
        // Win32
        // ------------------------------------------------------------------

        const uint CREATE_SUSPENDED = 0x00000004;
        const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        const uint WM_CLOSE = 0x0010;
        const int ERROR_MORE_DATA = 234;

        const int JobObjectBasicAccountingInformation = 1;
        const int JobObjectBasicProcessIdList = 3;
        const int JobObjectBasicUIRestrictions = 4;
        const int JobObjectExtendedLimitInformation = 9;

        const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
        const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        const uint JOB_OBJECT_UILIMIT_SYSTEMPARAMETERS = 0x00000008;
        const uint JOB_OBJECT_UILIMIT_EXITWINDOWS = 0x00000080;

        const uint PROCESS_SET_INFORMATION = 0x0200;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint NORMAL_PRIORITY_CLASS = 0x00000020;
        const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;

        const int ProcessPowerThrottling = 4;
        const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_UI_RESTRICTIONS
        {
            public uint UIRestrictionsClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_BASIC_UI_RESTRICTIONS info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool QueryInformationJobObject(IntPtr hJob, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int length, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool QueryInformationJobObject(IntPtr hJob, int infoClass, IntPtr info, int length, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateJobObject(IntPtr hJob, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcess(
            string lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetPriorityClass(IntPtr hProcess, uint priorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessInformation(IntPtr hProcess, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    }
}
