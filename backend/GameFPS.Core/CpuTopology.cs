using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace GameFPS.Core;

/// <summary>
/// 一个物理核：EfficiencyClass + 它包含的逻辑处理器编号。
/// </summary>
public sealed class CpuCore
{
    /// <summary>0 = 小核(E-core)，1 = 大核(P-core)。单架构 CPU 上通常全是 0。</summary>
    public int EfficiencyClass { get; init; }

    /// <summary>该核包含的逻辑处理器编号（超线程时一个核对应两个）。</summary>
    public int[] LogicalProcessors { get; init; } = Array.Empty<int>();

    public bool IsPerformanceCore => EfficiencyClass > 0;
}

/// <summary>
/// 读取 CPU 的大小核拓扑。
/// Windows 只通过 GetLogicalProcessorInformationEx 暴露 EfficiencyClass，
/// 没有其它公开 API 能区分 P-core / E-core，所以这里直接 P/Invoke。
/// 拓扑在运行期不会变，构造一次后复用即可。
/// </summary>
public static class CpuTopology
{
    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    /// <summary>全部物理核；读取失败时返回空列表。</summary>
    public static List<CpuCore> Read()
    {
        var cores = new List<CpuCore>();

        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            if (len == 0) return cores;

            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return cores;

                int offset = 0;
                while (offset < (int)len)
                {
                    int size = Marshal.ReadInt32(buf, offset + 4);
                    if (size <= 0) break;

                    // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX { Relationship(4); Size(4); union{...} }
                    // PROCESSOR_RELATIONSHIP { Flags(1); EfficiencyClass(1); Reserved[20]; GroupCount(2); GroupMask[] }
                    const int relBase = 8;
                    byte efficiency = Marshal.ReadByte(buf, offset + relBase + 1);
                    int groupCount = Marshal.ReadInt16(buf, offset + relBase + 22);

                    var logical = new List<int>();
                    for (int g = 0; g < groupCount; g++)
                    {
                        // GROUP_AFFINITY { KAFFINITY Mask(8 on x64); WORD Group; WORD Reserved[3] }
                        int gOff = offset + relBase + 24 + g * 16;
                        long mask = Marshal.ReadInt64(buf, gOff);
                        int group = Marshal.ReadInt16(buf, gOff + 8);

                        for (int bit = 0; bit < 64; bit++)
                            if ((mask & (1L << bit)) != 0)
                                logical.Add(group * 64 + bit);
                    }

                    logical.Sort();
                    cores.Add(new CpuCore { EfficiencyClass = efficiency, LogicalProcessors = logical.ToArray() });
                    offset += size;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            cores.Clear();
        }

        return cores;
    }

    /// <summary>
    /// 把所有逻辑处理器按大小核分组。
    /// 返回 (大核逻辑处理器, 小核逻辑处理器)。没有异构核时大核列表为空。
    /// </summary>
    public static (int[] Performance, int[] Efficient) SplitLogical(List<CpuCore> cores)
    {
        var p = cores.Where(c => c.IsPerformanceCore).SelectMany(c => c.LogicalProcessors).Distinct().OrderBy(x => x).ToArray();
        var e = cores.Where(c => !c.IsPerformanceCore).SelectMany(c => c.LogicalProcessors).Distinct().OrderBy(x => x).ToArray();

        // 单架构 CPU：EfficiencyClass 全为 0，此时把全部逻辑处理器当作大核处理，
        // 免得界面上"大核"一栏永远是 0。
        if (p.Length == 0 && e.Length > 0)
            return (e, Array.Empty<int>());

        return (p, e);
    }
}
