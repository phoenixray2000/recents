# Recents — 剩余功能实施计划

> 生成时间：2026-05-02  
> 基于：`recent_files_windows_prd.md`（唯一产品真理源）+ 代码库逐文件 review  
> 状态图例：🔴 严重 Bug / ❌ 未实现 / ⚠️ 部分实现 / ✅ 已完成

---

## 一、已确认完成项（无需再做）

| 功能 | PRD 章节 | 代码位置 | 备注 |
|---|---|---|---|
| 全局热键注册（含候选链） | §6.1 | `HotkeyService.cs` | ✅ |
| 系统托盘常驻 | §6.2 | `TrayService.cs` | ✅ |
| 单实例检测 | §6.2 | `SingleInstanceService.cs` | ✅ |
| L1 已知文件夹监听 | §6.3.2 | `KnownFolderWatchSource.cs` | ✅ 含 FileTypeClassifier |
| L1 自定义目录监听 | §6.3.3 | `UserFolderWatchSource.cs` | ✅ |
| L1 Recent .lnk 解析 | §6.3.4 | `RecentLnkSource.cs` | ✅ 含 FileTypeClassifier |
| .lnk 不展示（系统硬规则） | §5.11 | `MergeAsync()` | ✅ |
| FSW Error 重扫 | §6.19 | 各 Source | ✅ |
| SQLite 索引 | §6.18 | `RecentRepository.cs` | ✅ |
| 搜索过滤（多 token AND） | §6.6 | `MainViewModel.FilterItem()` | ✅ |
| 文件类型筛选 | §6.7 | `FilterItem()` | ✅ |
| 排序（Newest / Name A-Z） | §6.5 | `MainViewModel.ApplySort()` | ✅ |
| Open / Reveal / CopyPath / CopyFileName | §6.9/§6.10 | `FileActionService.cs` | ✅ |
| Pin / Unpin 收藏 | §6.23 | `RecentItemViewModel.TogglePinCommand` | ✅ |
| More 菜单（基础项） | §6.10.1 | `MainWindow.xaml` | ✅ |
| Shell IDList Array 拖拽 | §6.8 | `DragDropService.cs` | ✅ CIDA 完整构建 |
| 多文件拖拽 | §6.8 | `MainWindow.xaml.cs` | ✅ |
| Esc 隐藏窗口 | §6.13 | `Window_PreviewKeyDown` | ✅ |
| Ctrl+F 聚焦搜索 | §6.13 | `Window_PreviewKeyDown` | ✅ |
| Ctrl+O Reveal | §6.13 | `Window_PreviewKeyDown` | ✅ |
| 首次关闭托盘气泡提示 | §5.2 | `Window_Closing` | ✅ |
| 状态栏 StatusHintService 骨架 | §5.9 | `StatusHintService.cs` | ⚠️ 有 Bug（见 Bug#3/4） |
| FileTypeClassifier 统一实现 | §6.7 | `FileTypeClassifier.cs` | ✅ |
| CloudPlaceholder 保护 | §6.19 | `CloudPlaceholderDetector.cs` | ✅（ScanDirectory 调用） |
| Debounce 1200ms | §6.19 | `Debouncer.cs` | ✅ |
| InternalBufferSize 64KB | §6.19 | 各 Source | ✅ |
| 图标服务骨架 | §6.11 | `FileIconService.cs` | ⚠️ 同步调用（见 Bug#6） |
| 开机自启服务骨架 | §6.21 | `StartupService.cs` | ✅ 服务存在 |
| 排除规则过滤 | §6.14 | `MergeAsync()` | ✅ |
| 文件夹类型条目保留 | §6.3.7 | `MergeAsync()` | ✅ 无过滤 |

---

## 二、已发现的 Bug（阻塞运行或行为错误）

### 🔴 Bug-1：`_tray` 为 null 时传入 MainWindow

**文件：** `App.xaml.cs` 第 58 行  
**问题：** `new MainWindow(mainVm, _settings, _tray)` — 此时 `_tray` 还未初始化（第 67 行才赋值），传入为 `null`。`Window_Closing` 调用 `_tray.ShowBalloon(...)` 将在首次关闭时抛 NullReferenceException。

**修复：**
```csharp
// App.xaml.cs
// 调整顺序：先建 tray，再建 mainWindow
_tray = new TrayService(_index); // 临时构造，不传 mainWindow
var mainWindow = new MainWindow(mainVm, _settings, _tray);
_tray.SetMainWindow(mainWindow); // TrayService 新增 SetMainWindow 方法
```
或者将 `_tray` 改为懒初始化：MainWindow 构造时不传 tray，改为 `_tray = App.Current.TrayService`（单例访问）。

---

### 🔴 Bug-2：`Ctrl+Shift+C` 快捷键永不触发

**文件：** `MainWindow.xaml.cs` 第 78–95 行  
**问题：** 外层条件是 `Keyboard.Modifiers == ModifierKeys.Control`（严格等于），当同时按住 Ctrl+Shift，`Keyboard.Modifiers == Control | Shift`，外层条件为 false，内层对 Shift 的判断永远到不了。

**修复：**
```csharp
// 修改前
if (Keyboard.Modifiers == ModifierKeys.Control)
{
    if (e.Key == Key.C)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) // 永远 false

// 修改后
if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
{
    if (e.Key == Key.C)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            FileActionService.CopyFileName(selected.DisplayPath);
        else
            FileActionService.CopyPath(selected.DisplayPath);
    }
    if (e.Key == Key.O && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
    {
        // Ctrl+O: Reveal
    }
}
```

