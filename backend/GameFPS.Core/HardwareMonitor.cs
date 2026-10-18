using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace GameFPS.Core;

/// <summary>
/// 硬件占用监控：CPU 使用率、物理内存、以及按进程的 GPU 使用率与显存占用。
/// 全部基于 Windows 性能计数器与系统 API，不需要安装任何内核驱动，
/// 因此普通(管理员)进程即可运行，无需 PawnIO 之类的传感器驱动。
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private PerformanceCounter? _cpuTotal;
    private PerformanceCounter? _cpuFrequency;      // Actual Frequency (MHz)
    private PerformanceCounter? _cpuPower;          // Energy Meter / RAPL_Package0_PKG / Power (mW)

    /// <summary>每个逻辑处理器的占用率与性能百分比计数器，用于换算大小核。</summary>
    private readonly Dictionary<int, PerformanceCounter> _coreTime = new();
    private readonly Dictionary<int, PerformanceCounter> _corePerf = new();

    private int[] _pLogical = Array.Empty<int>();
    private int[] _eLogical = Array.Empty<int>();
    private double _nominalMHz;                     // P-core 基准频率
    private double _eCoreBaseMHz;                   // E-core 基准频率（按 E:P 经验比换算）

    private readonly NvmlGpu _nvml = new();

    private readonly Dictionary<string, PerformanceCounter> _engineCounters = new();
    private readonly Dictionary<string, PerformanceCounter> _memCounters = new();
    private string[] _engineInstances = Array.Empty<string>();
    private string[] _memInstances = Array.Empty<string>();
    private double _gpuMemoryTotalMB = -1;
    private DateTime _lastScan = DateTime.MinValue;

    /// <summary>ACPI 热区温度计数器（不是 CPU 核心温度，见属性说明）。</summary>
    private PerformanceCounter? _thermalZone;

    public double CpuUsagePercent { get; private set; }
    public double MemoryUsedMB { get; private set; }
    public double MemoryTotalMB { get; private set; }
    public double MemoryUsagePercent { get; private set; }

    /// <summary>CPU 当前实际频率(MHz)。</summary>
    public double CpuFrequencyMHz { get; private set; }

    /// <summary>CPU 封装功耗(W)，来自 Intel RAPL。读不到时为 0 且 HasCpuPower=false。</summary>
    public double CpuPowerW { get; private set; }
    public bool HasCpuPower { get; private set; }

    /// <summary>大核(P-core)平均占用率与频率。</summary>
    public double PCoreUsagePercent { get; private set; }
    public double PCoreFrequencyMHz { get; private set; }

    /// <summary>小核(E-core)平均占用率与频率。</summary>
    public double ECoreUsagePercent { get; private set; }
    public double ECoreFrequencyMHz { get; private set; }

    /// <summary>是否存在大小核混合架构。</summary>
    public bool HasHeterogeneousCores => _eLogical.Length > 0;

    public double GpuFrequencyMHz => _nvml.GraphicsClockMHz;
    public double GpuPowerW => _nvml.PowerW;
    public bool HasGpuSensors => _nvml.IsAvailable;

    /// <summary>GPU 核心温度（NVML，准确）。</summary>
    public double GpuTemperatureC => _nvml.TemperatureC;

    /// <summary>显存温度；消费级卡多数不支持，此时 HasGpuMemoryTemperature=false。</summary>
    public double GpuMemoryTemperatureC => _nvml.MemoryTemperatureC;
    public bool HasGpuMemoryTemperature => _nvml.HasMemoryTemperature;

    /// <summary>
    /// CPU 温度，取自 Windows 的 ACPI 热区（Thermal Zone Information 计数器）。
    ///
    /// 注意：这**不是** CPU 核心温度。核心/Package 温度必须读 MSR
    /// （IA32_PACKAGE_THERM_STATUS 0x1A2、IA32_THERM_STATUS 0x19C），
    /// 普通进程做不到，需要内核传感器驱动。热区温度一般反映 CPU 附近的机箱/主板传感器，
    /// 数值通常比核心温度低且变化滞后，只适合看趋势。
    /// </summary>
    public double CpuTemperatureC { get; private set; }
    public bool HasCpuTemperature { get; private set; }

    public HardwareMonitor()
    {
        try
        {
            // 用 % Processor Time 而不是 % Processor Utility：
            // 后者按处理器"性能利用率"计算，在支持睿频 / 大小核混合架构的 CPU 上
            // 会明显虚高（实测 i5-13500HX 上跑到 108~115%，而真实占用只有 68%）。
            // % Processor Time 是传统的时间占比口径，与任务管理器、WMI 一致。
            _cpuTotal = new PerformanceCounter("Processor Information", "% Processor Time", "_Total", true);
            _cpuTotal.NextValue();     // 预热，首值恒为 0
        }
        catch { _cpuTotal = null; }

        InitFrequencyAndPower();
        InitCoreCounters();
        InitThermalZone();
    }

    /// <summary>
    /// 准备 ACPI 热区温度计数器。先枚举实例，取第一个能读的。
    /// 计数器返回开尔文，需减 273.15 换算成摄氏度。
    /// </summary>
    private void InitThermalZone()
    {
        try
        {
            var cat = new PerformanceCounterCategory("Thermal Zone Information");
            foreach (var inst in cat.GetInstanceNames())
            {
                try
                {
                    var pc = new PerformanceCounter("Thermal Zone Information", "Temperature", inst, true);
                    double k = pc.NextValue();
                    if (k > 200 && k < 400)     // 合理的开尔文范围
                    {
                        _thermalZone = pc;
                        HasCpuTemperature = true;
                        return;
                    }
                    pc.Dispose();
                }
                catch { }
            }
        }
        catch { }

        HasCpuTemperature = false;
    }

    /// <summary>
    /// 准备 CPU 频率与功耗计数器。
    /// 频率用 Actual Frequency（直接给 MHz，不用自己乘百分比）；
    /// 功耗用 Intel RAPL 通过 Energy Meter 暴露的 Package 功率，不需要内核驱动。
    /// </summary>
    private void InitFrequencyAndPower()
    {
        try
        {
            _cpuFrequency = new PerformanceCounter("Processor Information", "Actual Frequency", "_Total", true);
            _cpuFrequency.NextValue();
        }
        catch { _cpuFrequency = null; }

        try
        {
            // 只有 RAPL_Package0_PKG 代表 CPU 封装总功耗；_Total / DRAM / PP0 在多数机器上是 0
            _cpuPower = new PerformanceCounter("Energy Meter", "Power", "RAPL_Package0_PKG", true);
            _cpuPower.NextValue();
            HasCpuPower = true;
        }
        catch { _cpuPower = null; HasCpuPower = false; }
    }

    /// <summary>
    /// 建每核计数器并划分大小核。
    /// 拓扑只在启动时读一次；标称频率取全核最大值作为统一基准——
    /// 部分核心会报出明显偏低的 Processor Frequency（实测 i5-13500HX 上 E-core 报 1527），
    /// 若各用各的会导致小核频率算出 2.1GHz 这种离谱值。
    /// </summary>
    private void InitCoreCounters()
    {
        try
        {
            var cores = CpuTopology.Read();
            var (p, e) = CpuTopology.SplitLogical(cores);
            _pLogical = p;
            _eLogical = e;

            double maxNominal = 0;
            foreach (var lp in _pLogical.Concat(_eLogical))
            {
                string inst = $"0,{lp}";
                try
                {
                    var tc = new PerformanceCounter("Processor Information", "% Processor Time", inst, true);
                    tc.NextValue();
                    _coreTime[lp] = tc;

                    var pc = new PerformanceCounter("Processor Information", "% Processor Performance", inst, true);
                    pc.NextValue();
                    _corePerf[lp] = pc;

                    var fc = new PerformanceCounter("Processor Information", "Processor Frequency", inst, true);
                    maxNominal = Math.Max(maxNominal, fc.NextValue());
                    fc.Dispose();
                }
                catch { }
            }

            _nominalMHz = maxNominal > 0 ? maxNominal : 2500;

            // E-core 基准频率：从 Intel 12 代起 E:P 基频比大致在 0.7 左右
            // （i5-13500HX 是 1.8/2.5）。Windows 没有公开 API 能直接读到，只能按经验比换算。
            _eCoreBaseMHz = _nominalMHz * 0.72;
        }
        catch
        {
            _pLogical = Array.Empty<int>();
            _eLogical = Array.Empty<int>();
        }
    }

    public void Sample()
    {
        try
        {
            if (_cpuTotal != null)
                CpuUsagePercent = Math.Round(Math.Clamp(_cpuTotal.NextValue(), 0, 100), 1);
        }
        catch { }

        try
        {
            if (_cpuFrequency != null)
            {
                double mhz = _cpuFrequency.NextValue();
                // 总频率同样平滑，避免显示值乱跳
                if (mhz > 0) CpuFrequencyMHz = Smooth(CpuFrequencyMHz, mhz);
            }
        }
        catch { }

        try
        {
            if (_cpuPower != null)
            {
                double mw = _cpuPower.NextValue();
                if (mw > 0) CpuPowerW = Math.Round(mw / 1000.0, 1);
            }
        }
        catch { }

        SampleCoreSplit();
        _nvml.Sample();

        try
        {
            if (_thermalZone != null)
            {
                double k = _thermalZone.NextValue();
                if (k > 200 && k < 400)
                    CpuTemperatureC = Math.Round(k - 273.15, 1);
                else
                    HasCpuTemperature = false;   // 读到异常值就关掉这项，别显示乱数
            }
        }
        catch
        {
            HasCpuTemperature = false;
        }

        try
        {
            var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref ms))
            {
                MemoryTotalMB = ms.ullTotalPhys / 1048576.0;
                MemoryUsedMB = (ms.ullTotalPhys - ms.ullAvailPhys) / 1048576.0;
                MemoryUsagePercent = MemoryTotalMB > 0
                    ? Math.Round(MemoryUsedMB / MemoryTotalMB * 100, 1) : 0;
            }
        }
        catch { }
    }

    /// <summary>
    /// 采样大小核的占用率与频率。
    ///
    /// 占用率是准确实测值。频率只能推算，因为 Windows 拿不到单核实际频率：
    ///   · 每核的 Actual Frequency 计数器恒为 0；
    ///   · CallNtPowerInformation 只报基频，不反映睿频；
    ///   · % Processor Performance 对 P-core / E-core 的基准频率不同
    ///     （i5-13500HX 是 P=2.5GHz、E=1.8GHz），统一基准会让小核偏高到 4.5GHz。
    /// 这里对两组分别用各自的基准换算，再做指数移动平均压住采样噪声。
    /// 精度约 ±10%，只适合看趋势，不要当作精确测量。
    /// </summary>
    private void SampleCoreSplit()
    {
        if (_pLogical.Length == 0 && _eLogical.Length == 0) return;

        PCoreUsagePercent = Math.Round(Math.Clamp(AvgOf(_pLogical, _coreTime), 0, 100), 1);
        ECoreUsagePercent = Math.Round(Math.Clamp(AvgOf(_eLogical, _coreTime), 0, 100), 1);

        if (_nominalMHz <= 0) return;

        double pPerf = AvgOf(_pLogical, _corePerf);
        double ePerf = AvgOf(_eLogical, _corePerf);

        if (pPerf > 0)
            PCoreFrequencyMHz = Smooth(PCoreFrequencyMHz, _nominalMHz * pPerf / 100.0);

        if (ePerf > 0 && _eCoreBaseMHz > 0)
            ECoreFrequencyMHz = Smooth(ECoreFrequencyMHz, _eCoreBaseMHz * ePerf / 100.0);
    }

    /// <summary>指数移动平均：新值权重 0.25，约 4 个采样周期收敛。</summary>
    private static double Smooth(double previous, double sample)
    {
        double v = previous <= 0 ? sample : previous * 0.75 + sample * 0.25;
        return Math.Round(v, 0);
    }

    private static double AvgOf(int[] logicals, Dictionary<int, PerformanceCounter> counters)
    {
        if (logicals.Length == 0) return 0;

        double sum = 0;
        int n = 0;
        foreach (int lp in logicals)
        {
            if (!counters.TryGetValue(lp, out var c)) continue;
            try { sum += c.NextValue(); n++; } catch { }
        }
        return n > 0 ? sum / n : 0;
    }

    /// <summary>目标进程的 GPU 使用率(%)，取其各引擎实例的最大值（与任务管理器口径一致）。</summary>
    public double GetGpuUsage(int pid)
    {
        if (pid <= 0) return 0;
        ScanInstances();

        double max = 0;
        string prefix = $"pid_{pid}_";
        foreach (var inst in _engineInstances)
        {
            if (!inst.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var c = GetOrCreate(_engineCounters, "GPU Engine", "Utilization Percentage", inst);
            if (c == null) continue;
            try { max = Math.Max(max, c.NextValue()); } catch { }
        }
        return Math.Round(Math.Clamp(max, 0, 100), 1);
    }

    /// <summary>目标进程的专用显存占用(MB)。</summary>
    public double GetGpuMemoryMB(int pid)
    {
        if (pid <= 0) return 0;
        ScanInstances();

        double max = 0, sum = 0;
        string prefix = $"pid_{pid}_";
        foreach (var inst in _memInstances)
        {
            if (!inst.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var c = GetOrCreate(_memCounters, "GPU Process Memory", "Local Usage", inst);
            if (c == null) continue;
            try
            {
                double v = c.NextValue() / 1048576.0;
                sum += v;
                max = Math.Max(max, v);
            }
            catch { }
        }
        // 同一进程可能有多个实例(不同 LUID)，取和更贴近任务管理器显示
        return Math.Round(sum > 0 ? sum : max, 0);
    }

    /// <summary>
    /// 显存总量(MB)。GPU 性能计数器里没有“显存上限”，
    /// 因此改读显示适配器注册表的 64 位专用显存大小(HardwareInformation.qwMemorySize)，
    /// 多适配器取最大值。硬件不会变，读一次后缓存。
    /// </summary>
    public double GetGpuMemoryTotalMB()
    {
        if (_gpuMemoryTotalMB >= 0) return _gpuMemoryTotalMB;

        double maxBytes = 0;
        try
        {
            using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");

            if (classKey != null)
            {
                foreach (var sub in classKey.GetSubKeyNames())
                {
                    using var key = classKey.OpenSubKey(sub);
                    var raw = key?.GetValue("HardwareInformation.qwMemorySize");
                    if (raw is long l && l > 0) maxBytes = Math.Max(maxBytes, l);
                    else if (raw is int i && i > 0) maxBytes = Math.Max(maxBytes, (long)i);
                }
            }
        }
        catch { /* 读不到就不显示总量 */ }

        _gpuMemoryTotalMB = Math.Round(maxBytes / 1048576.0, 0);
        return _gpuMemoryTotalMB;
    }

    /// <summary>实例列表每 2 秒刷新一次，避免每秒枚举近千个计数器实例。</summary>
    private void ScanInstances()
    {
        if ((DateTime.UtcNow - _lastScan).TotalSeconds < 2) return;
        _lastScan = DateTime.UtcNow;

        try { _engineInstances = new PerformanceCounterCategory("GPU Engine").GetInstanceNames(); }
        catch { _engineInstances = Array.Empty<string>(); }

        try { _memInstances = new PerformanceCounterCategory("GPU Process Memory").GetInstanceNames(); }
        catch { _memInstances = Array.Empty<string>(); }

        // 回收消失的实例
        Prune(_engineCounters, _engineInstances);
        Prune(_memCounters, _memInstances);
    }

    private static void Prune(Dictionary<string, PerformanceCounter> map, string[] alive)
    {
        var set = new HashSet<string>(alive, StringComparer.OrdinalIgnoreCase);
        foreach (var key in map.Keys.Where(k => !set.Contains(k)).ToList())
        {
            try { map[key].Dispose(); } catch { }
            map.Remove(key);
        }
    }

    private static PerformanceCounter? GetOrCreate(
        Dictionary<string, PerformanceCounter> map, string category, string counter, string instance)
    {
        if (map.TryGetValue(instance, out var existing)) return existing;
        try
        {
            var c = new PerformanceCounter(category, counter, instance, true);
            c.NextValue();                       // 预热
            map[instance] = c;
            return c;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        try { _cpuTotal?.Dispose(); } catch { }
        try { _cpuFrequency?.Dispose(); } catch { }
        try { _cpuPower?.Dispose(); } catch { }
        foreach (var c in _coreTime.Values) { try { c.Dispose(); } catch { } }
        foreach (var c in _corePerf.Values) { try { c.Dispose(); } catch { } }
        _coreTime.Clear();
        _corePerf.Clear();
        _nvml.Dispose();
        try { _thermalZone?.Dispose(); } catch { }
        foreach (var c in _engineCounters.Values) { try { c.Dispose(); } catch { } }
        foreach (var c in _memCounters.Values) { try { c.Dispose(); } catch { } }
        _engineCounters.Clear();
        _memCounters.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
