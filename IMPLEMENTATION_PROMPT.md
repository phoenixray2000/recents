# 实现任务：Recents（Windows Trickster 等效工具）

> 本文件是给 Antigravity / 编码 Agent 的实现指令。复制整段内容粘贴到 Agent 即可。
> 文档定位：**实现指令**。设计请看 `recent_files_windows_prd.md`（已升级到 UI 高保真版）。

---

## 项目位置与权威文档

- 项目根：`D:\Git\recents`
- **PRD（唯一真理源）**：`recent_files_windows_prd.md`，开始前**完整通读**
- **环境清单**：`ENVIRONMENT.md`
- **现有代码**：`src/Recents.App/` 已有 P0 部分实现，但**与新版 PRD 存在多处偏离**，必须先修后建。

你不是在重新设计，是按 PRD 修复偏离 → 补全 P0 余下功能。PRD 与现有结构冲突时**以 PRD 为准**。

---

## 阶段 A：合规回滚（先做这 13 条，不修完不允许进入阶段 B）

下面每一条都是新版 PRD 的硬约束，当前实现违反。**修一条 build 一次**。

### A1. 标题改为 `Recents`，全工程清除 `RecentDock`

- **改 `MainWindow.xaml`** `Title="..."` 为 `Recents`。
- 全文搜索 `RecentDock` 在 .xaml / .cs / .resx / 注释里的所有出现，全部替换为 `Recents`。
- 验证：编译后用 `strings Recents.exe | grep -i recentdock` 应无输出。
- PRD §5.2 / §7.8 / §14.2 第一条。

### A2. TargetFramework 回退到 .NET 8

- **改 `Recents.App.csproj`** `<TargetFramework>net10.0-windows</TargetFramework>` → `net8.0-windows`。
- PRD §2 / §16 #15 / `ENVIRONMENT.md`。
- 注：.NET 10 不在我方支持矩阵；任何"切高一版能修 bug"的诱惑都要拒绝。

### A3. 主窗口默认 ShowInTaskbar="False"

- **改 `MainWindow.xaml`** `ShowInTaskbar="True"` → `"False"`。
- PRD §7.3。

### A4. 主窗口尺寸改为 1040×760 / 760×520

- **改 `MainWindow.xaml`** `Width="960" Height="720" MinWidth="800" MinHeight="600"` →
  `Width="1040" Height="760" MinWidth="760" MinHeight="520"`。
- PRD §7.2。

### A5. 托盘菜单删除"待实现"占位

- **改 `Services/TrayService.cs`** 删除 `menu.Items.Add("设置 (待实现)", null, (s, e) => { });` 整行。
- 菜单文案统一英文：`Show / Rescan / Exit`（按 PRD §7.8）。
- 不允许出现任何"待实现"、"敬请期待"等占位文本。
- PRD §5.0 / §6.2 / §17。

### A6. 列表不展示 Recent 文件夹中的 `.lnk` 本体

- **改 `Services/RecentLnkSource.cs`** 当前实现已经把 .lnk 解析到目标，但当目标也是 `.lnk` 文件（非 Recent 目录的）时仍然要进列表。
- 在 `MergeAsync` 之前加一道过滤：当 `incoming.NormalizedPath` 位于 `%APPDATA%\Microsoft\Windows\Recent` 或其子目录、且扩展名是 `.lnk` 时，**直接丢弃**。
- 同时加默认 `ExcludedExtensions` 过滤（包含 `.lnk`），但 §5.11 是系统硬规则，**不依赖**用户黑名单。即便用户从黑名单删除 `.lnk`，Recent 目录的 .lnk 也不能进入列表。
- PRD §5.11 / §6.14。

### A7. 项目 build 输出（`bin\` / `obj\`）默认排除

- **改 `Models/AppSettings.cs`** `ExcludedPaths` 默认值加：
  ```
  bin\Debug
  bin\Release
  obj\Debug
  obj\Release
  ```
- 在 `RecentIndexService.MergeAsync` 或入索引前的过滤层应用 `ExcludedPaths` 前缀匹配（不区分大小写）。
- 验证：从 IDE 打开项目自身后，列表里**不能**出现 `Recents.deps.json` 等构建产物。
- PRD §6.14 / §14.2 倒数第三条。

### A8. 拖拽必须同时附 FileDrop + Shell IDList Array

