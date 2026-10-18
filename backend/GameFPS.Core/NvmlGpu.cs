using System;
using System.Runtime.InteropServices;

namespace GameFPS.Core;

/// <summary>
/// 通过 NVML 读取 NVIDIA 显卡的实时频率与功耗。
///
/// Windows 的性能计数器只暴露 GPU 利用率和显存，**没有**核心频率和功耗，
/// 所以这两项只能走 NVML。nvml.dll 随显卡驱动一起安装（System32 下就有），
/// 不需要额外部署组件，也不涉及内核驱动。
///
/// 非 NVIDIA 机器上 nvmlInit 会失败，此时 <see cref="IsAvailable"/> 为 false，
/// 上层据此隐藏相关显示项，而不是显示一堆 0。
/// </summary>
public sealed class NvmlGpu : IDisposable
{
    private const int NvmlSuccess = 0;

    // nvmlClockType_t
    private const int ClockGraphics = 0;
    private const int ClockMem = 2;

    // nvmlTemperatureSensors_t
    private const int TempGpu = 0;
    private const int TempMemory = 1;

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int NvmlInit();

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static extern int NvmlShutdown();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")]
    private static extern int NvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern int NvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimit")]
    private static extern int NvmlDeviceGetPowerManagementLimit(IntPtr device, out uint milliwatts);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int NvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);

    private IntPtr _device = IntPtr.Zero;
    private bool _initialized;
    private bool _memoryTempProbed;

    public bool IsAvailable { get; private set; }
    public string DeviceName { get; private set; } = "";

    public double GraphicsClockMHz { get; private set; }
    public double MemoryClockMHz { get; private set; }
    public double PowerW { get; private set; }
    public double PowerLimitW { get; private set; }
    public double TemperatureC { get; private set; }

    /// <summary>显存温度。只有 HBM 卡和部分高端卡支持，多数消费级卡返回 NOT_SUPPORTED。</summary>
    public double MemoryTemperatureC { get; private set; }
    public bool HasMemoryTemperature { get; private set; }

    public NvmlGpu()
    {
        try
        {
            if (NvmlInit() != NvmlSuccess) return;

            _initialized = true;
            if (NvmlDeviceGetHandleByIndex(0, out _device) != NvmlSuccess)
            {
                _device = IntPtr.Zero;
                return;
            }

            IsAvailable = true;
        }
        catch
        {
            // 没有 nvml.dll（AMD / Intel 显卡）时 DLL 加载就会失败，这里安静降级
            _initialized = false;
            IsAvailable = false;
        }
    }

    /// <summary>采样一次。任何一项不支持都只是保持 0，不影响其它项。</summary>
    public void Sample()
    {
        if (!IsAvailable || _device == IntPtr.Zero) return;

        try
        {
            if (NvmlDeviceGetClockInfo(_device, ClockGraphics, out uint g) == NvmlSuccess)
                GraphicsClockMHz = g;

            if (NvmlDeviceGetClockInfo(_device, ClockMem, out uint m) == NvmlSuccess)
                MemoryClockMHz = m;

            if (NvmlDeviceGetPowerUsage(_device, out uint mw) == NvmlSuccess)
                PowerW = Math.Round(mw / 1000.0, 1);

            if (NvmlDeviceGetPowerManagementLimit(_device, out uint lim) == NvmlSuccess)
                PowerLimitW = Math.Round(lim / 1000.0, 1);

            if (NvmlDeviceGetTemperature(_device, TempGpu, out uint t) == NvmlSuccess)
                TemperatureC = t;

            // 显存温度：消费级卡普遍不支持，探测一次后就不再重复尝试
            if (!_memoryTempProbed)
            {
                _memoryTempProbed = true;
                HasMemoryTemperature = NvmlDeviceGetTemperature(_device, TempMemory, out _) == NvmlSuccess;
            }
            if (HasMemoryTemperature &&
                NvmlDeviceGetTemperature(_device, TempMemory, out uint tm) == NvmlSuccess)
                MemoryTemperatureC = tm;
        }
        catch
        {
            // 驱动热重置等瞬时故障，下一 tick 再试
        }
    }

    public void Dispose()
    {
        if (_initialized)
        {
            try { NvmlShutdown(); } catch { }
            _initialized = false;
        }
        IsAvailable = false;
    }
}