---

### 🔴 Bug-3：状态栏 Ready 颜色错误（蓝色 vs. 绿色）

**文件：** `StatusHintService.cs` 第 22、35–37 行  
**问题：** 初始值和 `AppStatus.Ready` 都使用 `#40C4FF`（蓝色），PRD §5.9 规定"绿色点表示 Ready / Watching"，颜色应为 `#63C554`（Success）。

**修复：**
```csharp
// 两处修改
[ObservableProperty]
private SolidColorBrush _statusColor = new(Color.FromRgb(0x63, 0xC5, 0x54)); // Ready = 绿

case AppStatus.Ready:
    StatusText  = "Ready";
    StatusColor = new(Color.FromRgb(0x63, 0xC5, 0x54)); // #63C554 绿
    break;
case AppStatus.Watching:
    StatusText  = "Watching sources";
    StatusColor = new(Color.FromRgb(0x63, 0xC5, 0x54)); // 同绿
    break;
```

---

### 🔴 Bug-4：`AppStatus.Partial` 状态未处理

**文件：** `StatusHintService.cs` `SetStatus()` 方法  
**问题：** `AppStatus` 枚举有 `Partial` 值，但 `switch` 中无对应 case，UNC 断开时无法正确显示黄色警告。

**修复：** 在 switch 中补充：
```csharp
case AppStatus.Partial:
    StatusText  = "Some sources unavailable";
    StatusColor = new(Color.FromRgb(0xF5, 0xB6, 0x42)); // #F5B642 黄
    break;
```

---

### 🔴 Bug-5：`HasItems` 追踪总数而非过滤后数量

**文件：** `MainViewModel.cs` 第 57–60 行  
**问题：** `HasItems = _indexService.Items.Count > 0` 追踪的是未过滤的内存集合总量。当有 200 条记录但搜索/筛选后结果为 0 时，`HasItems = true`，空状态面板不显示。

**修复：** 改为监听 `ItemsView.CollectionChanged` 或 `Filter` 后的 `Cast<object>().Count()`：
```csharp
// MainViewModel.cs
private void RefreshHasItems()
{
    HasItems = ItemsView.Cast<object>().Any();
}

// 在 ItemsView.Filter 执行后触发（用 CollectionView.CollectionChanged 或在 OnSearchTextChanged / OnCurrentCategoryChanged 中调用）
partial void OnSearchTextChanged(string value)
{
    ItemsView.Refresh();
    RefreshHasItems();
}
partial void OnCurrentCategoryChanged(string value)
{
    ItemsView.Refresh();
    RefreshHasItems();
}
```
同时 `_indexService.Items.CollectionChanged` 处也要调用 `RefreshHasItems()`。

---

### 🟠 Bug-6：图标同步加载阻塞 UI 线程

**文件：** `RecentItemViewModel.cs` 第 42 行  
**问题：** `Icon => FileIconService.GetIcon(...)` 是同步 getter，在 ListBox 渲染时从 UI 线程调用，批量加载大量文件项时造成卡顿。PRD §6.11 要求"UI 永远不被图标加载阻塞"。

**修复：** 改为异步懒加载模式：
```csharp
private ImageSource? _icon;
private bool _iconLoaded;

public ImageSource? Icon
{
    get
    {
        if (!_iconLoaded)
        {
            _iconLoaded = true;
            Task.Run(() => FileIconService.GetIcon(Item.NormalizedPath, Item.IsFolder, true))
                .ContinueWith(t =>
                {
                    _icon = t.Result;
                    Application.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(Icon)));
                }, TaskScheduler.Default);
        }
        return _icon;
    }
}
```

---

### 🟠 Bug-7：`HotkeyDisplay` 无变更通知

**文件：** `MainViewModel.cs` 第 20 行  
**问题：** `HotkeyDisplay => _hotkeyService.ActiveLabel` 是普通 get-only 属性，`HotkeyService` 也未实现 `INotifyPropertyChanged`。当用户在 Settings 中修改热键后，搜索框 Badge 不更新。

**修复：** `HotkeyService` 添加事件或 `ActiveLabel` 改为可观察属性，并在 `MainViewModel` 中订阅：
```csharp
// HotkeyService.cs
public event Action? ActiveLabelChanged;
// 注册成功时 ActiveLabelChanged?.Invoke();

// MainViewModel.cs
_hotkeyService.ActiveLabelChanged += () => OnPropertyChanged(nameof(HotkeyDisplay));
```

---

### 🟡 Bug-8：Sort 下拉默认标签文案不符 PRD

**文件：** `MainWindow.xaml` 第 231 行  
**问题：** `SortLabel` 初始文本为 `"Recent Time"`，PRD §5.6 规定默认排序下拉显示 `"Newest first"`。

**修复：** XAML 改为 `<TextBlock x:Name="SortLabel" Text="Newest first" .../>` 且 MenuItem "Recent Time" 改为 "Newest first"，Tag 保留 "RecentTime"。

---

### 🟡 Bug-9：侧边栏导航与顶部 Filter Chip 状态互相覆盖

**文件：** `MainWindow.xaml.cs` `Nav_Checked()`  
**问题：** 侧边栏 RadioButton 和顶部筛选 Chip 共享同一个 `Nav_Checked` 处理器，都写 `_viewModel.CurrentCategory`。当侧边栏选 "Recent Folders"，再点 Chip "Docs"，`CurrentCategory` 被覆盖为 "Documents"，侧边栏高亮丢失，且实际上 "Folders + Docs" 应叠加过滤（PRD §5.5）。

