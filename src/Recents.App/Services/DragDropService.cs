using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Serilog;

namespace Recents.App.Services;

// PRD §6.8 / §14.4 拖拽 DataObject 构建
// A8a: FileDrop（已完成）
// A8b: CFSTR_SHELLIDLIST（Shell IDList Array），确保 Outlook / 微信 / 飞书等兼容
public class DragDropService
{
    public static System.Windows.DataObject CreateDataObject(string[] paths)
    {
        var dataObject = new System.Windows.DataObject();

        // 1. 基础 FileDrop 格式（兼容 Explorer / 大多数应用）
        dataObject.SetData(System.Windows.DataFormats.FileDrop, paths);

        // 2. Shell IDList Array（A8b）：Outlook 等在某些版本仅接受 CFSTR_SHELLIDLIST
        AddShellIdListArray(dataObject, paths);

        return dataObject;
    }

    // PRD §6.8 构造 CFSTR_SHELLIDLIST（CIDA）结构并注入 DataObject
    // CIDA layout:
    //   [UINT cidl]                    — 文件数
    //   [UINT aoffset[0]]              — 父 PIDL 偏移（从结构头开始）
    //   [UINT aoffset[1..cidl]]        — 各子 PIDL 偏移
    //   [ParentPidl bytes]             — 空父 PIDL（= Desktop 根，2字节 null）
    //   [ItemPidl[0]..ItemPidl[n-1]]   — 各文件绝对 PIDL
    private static void AddShellIdListArray(System.Windows.DataObject dataObject, string[] paths)
    {
        if (paths.Length == 0) return;

        var pidlPtrs  = new IntPtr[paths.Length];
        var pidlSizes = new int[paths.Length];
        int acquired  = 0;

        try
        {
            // 1. 获取各路径的绝对 PIDL
            for (int i = 0; i < paths.Length; i++)
            {
                pidlPtrs[i] = ILCreateFromPathW(paths[i]);
                if (pidlPtrs[i] == IntPtr.Zero)
                {
                    Log.Warning("DragDropService: ILCreateFromPathW 返回空指针 {Path}", paths[i]);
                    return; // 任意一个失败则放弃整个 ShellIDList
                }
                // ILGetSize 返回 PIDL 完整字节数（含末尾2字节 null 终止符）
                pidlSizes[i] = (int)ILGetSize(pidlPtrs[i]);
                acquired++;
            }

            // 2. 计算 CIDA 大小
            // 空父 PIDL = 仅2字节 null（代表 Desktop 绝对根）
            byte[] emptyParentPidl = { 0, 0 };
            int cidl       = paths.Length;
            int headerSize = 4 + (cidl + 1) * 4;  // cidl(4) + aoffset[0..cidl](各4字节)
            int totalSize  = headerSize + emptyParentPidl.Length;
            foreach (var s in pidlSizes) totalSize += s;

            // 3. 填充 CIDA
            using var ms = new MemoryStream(totalSize);
            using var bw = new BinaryWriter(ms);

            bw.Write((uint)cidl);                                // cidl

            uint currentOffset = (uint)headerSize;
            bw.Write(currentOffset);                             // aoffset[0] = 父 PIDL 偏移
            currentOffset += (uint)emptyParentPidl.Length;

            for (int i = 0; i < cidl; i++)
            {
                bw.Write(currentOffset);                         // aoffset[i+1]
                currentOffset += (uint)pidlSizes[i];
            }

            bw.Write(emptyParentPidl);                           // 空父 PIDL

            for (int i = 0; i < cidl; i++)
            {
                var buf = new byte[pidlSizes[i]];
                Marshal.Copy(pidlPtrs[i], buf, 0, pidlSizes[i]);
                bw.Write(buf);                                   // 子 PIDL
            }

            // 4. 注入 DataObject
            var cidaStream = new MemoryStream(ms.ToArray());
            dataObject.SetData("Shell IDList Array", cidaStream);
            Log.Debug("DragDropService: Shell IDList Array 已构建，cidl={Cidl}", cidl);
        }
        catch (Exception ex)
        {
            // 失败不影响 FileDrop，仅 Warning
            Log.Warning(ex, "DragDropService: Shell IDList Array 构建失败，仅保留 FileDrop");
        }
        finally
        {
            // 释放 Shell 分配的 PIDL 内存
            for (int i = 0; i < acquired; i++)
            {
                if (pidlPtrs[i] != IntPtr.Zero)
                    ILFree(pidlPtrs[i]);
            }
        }
    }

    #region Shell P/Invoke

    // 根据路径字符串创建 PIDL；调用方负责调用 ILFree 释放
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr ILCreateFromPathW(string pszPath);

    // 获取 PIDL 的总字节大小（含末尾 null 终止符）
    [DllImport("shell32.dll")]
    private static extern uint ILGetSize(IntPtr pidl);

    // 释放由 Shell 函数分配的 PIDL 内存
    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    #endregion
}
