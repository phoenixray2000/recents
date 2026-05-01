# 实现任务：Recents（Windows Trickster 等效工具）

> 本文件是给 Antigravity / 编码 Agent 的实现指令。复制整段内容粘贴到 Agent 即可开始第一阶段实现。
> 文档定位：**实现指令，不是设计文档**。设计请看 `recent_files_windows_prd.md`。

---

## 项目位置与权威文档

- 项目根：`D:\Git\recents`
- **PRD（唯一真理源）**：`recent_files_windows_prd.md`，开始前**完整通读**
- **环境清单**：`ENVIRONMENT.md`，列出所需 SDK / NuGet 包
- **现有骨架**：`src/Recents.App/` 已按 PRD §9 铺好目录与空类文件，**禁止改动目录结构**或新增顶层文件夹

你不是在重新设计，是按 PRD 把现有空骨架填满。PRD 与现有结构冲突时以 PRD 为准并报告冲突点。

---

## 已就绪

1. `Recents.sln` + `src/Recents.App/Recents.App.csproj`（.NET 8 / WPF / x64）
2. `app.manifest`（PerMonitorV2 + longPathAware + asInvoker）
3. 所有 `Models/` `ViewModels/` `Services/` `Services/Sources/` `Utils/` 下的空类骨架，类名与 PRD §9 一致
4. `App.xaml(.cs)` / `MainWindow.xaml(.cs)` / `SettingsWindow.xaml(.cs)` 仅 WPF 必要的 `InitializeComponent()`

---

## 本次实现范围（严格限定 PRD §17 第一阶段 / §15 P0）

按编号顺序实现。**不允许越界做 P1 / P2**。

1. **NuGet 引入到 csproj**：
   - `Microsoft.Data.Sqlite`、`Securify.ShellLink`、`CommunityToolkit.Mvvm`、`Serilog`、`Serilog.Sinks.File`
   - **不引入** OpenMcdf（Jump List 是 P1）、任何云盘 SDK、HTTP 库、托盘第三方库（用 `System.Windows.Forms.NotifyIcon`）

2. **`SingleInstanceService`**（PRD §13）：命名 Mutex `Global\Recents.SingleInstance`。第二实例通过命名管道或 `WM_COPYDATA` 通知首实例显示窗口后退出。

3. **`Models/AppSettings.cs` + `SettingsService`**（PRD §6.18、§8.3）：序列化到 `%APPDATA%\Recents\settings.json`。损坏时备份 `.bak.<ts>` 并重建默认值。

4. **`HotkeyService`**（PRD §6.1）：P/Invoke `RegisterHotKey` / `UnregisterHotKey`。默认 `Ctrl+Alt+R`，注册失败按以下顺序回退：`Win+;` → `Ctrl+Shift+Space` → `Ctrl+Alt+Space`。全部失败时 Serilog 记 Error + 托盘气泡提示。