- **改 `MainWindow.xaml.cs`** `ItemsList_MouseMove` 当前只附 `DataFormats.FileDrop`，**必须**同时附 `CFSTR_SHELLIDLIST`。
- 实现路径：把这部分逻辑搬到 `Services/DragDropService.cs`，由它构造 DataObject。需要 P/Invoke `SHParseDisplayName` / `ILCreateFromPath` 拿 PIDL，组装 `CIDA` 结构后塞 `MemoryStream` 进 DataObject。
- 验收：拖到微信、飞书、Outlook、Edge、Explorer 五处目标都能正确传文件。仅 FileDrop 在 Outlook 里不可靠。
- PRD §6.8 / §15 P0 / §14.4。

### A9. 文件夹条目必须保留在索引中

- **改 `Services/RecentIndexService.cs`** 当前 `MergeAsync` 第 148 行 `if (incoming.IsFolder || incoming.Exists == ExistsState.Missing)` 直接 `RemoveAsync` 文件夹 → 错误。
- 改为：`IsFolder=true` 的条目正常入库；只有 `Exists == ExistsState.Missing` 时才考虑移除（且要看 `ShowMissingFiles` 配置）。
- 同步修 `LoadFromDatabase` 的 `WHERE is_folder = 0 AND exists_state != 0` → 去掉 `is_folder = 0`，根据视图（All / Recent Folders）动态过滤。
- PRD §6.3.7 / §6.24。

### A10. `FileType` 必须由 FileTypeClassifier 分类

- 当前 `KnownFolderWatchSource.cs:155` / `RecentLnkSource.cs:117` 都是 `FileType = "Other"` 硬编码 → 顶部 Chip Docs / Images / Code 全部失效。
- **填充 `Utils/FileTypeClassifier.cs`**，提供：
  ```csharp
  public static string Classify(string extension, bool isFolder, IReadOnlyDictionary<string, List<string>> groups);
  ```
  返回 `Documents` / `Images` / `Videos` / `Audio` / `Archives` / `Code` / `Other`，文件夹返回空字符串或专用值。
- **在所有数据源** `CreateItem` / `HandleDebouncedChange` 末尾调用 `FileTypeClassifier.Classify(...)` 填充 `FileType`。
- 分组映射读自 `AppSettings.FileTypeGroups`（已有默认值）。
- PRD §6.7 / §16 #13。

### A11. 热键 Badge 改为数据绑定，删除 XAML 硬编码

- **改 `MainWindow.xaml`** 找到显示 `Ctrl + Alt + R` 的 TextBlock，改为绑定 `{Binding HotkeyDisplay}` 或类似。
- **改 `MainViewModel.cs`** 暴露 `HotkeyDisplay { get; }`，从 `HotkeyService.ActiveLabel` 取值。
- 注册失败时 Badge 显示 `Hotkey unavailable`。
- 验收：人为占用 Ctrl+Alt+R（运行另一个程序绑定）后启动 Recents，Badge 应显示候选热键（`Win+;` 等），而不是 `Ctrl + Alt + R`。
- PRD §5.3 / §6.1。

### A12. 补全 P0 必需的键盘绑定

- **改 `MainWindow.xaml.cs`** `Window_PreviewKeyDown` 添加：
  - `Ctrl+C`：复制 `SelectedItem.DisplayPath` 到剪贴板。
  - `Ctrl+Shift+C`：复制 `SelectedItem.DisplayName` 到剪贴板。
  - `Ctrl+O`：调用 `FileActionService.RevealInExplorer(path)`。
- 所有命令都走 `FileActionService`，不要散落在 MainWindow 里。
- PRD §6.13 / §15 P0。

### A13. P0 不显示 Settings 导航 + 视图密度按钮

- **改 `MainWindow.xaml`** 删除（或 `Visibility="Collapsed"`）侧栏 `Settings` RadioButton 和右上角视图密度切换按钮。
- 用 `Visibility` 而非"灰色禁用"，PRD §5.0 明确规定"未启用 ≠ 灰色禁用"。
- 等到 P1 实现设置页 / 密度切换时再恢复。
- PRD §5.4 / §5.6 / §17。

---

### 阶段 A 完成定义

执行以下命令应全部通过：

```powershell
dotnet build src\Recents.App\Recents.App.csproj
```

人工核验：

