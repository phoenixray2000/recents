using System.Runtime.InteropServices;
using System.Text;

namespace Recents.App.Utils;

// PRD §6.3.7 / §16.11 路径标准化工具
// 全工程任何路径写入索引前必须经过此工具，禁止散落 ToLower() / TrimEnd('\')。
// 规则：
//   1. 去掉 \\?\ 前缀（长路径前缀，Win32 内部用，不展示给用户）
//   2. 盘符字母转大写（C:\ 而非 c:\）
//   3. 解析 8.3 短名（GetLongPathName）
//   4. 去掉路径末尾的目录分隔符（文件夹路径不含尾随 \，除根目录如 C:\）
//   5. UNC 路径：保留 \\host\share\ 前缀（两级共享名后不去尾随分隔符）
//   6. 最终为 Unicode 规范化路径（NFC）
public static class PathNormalizer
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);

    // 对外唯一入口：传入任意格式路径，返回规范化路径。
    // 若传入 null / 空字符串，原样返回 string.Empty。
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        // 1. 去掉 \\?\ 前缀（含 \\?\UNC\ → \\）
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            path = path[4..];

        bool isUnc = path.StartsWith(@"\\");

        // 2. 解析 8.3 短名 → 长路径（仅本地路径且文件确实存在时有效；失败静默）
        path = TryGetLongPath(path);

        // 3. 盘符大写（仅本地路径，如 c:\foo → C:\foo）
        if (!isUnc && path.Length >= 2 && path[1] == ':')
            path = char.ToUpperInvariant(path[0]) + path[1..];

        // 4. 路径分隔符统一为反斜杠
        path = path.Replace('/', '\\');

        // 5. 去掉尾随反斜杠（根目录 C:\ 例外；UNC 前两级 \\host\share\ 例外）
        path = TrimTrailingSeparator(path, isUnc);

        // 6. Unicode NFC 规范化
        path = path.Normalize(NormalizationForm.FormC);

        return path;
    }

    // 尝试用 GetLongPathName 把 8.3 短名转成完整长名；失败返回原路径
    private static string TryGetLongPath(string path)
    {
        try
        {
            var sb     = new StringBuilder(32767);
            var result = GetLongPathName(path, sb, (uint)sb.Capacity);
            return result > 0 ? sb.ToString() : path;
        }
        catch
        {
            return path;
        }
    }

    // 去掉尾随反斜杠，但保留：
    //   - 本地根目录  C:\
    //   - UNC 共享根  \\host\share\（正好两个反斜杠分段之后的那个分隔符）
    private static string TrimTrailingSeparator(string path, bool isUnc)
    {
        if (!path.EndsWith('\\'))
            return path;

        if (isUnc)
        {
            // \\host\share\ → 保留；\\host\share\sub\ → 去掉
            // 去掉 \\ 前缀后，数反斜杠数量
            var inner = path[2..]; // "host\share\"
            var count = inner.Count(c => c == '\\');
            if (count <= 1)   // 只剩 host\share\ 这一个分隔符
                return path;
        }
        else
        {
            // 本地根 C:\ 保留
            if (path.Length == 3 && path[1] == ':')
                return path;
        }

        return path.TrimEnd('\\');
    }
}