5. **`Utils/PathNormalizer.cs`**（PRD §6.3.7）：处理 `\\?\` 前缀、盘符大小写、8.3 短名、尾随分隔符；UNC 保留 `\\host\share\` 前缀。**全工程任何路径写入索引前必须经过此工具**，不允许散落 `ToLower()` / `TrimEnd`。

6. **`Models/RecentItem.cs`**（PRD §8.1）：DTO + `SourceKinds [Flags]` + `ExistsState { Missing=0, Exists=1, Unknown=2 }`。

7. **`RecentIndexService`**（PRD §6.3、§6.18）：
   - SQLite `%LOCALAPPDATA%\Recents\index.db`，schema 严格按 PRD §6.18。
   - 内存 `ObservableCollection<RecentItemViewModel>`，按 RecentTime 倒序。
   - `MergeAsync(RecentItem incoming)`：按 `NormalizedPath` 查找 → `SourceKinds` 位或合并 → `RecentTime = max(...)` → 写 SQLite + 通知 UI。
   - 启动顺序：先从 SQLite 灌入内存供 UI 立即渲染，各 source 的扫描在后台异步推增量。

8. **`Services/Sources/IRecentSource.cs`**（PRD §6.3）：
   ```csharp
   Task InitialScanAsync(CancellationToken ct);
   IObservable<RecentChange> Watch();
   SourceKinds Kind { get; }
   ```

9. **`KnownFolderWatchSource`**（PRD §6.3.2、§6.19）—— **本阶段最关键模块**：
   - **禁止硬编码** `C:\Users\<x>\Downloads`。用 P/Invoke `SHGetKnownFolderPath` 解析 `FOLDERID_Downloads / Desktop / Documents / Pictures / Videos / Music`。
   - 每个文件夹一个 `FileSystemWatcher`：`IncludeSubdirectories=true`、`InternalBufferSize=64*1024`、订阅 `Error` 事件（溢出 → 标 Stale → 触发该源 LastWriteTime 增量重扫）。
   - debounce **1200ms**（兼容 `.crdownload` → `.ext` 重命名 + 杀毒尾抖）。
   - **占位符保护**：在 `FileInfo.Length` / 缩略图调用前 `GetFileAttributes` 检查：
     - `FILE_ATTRIBUTE_RECALL_ON_OPEN` (0x40000)
     - `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` (0x400000)
     - `FILE_ATTRIBUTE_OFFLINE` (0x1000)
     命中任一：跳过 Length 与缩略图，只发元数据。
   - 初次扫描：枚举 `LastWriteTime` 在 `RecentLookbackDays`（默认 30 天）内的文件。

10. **`ShellLinkResolver`**（PRD §6.4）：用 `Securify.ShellLink` 纯托管解析 .lnk。返回 TargetPath / WorkingDir / Args / LnkLastWriteTime。**禁用** `WSH IWshRuntimeLibrary`。可在 ThreadPool 上并发，目标 1000 个 .lnk ≤ 1.5s。

11. **`RecentLnkSource`**（PRD §6.3.4）：扫描 `%APPDATA%\Microsoft\Windows\Recent\*.lnk`，**跳过** `AutomaticDestinations` 与 `CustomDestinations` 子目录。每个 .lnk 用 `ShellLinkResolver` 解析后 emit RecentItem，`SourceKinds.RecentLnk`，`RecentTime = .lnk LastWriteTime`。同目录起 `FileSystemWatcher` 做增量。

12. **主窗口暗色 UI**（PRD §5、§7）：
    - 布局：顶部搜索框、中间虚拟化文件列表（`VirtualizingStackPanel`）、底部状态栏。
    - 颜色严格按 §7.4；字体 Segoe UI。
    - 520×680，最小 420×480，`Topmost=True`，`ShowInTaskbar=False`。
    - 显示时：定位到鼠标当前所在显示器 work area 居中；自动聚焦搜索框。

13. **`MainViewModel`**：
    - 绑定 `RecentIndexService` 集合。
    - `SearchText` 实时内存过滤，语法（PRD §6.6）：首字符 `.` → 扩展名精确；含 `\` `/` → 路径片段；其他 → 文件名 + 路径模糊大小写不敏感；空格分割多 token AND。
    - 键盘（PRD §6.13）：↑/↓ 移选、Enter 打开、Esc 隐藏、`Ctrl+F` 聚焦搜索、`Ctrl+C` 复制完整路径、`Ctrl+Shift+C` 复制文件名、`Ctrl+O` 打开所在位置。

14. **`FileActionService`**（PRD §6.9、§6.10）：
    - 双击：`ProcessStartInfo { UseShellExecute = true, FileName = path }`。
    - 打开所在位置：`explorer.exe /select,"path"`。
    - 复制路径 / 文件名：剪贴板。

15. **`DragDropService`**（PRD §6.8）：
    - `DataObject` **同时**附 `DataFormats.FileDrop`（`string[]`）与 `CFSTR_SHELLIDLIST`（Shell IDList Array）。两者缺一会导致部分目标 App 不识别。
    - 多选拖拽。
    - Missing 文件：游标显示禁止图标，禁止拖拽。

16. **`TrayService`**（PRD §6.2）：用 `System.Windows.Forms.NotifyIcon`（添加 `<UseWindowsForms>true</UseWindowsForms>` 到 csproj）。菜单：显示窗口 / 设置（占位，弹出空 SettingsWindow）/ 重新扫描 / 退出。关闭主窗口 → 仅隐藏到托盘；退出 → 真正结束进程，注销热键，关闭 SQLite。

17. **`App.xaml.cs` 启动流程**：
    1. SingleInstance 检查，已有实例则发信号后退出。
    2. 初始化 Serilog → `%LOCALAPPDATA%\Recents\logs\recents.log`，按天滚动。
    3. 加载 settings。
    4. 打开 SQLite，把缓存条目灌入 `RecentIndexService` 内存集合。
    5. 注册全局热键（含回退链）。
    6. 初始化托盘。
    7. 后台 `Task.Run` 启动 `KnownFolderWatchSource` + `RecentLnkSource`。
    8. 若启动参数含 `--minimized` → 仅托盘；否则 `MainWindow.Show()`。

---

## 硬约束（违反任何一条都视为返工）

- ❌ 不集成云盘 SDK（OneDrive / Google Drive / Dropbox / SharePoint）、不引入 HTTP 库、不读浏览器历史 / 聊天工具数据库
- ❌ 不使用 WSH `IWshRuntimeLibrary`、不使用 Electron / WebView2 作为主 UI
- ❌ UI 线程不允许执行：磁盘扫描、.lnk 解析、注册表读取、`File.Exists` 远程探测、图标加载
- ❌ 不删除原始文件；"删除 Recent 记录" 仅删 `%APPDATA%\Microsoft\Windows\Recent` 下的对应 .lnk
- ❌ 不联网（本阶段不做 UNC，已被排除在 P0 之外）
- ❌ 不新增 README / CHANGELOG / ARCHITECTURE / 设计文档 —— PRD 是唯一真理源
- ✅ 所有路径处理走 `PathNormalizer`
- ✅ 每完成一个编号必须 `dotnet build` 通过 + 手测对应行为
- ✅ 注释中文优先，与 PRD 风格一致；只解释 WHY，不解释 WHAT

---

## 关键验收点（不通过则整个 P0 不算完成）

> **从浏览器下载一个文件到 `%USERPROFILE%\Downloads`，2 秒内该文件出现在主窗口列表顶部。**

这是 PRD §14.2 的核心验收，也是本工具区别于"只读 Recent 文件夹"老方案的根本意义。不通过 = `KnownFolderWatchSource` 没做对（最常见的错因：监听了错误的路径、没有 `IncludeSubdirectories`、debounce 太短让 `.crdownload` 重命名事件丢失、占位符保护误伤普通文件）。

其余必过项：
- `Ctrl+Alt+R` 在窗口隐藏 / 显示间切换，呼出 < 100ms
- 重启应用后列表立即从 SQLite 缓存渲染（不等扫描）
- 双击打开调用系统默认程序
- 拖拽到资源管理器、Edge、微信、飞书、Outlook 五处都能正确传文件
- 搜索 `.docx` 仅匹配扩展名，搜索 `download` 匹配路径，搜索 `report 2026` 多 token AND
- 关闭主窗口 → 隐藏到托盘；托盘 Exit → 进程真正结束（任务管理器查无 `Recents.exe`）

---

## 工作流要求

1. **一次只做一个编号**。做完即 `dotnet build` + 手测，再做下一个。
2. **不允许一次铺完所有代码**（PRD §16 #1）。
3. 每完成 3–5 个编号给一个简短进度报告（哪些已完成、build 状态、是否有偏离 PRD 的取舍）。
4. 完成全部 17 个编号后停下来，输出：
   - build 结果
   - **Downloads 验收点是否通过**（必须实际测试）
   - 已知技术债 / 偏离 PRD 的地方
   - 等待人类确认再进入 P1（设置页 UI、收藏、Jump List、Office MRU、UNC 自定义来源、真实图标缩略图、开机自启）
5. 遇到 PRD 没明确的边界情况，**先停下问**，不要自己拍板。

开始吧。