**修复：** 将导航类别（nav）和 Chip 类型筛选（chip）拆分为两个独立属性：
```csharp
// MainViewModel.cs
[ObservableProperty] private string _currentNavCategory = "All";  // 侧边栏
[ObservableProperty] private string _currentChipFilter  = "All";  // 顶部 Chip

// FilterItem() 改为同时判断两个维度
```

---

## 三、未实现的 P0 功能（按优先级排序）

---

### ❌ F-01：Settings 页面（优先级最高，影响最广）

**PRD 章节：** §6.22  
**当前状态：** `SettingsWindow.xaml.cs` 仅 `InitializeComponent()`；`SettingsViewModel.cs` 仅空类  

**需实现的 7 个分组：**

#### F-01-A：General 分组

```csharp
// SettingsViewModel.cs 新增属性
[ObservableProperty] private bool _launchAtStartup;
[ObservableProperty] private bool _hideOnFocusLost;
[ObservableProperty] private bool _alwaysOnTop;
[ObservableProperty] private bool _closeToTray = true;  // true=隐藏托盘 / false=退出
```

XAML 控件：Toggle（ToggleSwitch 或 CheckBox）+ 说明文字  
`LaunchAtStartup` 需调用 `StartupService.Enable()` / `StartupService.Disable()`  
`AlwaysOnTop` / `HideOnFocusLost` 变更后即时写入 settings + 应用到 MainWindow

#### F-01-B：Hotkey 分组

```csharp
[ObservableProperty] private string _currentHotkey;  // 来自 HotkeyService.ActiveLabel
[ObservableProperty] private string _recordingHotkey; // 录制中的热键文本

[RelayCommand] private void StartRecording();  // 进入录制模式
[RelayCommand] private void ResetHotkey();     // 重置为默认 Ctrl+Alt+R
```

XAML：Label 显示当前热键；KeyCapture TextBox（拦截 KeyDown 事件组合，禁止单独的 Modifier 键）；"Reset" 按钮  
录制完成后调用 `HotkeyService.ReRegister(newMod, newVk, newLabel)`（需新增此方法）

#### F-01-C：Sources 分组

```csharp
// SourcesViewModel.cs 改为完整实现
public ObservableCollection<SourceItemViewModel> Sources { get; }

[RelayCommand] private async Task AddFolder();       // FolderBrowserDialog
[RelayCommand] private async Task AddNetworkPath(); // InputDialog 输入 UNC
[RelayCommand] private void RemoveSource(SourceItemViewModel src);
```

XAML：每行 `SourceItemViewModel` 显示：图标 + 显示名 + 状态 Badge（Active/Disconnected/Disabled/Stale） + Toggle + 删除按钮  
默认的 6 个已知文件夹（Downloads/Desktop/Documents/Pictures/Videos/Music）不可删除只可禁用  
`RecentLookbackDays` 可在行内或展开后设置（Slider 或 TextBox，范围 1–365）

#### F-01-D：List 分组

```csharp
[ObservableProperty] private int _maxRecentItems;   // ComboBox: 100/200/500/1000/Unlimited
[ObservableProperty] private bool _showMissingFiles;
[ObservableProperty] private bool _showFolders;
[ObservableProperty] private string _defaultSort;   // ComboBox: "Newest first" / "Name A-Z"
```

变更立即写入 settings；`MaxRecentItems` 变更需触发 `_index.LoadFromDatabase(newMax)`

#### F-01-E：Filters 分组

```csharp
// 每类用 ObservableCollection<string> 驱动 TagEditor
public ObservableCollection<string> ExcludedExtensions { get; }
public ObservableCollection<string> ExcludedPaths { get; }
public ObservableCollection<string> ExcludedKeywords { get; }
public ObservableCollection<string> WhitelistedPaths { get; }
```

XAML：每行 = TextBox（输入回车 Add） + 现有标签列表（每个 tag 可点 × 删除）  
变更立即写入 settings；不需重启（`MergeAsync` 读 `_settingsService.Current`）

#### F-01-F：Cache 分组

```csharp
[RelayCommand] private async Task RebuildIndex();   // 无需确认，直接 _index.RebuildAsync()
[RelayCommand] private void ClearIconCache();       // 删除 %LOCALAPPDATA%\Recents\icons\*
[RelayCommand] private void ClearHiddenItems();     // 清除 is_hidden=1 的数据库记录
```

"Rebuild index" 点击后状态栏切为 Indexing；完成后切回 Ready/Watching

#### F-01-G：About 分组

- 显示版本号（Assembly version，如 `1.0.0`）
- 数据路径（静态文本 + 可点击按钮打开目录）：
  - Settings: `%APPDATA%\Recents\settings.json`
  - Index DB: `%LOCALAPPDATA%\Recents\index.db`
  - Icons: `%LOCALAPPDATA%\Recents\icons\`
  - Logs: `%LOCALAPPDATA%\Recents\logs\`
- "Open log folder" 按钮 → `FileActionService.RevealInExplorer(logDir)`

#### F-01 通用要求

- `SettingsWindow` 样式使用与 `MainWindow` 相同的色值 token（`BgMain`、`TextPrimary` 等）
- 顶部显示分组 Tab 或左侧列表导航
- 每个设置项变更后立即调用 `_settingsService.Save()`（节流 300ms 避免过频写盘）
- `SettingsWindow` 打开方式：从侧边栏 Settings 导航项或托盘菜单 Settings 打开，同一时间只允许一个实例（单例窗口 `Activate()` 已有则置前）

---

### ❌ F-02：侧边栏 Favorites 导航项（P0）

**PRD 章节：** §5.4  
**当前状态：** `Visibility="Collapsed"` 且注释 `<!-- P1 -->`，但 PRD 明确 Favorites 是 P0 必须项

**修复：**

```xml
<!-- MainWindow.xaml -->
<!-- 去掉 Visibility="Collapsed" -->
<RadioButton Content="Favorites" Style="{StaticResource SidebarNavItem}" 
             Tag="&#xE734;" Checked="Nav_Checked"/>
