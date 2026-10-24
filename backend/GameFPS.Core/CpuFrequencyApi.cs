using System;
using System.Runtime.InteropServices;

namespace GameFPS.Core;

/// <summary>
/// 从 CallNtPowerInformation 读每个逻辑处理器的当前频率(MHz)。
///
/// 这是在 Windows 上不装内核驱动就能拿到真实运行频率的唯一可靠途径：
///   · "Processor Information\Actual Frequency" 计数器在相当多机器上恒为 0
///     （尤其是 12 代以后的混合架构，实测 i5-13500HX 直接返回 0）；
///   · "% Processor Performance" 计数器缺失时连百分比推算都做不了；
///   · CallNtPowerInformation 走 ACPI，报的是当前 P-State 频率，能反映睿频，
///     但精度到 100MHz 台阶，适合看趋势不适合当标尺。
///
/// 返回数组按逻辑处理器编号索引，读不到时给空数组（调用方回落到计数器推算）。
/// </summary>
internal static class CpuFrequencyApi
{
    private const int ProcessorInformation = 11;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint CallNtPowerInformation(
        int informationLevel,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize);

    /// <summary>每个逻辑处理器的当前频率(MHz)；失败时返回空数组。</summary>
    public static uint[] ReadCurrentMhz()
    {
        int n = Environment.ProcessorCount;
        if (n <= 0) return Array.Empty<uint>();

        int one = Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>();
        int size = one * n;
        IntPtr buf = Marshal.AllocHGlobal(size);

        try
        {
            // NTSTATUS 0 = STATUS_SUCCESS；其它值说明这级信息不可用
            uint status = CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, buf, (uint)size);
            if (status != 0) return Array.Empty<uint>();

            var result = new uint[n];
            for (int i = 0; i < n; i++)
            {
                IntPtr p = IntPtr.Add(buf, i * one);
                var info = Marshal.PtrToStructure<PROCESSOR_POWER_INFORMATION>(p);

                // 用 Number 当索引更稳：不依赖数组顺序与处理器编号一致
                int idx = (int)info.Number;
                if (idx >= 0 && idx < n) result[idx] = info.CurrentMhz;
            }
            return result;
        }
        catch
        {
            return Array.Empty<uint>();
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
