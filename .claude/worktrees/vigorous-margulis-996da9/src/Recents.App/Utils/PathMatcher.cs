namespace Recents.App.Utils;

// PRD §6.6 搜索语法 + §6.14/§6.15/§6.16 排除/黑/白名单匹配。
// 搜索：
//   - 含 .（首字符）：扩展名精确匹配
//   - 含 \ /：路径片段匹配
//   - 其他：文件名 + 路径模糊（不区分大小写）
//   - 多 token AND
public static class PathMatcher
{
}