```

```csharp
// MainViewModel.FilterItem()
// "Favorites" 导航下：只显示 IsFavorite=true 的条目（文件和文件夹均可）
if (CurrentNavCategory == "Favorites")
    if (!vm.Item.IsFavorite) return false;
```

Favorites 为空时显示空状态（带说明文字 "No pinned items yet"），不隐藏导航项。

---

### ❌ F-03：Settings 导航入口（侧边栏 + 托盘菜单）

**PRD 章节：** §5.4（Settings 仅当设置页已实现时显示）、§6.2  

Settings 页面（F-01）完成后需同时启用：

1. **侧边栏 Settings 导航项**（`MainWindow.xaml` 第 151 行，去掉 `Visibility="Collapsed"`）：
   ```xml
   <RadioButton Content="Settings" Style="{StaticResource SidebarNavItem}" 
                Tag="&#xE713;" Margin="0,0,0,20" Checked="Nav_Settings"/>
   ```
   ```csharp
   private void Nav_Settings(object sender, RoutedEventArgs e)
   {
       SettingsWindowSingleton.ShowOrActivate();
   }
   ```

2. **托盘菜单 Settings 项**（`TrayService.cs` `CreateMenu()`）：
   ```csharp
   menu.Items.Insert(2, new ToolStripMenuItem("Settings", null, (s, e) => 
       Application.Current.Dispatcher.Invoke(() => SettingsWindowSingleton.ShowOrActivate())));
   ```

`SettingsWindowSingleton` 为静态辅助类，维护单一 `SettingsWindow` 实例，防止多开。

---

### ❌ F-04：空状态操作按钮

**PRD 章节：** §5.10  
**当前状态：** `MainWindow.xaml` 空状态面板只有图标 + 文字，无按钮

**修复：** 在 `MainWindow.xaml` 空状态 StackPanel 中补充：

```xml
<Button Content="Clear filters" Command="{Binding ClearFiltersCommand}"
        Style="{StaticResource ActionButtonStyle}" Margin="0,12,8,0"/>
<Button Content="Rebuild index" Command="{Binding RebuildIndexCommand}"
        Style="{StaticResource ActionButtonStyle}" Margin="0,12,0,0"/>
```

```csharp
// MainViewModel.cs
[RelayCommand]
private void ClearFilters()
{
    SearchText = string.Empty;
    CurrentChipFilter = "All";
    CurrentNavCategory = "All";
}

[RelayCommand]
private async Task RebuildIndex()
{
    Status.SetStatus(StatusHintService.AppStatus.Indexing);
    await _indexService.RebuildAsync();
    // 重启各数据源
    await _app.RestartSourcesAsync();
}
```

---

### ❌ F-05：状态栏过滤后数量显示

**PRD 章节：** §5.9（"`128 items` 必须是当前过滤后可见数量，不是数据库总量"）  
**当前状态：** `UpdateCount` 接收 `_indexService.Items.Count`（未过滤总数）

**修复：** `MainViewModel` 在 `ItemsView.Refresh()` 之后更新计数：

```csharp
private void RefreshVisibleCount()
{
    var count = ItemsView.Cast<object>().Count();
    Status.UpdateCount(count);
}