- [ ] 启动后窗口标题显示 `Recents`，不在任务栏，1040×760。
- [ ] 列表里没有 `bin\Debug\Recents.deps.json` 之类构建产物。
- [ ] 列表里没有 `Antigravity.lnk` 之类 Recent 目录的 .lnk 条目。
- [ ] 顶部 Chip `Docs` 点击后能筛出 `.docx / .pdf` 文件（不再全是 Other）。
- [ ] 主窗口侧栏看不到 Settings；右上角看不到视图密度图标；托盘菜单看不到"设置(待实现)"。
- [ ] 拖一个文件到 Outlook，能创建为附件（仅 FileDrop 在某些场景失败，加上 Shell IDList Array 后稳定）。
- [ ] `Ctrl+C` 复制路径，`Ctrl+Shift+C` 复制文件名，`Ctrl+O` 打开所在位置。
- [ ] 手动占用 Ctrl+Alt+R，重启应用，搜索框右侧 Badge 显示候选热键名字（不是硬编码 `Ctrl + Alt + R`）。
- [ ] `Recents.App.csproj` 的 `<TargetFramework>` 是 `net8.0-windows`。

阶段 A 完成后停下来报告：build 状态、上述每条核验结果。等待人类确认再进入阶段 B。

---

## 阶段 B：补齐 P0 剩余功能（PRD §15 P0）

阶段 A 通过后，按下面顺序补 P0 的剩余功能。每条做完即 `dotnet build` + 手测对应行为。

### B1. 关闭按钮首次气泡 + `ClosedToTrayNoticeShown` 持久化

- 在 `MainWindow.Window_Closing` 中：若 `_settings.Current.ClosedToTrayNoticeShown == false`，调用 `_tray.ShowBalloon(...)` 显示 `Recents 仍在托盘运行。可在设置中改为按关闭即退出。`，然后置 `true` 并 `_settings.Save()`。
- PRD §5.2 / §14.2 关闭气泡条目。

### B2. `StatusHintService` 输出动态状态栏

- 新建 `Services/StatusHintService.cs`：维护 `Ready / Indexing / Watching / Partial / Error` 状态枚举 + 当前可见数量 + 动态键盘提示文本。
- `MainViewModel` 订阅它，`MainWindow.xaml` 绑定到底部状态栏。
- 键盘提示：无选中 → `↑↓ Navigate`；有选中 → `↑↓ Navigate · Enter Open`；当前项可拖 → 追加 `· Drag to share`。
- PRD §5.9。

### B3. 行内 Reveal / Pin / More 按钮命令绑定

- `RecentItemViewModel` 暴露 `OpenCommand / RevealCommand / TogglePinCommand / MoreCommand`（用 `CommunityToolkit.Mvvm.Input.RelayCommand`）。
- `MainWindow.xaml` 文件行模板里把行内三个图标按钮 `Command="{Binding ...}"` 绑上。
- 命令实现走 `FileActionService`。
- More 菜单弹 `ContextMenu`，菜单项就是 §6.10.1 五项。
- Pin 状态变化必须立即写 SQLite 并刷新 UI。

### B4. 多选 + 多选拖拽

- `ListBox` 启用 `SelectionMode="Extended"`。
- 拖拽逻辑改为：取 `ItemsList.SelectedItems` 全部 `NormalizedPath`，构造同时含 FileDrop + Shell IDList Array 的 DataObject。
- 多选时行内单项按钮禁用（`IsEnabled` 绑定到 `IsSinglySelected`）。
- PRD §5.7。

### B5. 排序下拉

- `MainViewModel` 加 `SelectedSort` 属性（枚举：`NewestFirst / NameAZ`），切换时重排 `ItemsView`。
- `MainWindow.xaml` 顶部 ComboBox 只显示这两项；不允许出现 `Oldest first` / `Size largest`。
- 当前选项写入 `AppSettings.DefaultSort` 并持久化。
- PRD §5.6。

### B6. Recent Folders 视图

- `RecentFolders` 导航项点击后切换到只显示 `IsFolder=true` 的视图。
- 折叠子目录到父目录的逻辑：取 RecentTime 最大的父目录作为代表（PRD §6.24 父目录聚合）。
- 双击文件夹用 Explorer 打开。
- PRD §6.24。

### B7. 空状态 / 加载态 / 错误态

- 列表为空时显示 `No recent files found / Try changing filters or rebuilding the index.` + `Clear filters` 与 `Rebuild index` 两个按钮。
- `Indexing...` 状态由 `StatusHintService` 提示，列表正常显示已有数据（不要遮挡列表）。
- `Rebuild index` 点击直接执行，不弹确认。
- PRD §5.10。

### B8. 真实文件图标占位（`SHGetFileInfo` + `SHGFI_USEFILEATTRIBUTES`）

- 填充 `Services/FileIconService.cs`：按扩展名 + isFolder 取系统通用图标，缓存 `WriteableBitmap` 在内存，懒加载到 `RecentItemViewModel.Icon`。
- 真实缩略图（`IShellItemImageFactory`）属于 P1，本阶段不做。
- **不允许**任何二次重绘 / AI 图标（PRD §7.6）。
- PRD §6.11。

