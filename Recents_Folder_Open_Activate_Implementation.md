# Recents 文件夹打开/激活功能实施方案

## 1. 目标

为 Recents 增加“打开或激活文件夹”能力。

当用户对文件夹执行以下操作时，统一走同一套逻辑：

- 双击文件夹条目
- 右键菜单选择“打开”

目标行为：

- 如果对应文件夹已经在 Windows Explorer 中打开，则切换到该 Explorer 窗口前台。
- 如果对应文件夹未打开，则用 Explorer 打开一个新窗口。
- 如果文件夹不存在，则显示错误提示，不影响主程序运行。

该功能只影响文件夹条目，不改变普通文件的打开、拖拽、预览、复制路径等逻辑。

---

## 2. 产品规则

### 2.1 触发入口

以下入口必须共用同一个服务方法：

```text
双击文件夹条目
右键文件夹 → 打开
```

不要分别实现两套逻辑。

### 2.2 文件夹行为

| 场景 | 行为 |
|---|---|
| 文件夹已在 Explorer 中打开 | 激活已有 Explorer 窗口 |
| 已打开窗口处于最小化 | 恢复窗口并置前 |
| 文件夹未打开 | 执行 `explorer.exe "folderPath"` |
| 文件夹不存在 | 显示错误提示 |
| Explorer 枚举失败 | fallback 到打开新窗口 |
| Windows 11 Explorer 标签页无法识别 | 允许打开新窗口 |

### 2.3 普通文件行为

普通文件保持原逻辑：

```text
双击文件 → 使用默认应用打开原文件
右键文件 → 打开 → 使用默认应用打开原文件
```

### 2.4 路径匹配规则

路径比较前必须标准化：

- 展开完整路径。
- 去掉末尾反斜杠。
- 忽略大小写。
- 将 `file:///` URL 转为本地路径。
- 枚举 Explorer 某个窗口失败时跳过该窗口。

---

## 3. 推荐架构

新增服务：

```text
Services/FolderActivationService.cs
```

新增接口：

```csharp
public interface IFolderActivationService
{
    bool OpenOrActivateFolder(string folderPath);
}
```

职责：

- 校验文件夹是否存在。
- 枚举已打开 Explorer 窗口。
- 判断是否已有同路径窗口。
- 激活已有窗口。
- 未匹配时打开新窗口。
- 处理异常并写日志。

---

## 4. 核心流程

```text
OpenItem(item)
→ item.IsFolder ?
    → FolderActivationService.OpenOrActivateFolder(item.TargetPath)
        → Directory.Exists(folderPath) ?
            → 否：返回 false
            → 是：枚举 Explorer 窗口
                → 匹配到同路径：Restore + SetForegroundWindow
                → 未匹配：explorer.exe "folderPath"
→ item.IsFolder == false
    → FileActionService.OpenFile(item.TargetPath)
```

---

## 5. 接入点

### 5.1 双击文件夹

找到当前文件列表双击处理逻辑，将文件夹分支改为：

```csharp
public void OpenSelectedItem()
{
    var item = SelectedItem;
    if (item == null)
        return;

    OpenRecentItem(item);
}
```

### 5.2 右键菜单“打开”

右键菜单“打开”也调用同一个方法：

```csharp
public void OpenContextMenuItem(RecentItem item)
{
    OpenRecentItem(item);
}
```

### 5.3 统一打开入口

新增或调整统一入口：

```csharp
private void OpenRecentItem(RecentItem item)
{
    if (item.IsFolder)
    {
        var ok = _folderActivationService.OpenOrActivateFolder(item.TargetPath);

        if (!ok)
        {
            ShowError("Folder not found or cannot be opened.");
        }

        return;
    }

    _fileActionService.OpenFile(item.TargetPath);
}
```

要求：

- 双击和右键“打开”都必须走 `OpenRecentItem`。
- 不要在右键菜单中直接调用 `Process.Start`。
- 不要在双击事件中复制一套文件夹激活逻辑。

---

## 6. 参考实现

### 6.1 FolderActivationService

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Recents.App.Services;

public interface IFolderActivationService
{
    bool OpenOrActivateFolder(string folderPath);
}

public sealed class FolderActivationService : IFolderActivationService
{
    public bool OpenOrActivateFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return false;

        if (!Directory.Exists(folderPath))
            return false;

        var target = NormalizeFolderPath(folderPath);

        try
        {
            var shellAppType = Type.GetTypeFromProgID("Shell.Application");
            if (shellAppType != null)
            {
                dynamic? shell = Activator.CreateInstance(shellAppType);
                dynamic windows = shell.Windows();

                foreach (var window in windows)
                {
                    try
                    {
                        string? locationUrl = window.LocationURL;
                        if (string.IsNullOrWhiteSpace(locationUrl))
                            continue;

                        if (!TryConvertLocationUrlToPath(locationUrl, out var currentPath))
                            continue;

                        var current = NormalizeFolderPath(currentPath);

                        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                        {
                            var hwnd = new IntPtr(Convert.ToInt64(window.HWND));
                            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                            NativeMethods.SetForegroundWindow(hwnd);
                            return true;
                        }
                    }
                    catch
                    {
                        // 忽略单个 Explorer 窗口读取失败
                    }
                }
            }
        }
        catch
        {
            // ShellWindows 枚举失败时 fallback 到打开新窗口
        }