// 在 OnSearchTextChanged、OnCurrentCategoryChanged、OnCurrentChipFilterChanged 中调用
// 在 _indexService.Items.CollectionChanged 中调用
```

---

### ❌ F-06：`ExistsProbeService` 接入（可视区文件存在性探测）

**PRD 章节：** §6.20  
**当前状态：** `ExistsProbeService.cs` 存在但未在任何地方调用

**需实现：**

```csharp
// MainWindow.xaml.cs 或 RecentItemViewModel.cs
// 当 ListBoxItem 进入可视区域时触发探测
ItemsList.ItemContainerGenerator.StatusChanged += (s, e) =>
{
    if (ItemsList.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
    {
        foreach (RecentItemViewModel vm in ItemsList.Items)
        {
            var container = ItemsList.ItemContainerGenerator.ContainerFromItem(vm);
            if (container != null && IsInViewport(container))
                _ = ExistsProbeService.ProbeAsync(vm); // 超时 1.5s，结果写回 vm.Item.Exists
        }
    }
};
```

或使用 `ScrollChanged` 事件触发对可视区内条目的批量探测。`ExistsProbeService.ProbeAsync` 应：
- 本地路径：`File.Exists`（1.5s 超时）
- UNC 路径：同超时，超时返回 `Unknown`
- 结果写入 `vm.Item.Exists` 并调用 `_index.MergeAsync()`（触发 DB 更新 + UI refresh）

---

### ❌ F-07：Jump List L2 数据源

**PRD 章节：** §6.3.5（标注"✅ 必选"）  
**当前状态：** `JumpListSource : StubRecentSource`（空实现）

**实现步骤：**

1. **添加 NuGet：** `OpenMcdf`（v3.x，纯托管 OLECF 解析）
   ```xml
   <!-- Recents.App.csproj -->
   <PackageReference Include="OpenMcdf" Version="3.*" />
   ```

2. **扫描目录：**
   ```csharp
   var autoDir = Path.Combine(
       Environment.GetFolderPath(SpecialFolder.ApplicationData),
       @"Microsoft\Windows\Recent\AutomaticDestinations");
   ```

3. **解析 OLECF：** 每个 `.automaticDestinations-ms` 文件是 Compound Document，内含多个 Stream，每个 Stream 是一条 MS-SHLLINK（.lnk 格式）记录
   ```csharp
   using var cf = new CompoundFile(filePath, CFSUpdateMode.ReadOnly, CFSConfiguration.SectorRecycle);
   cf.RootStorage.VisitEntries(entry =>
   {
       if (entry.IsStream && entry.Name != "DestList")
       {
           // 读取 Stream 字节 → 交给 ShellLinkResolver 解析
           var stream = cf.RootStorage.GetStream(entry.Name);
           var bytes = stream.GetData();
           var result = ShellLinkResolver.ResolveFromBytes(bytes);
           // ...
       }
   }, false);
   ```

4. **`ShellLinkResolver` 新增 `ResolveFromBytes(byte[])` 方法**（Securify.ShellLink 支持从 byte[] 解析）

5. **FileSystemWatcher** 监听 `AutomaticDestinations` 目录，文件变化时增量重解析对应 `.automaticDestinations-ms`

6. 解析失败单文件忽略（catch + Log.Warning）

7. 启动时 `App.cs` 中加入 `JumpListSource` 到 sources 列表

---

### ❌ F-08：Office MRU L2 数据源

**PRD 章节：** §6.3.6（标注"可选（首版做）"）  
**当前状态：** `OfficeMruSource : StubRecentSource`

**实现步骤：**

```csharp
// 读取注册表键
const string officeRoot = @"Software\Microsoft\Office";

// 枚举版本号子键（如 16.0、15.0）
using var officeKey = Registry.CurrentUser.OpenSubKey(officeRoot);
foreach (var version in officeKey.GetSubKeyNames())
{
    // 枚举 App（Word、Excel、PowerPoint 等）
    using var appKey = officeKey.OpenSubKey($@"{version}\Word\User MRU");
    // ... \Excel\User MRU, \PowerPoint\User MRU

    // 枚举 User MRU 下 sid 子键
    foreach (var sid in appKey.GetSubKeyNames())
    {
        using var fileMruKey = appKey.OpenSubKey($@"{sid}\File MRU");
        foreach (var valueName in fileMruKey.GetValueNames())
        {
            // 格式："[F00000000][T01D...][O00000000]*C:\path\to\file.docx"
            var raw = fileMruKey.GetValue(valueName)?.ToString();
            // 解析时间戳和路径
            var (timestamp, path) = ParseOfficeMruValue(raw);
        }
    }
}
```

- 定时轮询（5分钟）+ 启动时刷新（不做注册表 Watch）
- 构造 `RecentItem` 时 `Sources = SourceKinds.OfficeMru`，`RecentTime` 取解析出的时间戳

---

### ❌ F-09：OpenSavePidlMRU L3 数据源

**PRD 章节：** §6.3.6（标注"可选（首版做）"）  
**当前状态：** `OpenSavePidlMruSource : StubRecentSource`

**实现步骤：**

```csharp
// 注册表路径
const string key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU";

using var root = Registry.CurrentUser.OpenSubKey(key);
foreach (var extKey in root.GetSubKeyNames())
{
    // extKey = "docx", "xlsx", "*" 等
    using var extSubKey = root.OpenSubKey(extKey);
    foreach (var valueName in extSubKey.GetValueNames())
    {
        if (valueName == "MRUListEx") continue; // 跳过排序索引

        // 值是 PIDL 二进制
        var pidlBytes = extSubKey.GetValue(valueName) as byte[];
        // 用 P/Invoke SHGetPathFromIDListW 将 PIDL 转路径
        var path = SHGetPathFromIDListBytes(pidlBytes);
        if (!string.IsNullOrEmpty(path))
        {
            // 构造 RecentItem，RecentTime = File.GetLastWriteTime（PIDL 内无时间戳）
        }
    }
}
```

同样定时轮询 5 分钟，不做 Watch。

---

### ⚠️ F-10：响应式侧边栏（窗口宽度 < 900px 折叠）

**PRD 章节：** §7.2  
**当前状态：** 侧边栏固定宽度 220px，无折叠行为

**实现方案：**

```xml
<!-- MainWindow.xaml：侧边栏列宽改为自适应 -->
<ColumnDefinition x:Name="SidebarCol" Width="220"/>
```

```csharp
// MainWindow.xaml.cs：监听窗口宽度变化
protected override void OnRenderSizeChanged(SizeChangedInfo info)
{
    base.OnRenderSizeChanged(info);
    UpdateSidebarMode(info.NewSize.Width);
}

private void UpdateSidebarMode(double width)
{
    if (width < 900)
    {
        SidebarCol.Width = new GridLength(56); // Icon-only 模式
        // 隐藏导航文字，仅显示 Tag 图标
        foreach (var rb in SidebarNavItems)
            rb.SetValue(NavTextProperty, Visibility.Collapsed);
    }
    else
    {
        SidebarCol.Width = new GridLength(220);
        foreach (var rb in SidebarNavItems)
            rb.SetValue(NavTextProperty, Visibility.Visible);
    }
}
```

侧边栏 RadioButton 模板需支持"图标模式"（仅图标居中）和"完整模式"（图标+文字）的切换，通过 `VisualStateManager` 或 DataTrigger 实现。

---

### ⚠️ F-11：路径中段省略显示

**PRD 章节：** §7.9（`D:\very\long\…\Contracts\file.docx`）  
**当前状态：** XAML 使用 `TextTrimming="CharacterEllipsis"`（尾部省略），Tooltip 无完整路径

**实现：**

1. 新增 `Converter/MidEllipsisConverter.cs`：
   ```csharp
   [ValueConversion(typeof(string), typeof(string))]
   public class MidEllipsisConverter : IValueConverter
   {
       public object Convert(object value, ...)
       {
           var path = value as string ?? "";
           if (path.Length <= 60) return path;
           var parts = path.Split('\\');
           // 保留 "盘符:\" + 首个目录 + "…" + 最后两级
           return $@"{parts[0]}\{parts[1]}\…\{parts[^2]}\{parts[^1]}";
       }
   }
   ```

2. XAML 中 `NormalizedPath TextBlock` 改为：
   ```xml
   <TextBlock Text="{Binding DisplayPath, Converter={StaticResource MidEllipsis}}"
              ToolTip="{Binding DisplayPath}" FontSize="11" .../>
   ```

---

### ⚠️ F-12：多选时禁用单项行内按钮

**PRD 章节：** §5.7（"多选时行内单项按钮（Open/Reveal/Pin）禁用"）  
**当前状态：** `SelectionMode="Extended"` 已启用多选，但行内按钮未感知选择数量

**实现：**

在 `MainViewModel` 新增：
```csharp
[ObservableProperty] private bool _isMultiSelect = false;

// 在 MainWindow.xaml.cs 选择变化时更新
ItemsList.SelectionChanged += (s, e) =>
{
    _viewModel.IsMultiSelect = ItemsList.SelectedItems.Count > 1;
};
```

XAML 中行内按钮的 `IsEnabled` 绑定：
```xml
<Button IsEnabled="{Binding DataContext.IsMultiSelect, 
        RelativeSource={RelativeSource AncestorType=ListBox},
        Converter={StaticResource BoolNegate}}" .../>