### B9. 颜色 Token 落到 App.xaml 资源

- 新建 `Resources/Colors.xaml`，把 PRD §7.4 的全部 Token 定义为 `SolidColorBrush` 资源（key 与 PRD 名一致：`WindowBackground` / `SidebarBackground` / `Accent` / ...）。
- `App.xaml` `<Application.Resources>` 合并字典。
- 现有 `MainWindow.xaml` 内的 `<SolidColorBrush x:Key="..." Color="..."/>` 改为引用全局资源（`{StaticResource Accent}` 等）。
- 设计图与 §7.4 不一致时**以 §7.4 为准**。

### B10. `bin\` / `obj\` 排除验收 + 默认 ExcludedPaths 校对

- 启动一次后检查 `%APPDATA%\Recents\settings.json` 的 `ExcludedPaths` 字段，应包含 `bin\Debug` / `bin\Release` / `obj\Debug` / `obj\Release` / `node_modules` / `.git` 等。
- 若旧版用户的 settings.json 已存在但缺少这些项，启动时合并补齐（不覆盖用户已修改的项）。

---

### 阶段 B 完成定义（= P0 完成定义）

必须**全部**通过 PRD 中以下三组验收：

- §14.1 基础功能（9 项）
- §14.2 主界面 UI 验收（**全部 18 项**）
- §14.3 数据源覆盖前两项（Downloads 2 秒 + IDE 打开 / 保存）

性能验收：

- §14.7 全部 5 项（重点：FSW 溢出恢复 + OneDrive 占位符不联网下载）

**关键卡点**：从浏览器下载文件到 Downloads，2 秒内出现在列表顶部。这条不过 = P0 不算完成，重做 KnownFolderWatchSource。

完成阶段 B 后输出：

- build 结果
- §14.1 / §14.2 / §14.3 / §14.7 逐项打勾（用 markdown checkbox）
- 已知技术债 / 偏离 PRD 的地方
- 等待人类确认再进入 P1（设置页 UI、收藏增强、Jump List、Office MRU、UNC 自定义来源、真实图标缩略图、开机自启、§6.10.2 扩展菜单项）

---

## 硬约束（违反任何一条都视为返工）

新版 PRD 加了几条特别容易翻车的硬规则，再强调一次：

- ❌ **未实现的功能不得显示**（不允许灰色禁用），PRD §5.0 / §17。
- ❌ **不展示 Recent 文件夹中的 .lnk**（系统硬规则，用户黑名单不能关闭），PRD §5.11 / §6.14。
- ❌ **图标只能来自系统真实图标 + 项目内通用占位 + Segoe Fluent Icons**，禁止任何二次重绘 / AI 头像 / 风格化图标，PRD §7.6。
- ❌ **TargetFramework 必须 `net8.0-windows`**，PRD §16 #15。
- ❌ **标题必须 `Recents`**，工程内不允许出现 `RecentDock` 字符串，PRD §5.2 / §7.8。
- ❌ 不集成云盘 SDK / HTTP 客户端 / 浏览器历史读取库
- ❌ 不使用 WSH `IWshRuntimeLibrary`、不使用 Electron / WebView2 作为主 UI
- ❌ UI 线程不允许执行：磁盘扫描、.lnk 解析、注册表读取、`File.Exists` 远程探测、图标加载
- ❌ 不删除原始文件
- ❌ 不联网（本阶段不做 UNC）
- ❌ 不新增 README / CHANGELOG / ARCHITECTURE / 设计文档 —— PRD 是唯一真理源
- ✅ 所有路径处理走 `PathNormalizer`
- ✅ 所有文件类型分类走 `FileTypeClassifier`，禁止 `FileType="Other"` 硬编码
- ✅ 所有 UI 文案从 §7.8 表中取，禁止散落字符串字面量
- ✅ 每完成一个编号必须 `dotnet build` 通过 + 手测对应行为
- ✅ 注释中文优先，与 PRD 风格一致；只解释 WHY，不解释 WHAT

---

## 工作流要求

1. **阶段 A 一次只做一个编号**（A1–A13）。每改完一条，build + 手测，再做下一条。
2. 阶段 A 全部 13 条完成后**停下来报告**，等人类确认再进入阶段 B。
3. 阶段 B 完成 1–3 个编号给一次进度报告（哪些已完成、build 状态、是否有偏离 PRD 的取舍）。
4. 阶段 B 全部完成后停下来，输出 P0 完成验收。
5. 遇到 PRD 没明确的边界情况，**先停下问**，不要自己拍板。

开始吧。先做 A1。