        return OpenFolderInExplorer(folderPath);
    }

    private static bool OpenFolderInExplorer(string folderPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeFolderPath(string path)
    {
        var full = Path.GetFullPath(path);

        return full
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryConvertLocationUrlToPath(string locationUrl, out string path)
    {
        path = string.Empty;

        try
        {
            if (Uri.TryCreate(locationUrl, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                path = uri.LocalPath;
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}

internal static class NativeMethods
{
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
```

### 6.2 HWND 处理注意

`window.HWND` 通过 dynamic 返回时类型可能不稳定。

推荐使用：

```csharp
var hwnd = new IntPtr(Convert.ToInt64(window.HWND));
```

不要使用固定的强转：

```csharp
var hwnd = new IntPtr((int)window.HWND);
```

---

## 7. 日志建议

建议记录以下事件：

```text
FolderActivation: target folder path
FolderActivation: matched existing Explorer window
FolderActivation: opened new Explorer window
FolderActivation: folder not found
FolderActivation: failed to enumerate Explorer windows
FolderActivation: failed to activate window
```

不要记录文件内容。

---

## 8. UI 文案

文件夹不存在：

```text
Folder not found or cannot be opened.
```

中文界面：

```text
文件夹不存在或无法打开。
```

右键菜单保持：

```text
Open
Open Location
Copy Path
Pin / Unpin
```

其中：

- 文件夹的 `Open` = 打开或激活文件夹。
- 文件的 `Open` = 使用默认应用打开文件。
- `Open Location` 对文件夹可打开其父目录并选中该文件夹；如暂未实现，保持现有行为。

---

## 9. 验收标准

### 9.1 双击行为

- [ ] 双击普通文件，仍用默认应用打开。
- [ ] 双击文件夹，如果该文件夹未打开，则打开新的 Explorer 窗口。
- [ ] 双击文件夹，如果该文件夹已打开，则切换到已有 Explorer 窗口。
- [ ] 已打开 Explorer 最小化时，双击后恢复并置前。
- [ ] 文件夹不存在时显示错误提示，程序不崩溃。

### 9.2 右键菜单行为

- [ ] 右键普通文件 → 打开，仍用默认应用打开。
- [ ] 右键文件夹 → 打开，如果该文件夹未打开，则打开新的 Explorer 窗口。
- [ ] 右键文件夹 → 打开，如果该文件夹已打开，则切换到已有 Explorer 窗口。
- [ ] 右键文件夹 → 打开，与双击文件夹行为完全一致。

### 9.3 路径匹配

- [ ] 路径大小写不同但实际相同，应识别为同一文件夹。
- [ ] 路径末尾有无反斜杠，应识别为同一文件夹。
- [ ] Explorer 的 `file:///` URL 能正确转换为本地路径。
- [ ] 枚举某个 Explorer 窗口失败时，不影响其他窗口匹配。

### 9.4 回归验证

- [ ] 不影响文件拖拽。
- [ ] 不影响文件右键“复制路径”。
- [ ] 不影响搜索过滤。
- [ ] 不影响 Space 预览。
- [ ] 不影响失焦自动隐藏。
- [ ] Windows 11 Explorer 标签页识别不到时，允许打开新窗口。

---

## 10. 给 Antigravity 的实施 Prompt

```text
请为 Recents 增加“打开或激活文件夹”功能。

必须遵守：

1. 对文件夹条目，双击和右键菜单“打开”都必须执行同一套逻辑：
   - 如果目标文件夹已经在 Explorer 打开，则激活已有 Explorer 窗口；
   - 如果未打开，则打开新的 Explorer 窗口；
   - 如果文件夹不存在，则显示错误提示，不崩溃。

2. 对普通文件，双击和右键菜单“打开”保持现有逻辑：
   - 使用系统默认应用打开原文件。

3. 新增服务：
   - Services/FolderActivationService.cs
   - 接口：IFolderActivationService.OpenOrActivateFolder(string folderPath)

4. 文件夹激活逻辑：
   - 使用 Shell.Application / ShellWindows 枚举 Explorer 窗口；
   - 读取 LocationURL；
   - 转换 file:/// URL 为本地路径；
   - 标准化路径；
   - 忽略大小写比较；
   - 匹配成功后 ShowWindow(hwnd, SW_RESTORE) + SetForegroundWindow(hwnd)；
   - 未匹配时执行 explorer.exe "folderPath"。

5. 双击文件夹和右键“打开”不要分别写两套逻辑。
   请新增或复用统一方法 OpenRecentItem(RecentItem item)。

6. 路径标准化要求：
   - Path.GetFullPath；
   - 去掉末尾 DirectorySeparatorChar / AltDirectorySeparatorChar；
   - 忽略大小写。

7. 异常处理：
   - 单个 Explorer 窗口读取失败时跳过；
   - ShellWindows 枚举失败时 fallback 到打开新窗口；
   - Process.Start 失败时返回 false 并提示用户。

8. 不要影响：
   - 普通文件打开；
   - 文件拖拽；
   - 右键复制路径；
   - 搜索过滤；
   - Space 预览；
   - 失焦自动隐藏。

验收：
- 双击文件夹和右键文件夹“打开”的行为完全一致。
- 已打开文件夹会被切换到前台。
- 未打开文件夹会新开 Explorer。
- 文件夹不存在时提示错误。
- 普通文件行为无回归。
```