```

---

### ⚠️ F-13：Open 操作后隐藏主窗口

**PRD 章节：** §3.2（操作流程末端"其他应用收到"暗示窗口应关闭）、§6.9  
**当前状态：** `RecentItemViewModel.Open()` 只调用 `FileActionService.OpenFile()`，主窗口不隐藏

**修复：** `RecentItemViewModel.Open()` 通知 MainWindow 隐藏（通过事件或 WeakReference 到 MainWindow）：
```csharp
// 方案：通过 MainViewModel 转发
[RelayCommand]
private void Open()
{
    FileActionService.OpenFile(Item.NormalizedPath);
    OpenRequested?.Invoke(); // 触发 MainWindow.Hide()
}
public event Action? OpenRequested;
```
或在 `MainWindow.xaml.cs` 中拦截 `OpenCommand` 的 `CanExecuteChanged` 后补充 `Hide()`。

---

## 四、P1 功能（首版不做，待二期）

以下功能 PRD 明确标注为 P1+，**不在当前版本范围内**，此处仅列出以免混淆优先级：

| 功能 | PRD 章节 | 说明 |
|---|---|---|
| 排序：Oldest first | §5.6 | P1，下拉中目前 Collapsed 正确 |
| 排序：Size largest | §5.6 | P1，同上 |
| 视图密度切换（Compact/Comfortable） | §5.6 | P1 |
| "Open With..." 菜单项 | §6.10.2 | P1 |
| "Hide from list" 菜单项 | §6.10.2 | P1 |
| "Remove from Recent" 菜单项 | §6.10.2 | P1 |
| Delete 键隐藏记录 | §6.13 | P1，"未实现时该键不绑定" |
| 开机自启 UI 开关 | §6.21 | P1（服务已有，Settings UI 接入） |
| Explorer Quick Access 解析 | §6.24 | P1 |
| TypedPaths 注册表补充 | §6.24 | P1 |
| 日志路径隐私哈希 | §12 | P1 |
| 全文搜索 | §4.2 | 明确不做 |
| 云同步 | §4.2 | 明确不做 |

---

## 五、今日实施计划（2026-05-02）

按"可独立 commit、阻塞少、收益高"原则分 4 个 Session 完成。

---

### Session 1：修复所有已知 Bug（约 1.5h）

完成后所有现有功能行为正确，可稳定运行。

| 步骤 | 文件 | 改动摘要 |
|---|---|---|
| 1.1 | `App.xaml.cs` | 修复 Bug-1：调整 `_tray` 初始化顺序，或 TrayService 新增 `SetMainWindow()` |
| 1.2 | `MainWindow.xaml.cs` | 修复 Bug-2：`Ctrl+Shift+C` 逻辑改用 `& ModifierKeys.Control` 而非 `==` |
| 1.3 | `StatusHintService.cs` | 修复 Bug-3：Ready/Watching 颜色改为 `#63C554` |
| 1.4 | `StatusHintService.cs` | 修复 Bug-4：补充 `AppStatus.Partial` switch case |
| 1.5 | `MainViewModel.cs` | 修复 Bug-5：`HasItems` 改为追踪过滤后视图 + 同步 `RefreshVisibleCount()` |
| 1.6 | `StatusHintService.cs` | 修复 Bug-5 关联：`UpdateCount` 接收过滤数而非总数 |
| 1.7 | `RecentItemViewModel.cs` | 修复 Bug-6：图标改为异步懒加载 |
| 1.8 | `MainViewModel.cs` + `HotkeyService.cs` | 修复 Bug-7：`ActiveLabel` 变更通知 |
| 1.9 | `MainWindow.xaml` | 修复 Bug-8：Sort 标签改为 "Newest first" |
| 1.10 | `MainWindow.xaml.cs` + `MainViewModel.cs` | 修复 Bug-9：Nav 与 Chip 拆分为独立属性 |

**验收：** 启动不崩溃；状态栏绿点；Ctrl+Shift+C 可复制文件名；搜索无结果时显示空状态面板。

---

### Session 2：P0 缺失功能（小型修复，约 1.5h）

| 步骤 | 功能 | 文件 | 改动摘要 |
|---|---|---|---|
| 2.1 | F-02 Favorites 显示 | `MainWindow.xaml` | 去掉 `Visibility="Collapsed"` |
| 2.2 | F-02 Favorites 过滤 | `MainViewModel.cs` | `FilterItem()` 补充 Favorites 导航分支 |
| 2.3 | F-04 空状态按钮 | `MainWindow.xaml` + `MainViewModel.cs` | 添加 Clear filters / Rebuild index 按钮 |
| 2.4 | F-05 状态栏过滤计数 | `MainViewModel.cs` | `RefreshVisibleCount()` 方法接入 |
| 2.5 | F-13 Open 后隐藏窗口 | `RecentItemViewModel.cs` + `MainWindow.xaml.cs` | Open 命令执行后 `mainWindow.Hide()` |
| 2.6 | F-11 路径中段省略 | 新建 `Converters/MidEllipsisConverter.cs` + `MainWindow.xaml` | 添加 Converter + Tooltip |

**验收：** Favorites 导航项可见可用；空状态有按钮；状态栏数量为过滤后数量；路径显示中段省略。

---

### Session 3：Settings 页面全实现（约 3.5h）

这是最大的单项工作，直接影响多个 P0 功能的可用性。

| 步骤 | 子任务 | 预估时间 |
|---|---|---|
| 3.1 | `SettingsWindow.xaml` 骨架：Tab 导航（General/Hotkey/Sources/List/Filters/Cache/About） | 20min |
| 3.2 | `SettingsViewModel.cs` 所有属性 + Commands + 构造注入服务 | 30min |
| 3.3 | General Tab UI + 逻辑（含 `StartupService` 接线） | 20min |
| 3.4 | Hotkey Tab UI + 录制逻辑（KeyCapture） + `HotkeyService.ReRegister()` | 40min |
| 3.5 | Sources Tab UI（已知文件夹列表 + 自定义 + 状态 Badge） | 40min |
| 3.6 | List / Filters Tab UI + 逻辑 | 20min |
| 3.7 | Cache Tab（Rebuild / Clear 按钮）+ About Tab（版本、路径、日志） | 20min |
| 3.8 | F-03 激活入口：侧边栏 Settings 导航 + 托盘菜单 Settings 项 | 15min |
| 3.9 | 单例 SettingsWindow 管理（`SettingsWindowSingleton`） | 10min |

**验收：** Settings 从侧边栏 / 托盘均可打开；General 开关实时生效；Sources 列表正确；Rebuild index 可用；About 路径按钮可打开目录。

---

### Session 4：Jump List L2 数据源（约 2h）

| 步骤 | 改动摘要 |
|---|---|
| 4.1 | `Recents.App.csproj` 添加 `OpenMcdf` NuGet 引用 |
| 4.2 | `ShellLinkResolver.cs` 添加 `ResolveFromBytes(byte[])` 重载 |
| 4.3 | `JumpListSource.cs` 完整实现：扫描 + OLECF 解析 + Watch + 错误处理 |
| 4.4 | `App.cs` `StartSources()` 中加入 `JumpListSource` |
| 4.5 | 测试：VS Code / Chrome / Edge 最近文件出现在列表 |

**验收：** IDE / 浏览器的最近文件（通过 Jump List）出现在 Recents 列表中；单文件解析失败不影响整体。

---

### Session 5（若有余量，约 1.5h）

按重要性选做：

**选项 A：Office MRU（F-08）**  
适合：用户有 Office 套件场景  
工作量：`OfficeMruSource.cs` 完整实现 + 定时 5 分钟轮询

**选项 B：ExistsProbeService 接入（F-06）**  
适合：提升离线 / 删除文件体验  
工作量：`ScrollChanged` + 批量异步探测 + 结果回写

**选项 C：响应式侧边栏（F-10）**  
适合：窗口缩放场景  
工作量：`OnRenderSizeChanged` + 侧边栏 DataTemplate 切换

---

## 六、改动文件速查表

| 文件 | Bug 修复 | P0 功能 | 新文件 |
|---|---|---|---|
| `App.xaml.cs` | Bug-1 | F-03 托盘 Settings | |
| `MainWindow.xaml` | Bug-8 | F-02/F-03/F-04/F-11 | |
| `MainWindow.xaml.cs` | Bug-2、Bug-9 | F-04/F-12/F-13 | |
| `MainViewModel.cs` | Bug-5、Bug-7、Bug-9 | F-02/F-04/F-05 | |
| `StatusHintService.cs` | Bug-3、Bug-4 | F-05 | |
| `HotkeyService.cs` | Bug-7 | F-01-B（ReRegister） | |
| `RecentItemViewModel.cs` | Bug-6 | F-13 | |
| `TrayService.cs` | Bug-1 | F-03 | |
| `SettingsWindow.xaml` | | F-01 | |
| `SettingsWindow.xaml.cs` | | F-01 | |
| `SettingsViewModel.cs` | | F-01 | |
| `SourcesViewModel.cs` | | F-01-C | |
| `JumpListSource.cs` | | F-07 | |
| `OfficeMruSource.cs` | | F-08 | |
| `OpenSavePidlMruSource.cs` | | F-09 | |
| `ShellLinkResolver.cs` | | F-07（ResolveFromBytes） | |
| | | | `Converters/MidEllipsisConverter.cs` |
| | | | `SettingsWindowSingleton.cs` |

---

## 七、验收检查清单（全量）

完成全部任务后，按以下清单逐项确认：

### 启动与基础
- [ ] 首次启动不崩溃，日志显示所有 Source 初始化成功
- [ ] 第二次启动（单实例）：激活已有窗口
- [ ] `--minimized` 参数启动：不显示主窗口，仅托盘常驻

### 主界面
- [ ] 标题栏显示 "Recents"，无 "RecentDock" 等旧名称
- [ ] 窗口 1040×760，最小 760×520，调整大小有效
- [ ] 搜索框呼出后自动聚焦
- [ ] 热键 Badge 显示实际注册的热键（非硬编码）
- [ ] 侧边栏：All / Favorites / Recent Folders / Documents / Images / Code 全部可见可用
- [ ] Settings 导航项（设置页实现后显示，无则不显示）
- [ ] Filter Chip 与侧边栏可叠加过滤
- [ ] 排序下拉默认"Newest first"，切换后立即生效
- [ ] 状态栏：绿点 Ready；黄点 Indexing/Partial；红点 Error
- [ ] 状态栏数量 = 当前过滤后可见数量
- [ ] 键盘提示随选中状态动态变化
- [ ] 空状态：显示"No recent files found"+ Clear filters + Rebuild index 按钮
- [ ] 无任何硬编码示例文件名/路径/时间

### 文件列表
- [ ] 图标异步加载，列表显示不阻塞
- [ ] .lnk 本体不出现在列表（Recent 目录的 .lnk）
- [ ] `bin\Debug`、`bin\Release`、`obj\` 目录文件不出现
- [ ] 文件行显示：时间（yyyy-MM-dd HH:mm）、大小、路径（中段省略）
- [ ] 路径 Tooltip 显示完整路径
- [ ] Missing 文件行灰显，Open/Drag 禁用
- [ ] Unknown 文件路径灰显，Tooltip 说明
- [ ] Hover 行内按钮显示
- [ ] Pin 图标：已收藏=蓝色，未收藏=灰色
- [ ] More 菜单包含：Open/Reveal/Copy Full Path/Copy File Name/Pin-Unpin

### 交互操作
- [ ] 双击文件：用默认程序打开 + 主窗口隐藏
- [ ] 双击文件夹：Explorer 打开
- [ ] 拖拽文件到微信/飞书：成功接收（FileDrop + CFSTR_SHELLIDLIST）
- [ ] 拖拽 Missing 文件：禁止（鼠标禁止图标）
- [ ] Ctrl+C：复制完整路径
- [ ] Ctrl+Shift+C：复制文件名
- [ ] Ctrl+O：定位到文件
- [ ] Ctrl+F：聚焦搜索框
- [ ] Enter：打开选中文件
- [ ] Esc：隐藏窗口
- [ ] ↑↓：列表导航

### 热键
- [ ] `Ctrl+Alt+R`：呼出/隐藏主窗口
- [ ] 注册失败时自动尝试候选链
- [ ] 所有候选失败：托盘气泡提示

### 托盘
- [ ] 托盘图标常驻
- [ ] 左键点击：显示/聚焦主窗口
- [ ] 菜单：Show / Settings（实现后）/ Rescan / Exit
- [ ] 退出：真正结束进程 + 释放热键
- [ ] 关闭按钮：隐藏到托盘（首次弹气泡提示）

### 设置页
- [ ] 从侧边栏 Settings 打开
- [ ] 从托盘菜单 Settings 打开
- [ ] 同一时间只有一个设置窗口
- [ ] General：Launch at startup / Hide on focus lost / Always on top / Close behavior
- [ ] Hotkey：录制新热键 + 重置
- [ ] Sources：已知文件夹 Toggle + 自定义路径增删
- [ ] List：Max items / Show missing / Show folders / Default sort
- [ ] Filters：排除扩展名/路径/关键词可增删
- [ ] Cache：Rebuild index / Clear icon cache 可用
- [ ] About：版本号正确；路径按钮可打开目录

### 数据源
- [ ] Downloads/Desktop/Documents/Pictures/Videos/Music 文件被索引
- [ ] 文件保存后 2 秒内出现在列表顶部
- [ ] Recent .lnk 解析的真实文件出现在列表
- [ ] Jump List（IDE/浏览器等应用）文件出现在列表
- [ ] 收藏状态跨重启保持
- [ ] Rebuild index：清空后重新扫描，状态栏切 Indexing 再切 Ready
