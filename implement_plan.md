未完成项目一览
🔴 违规项（需立即修正）
#	文件	问题	应为
A8b	DragDropService.cs:23	AddShellIdListArray 方法体为空	需实现 CFSTR_SHELLIDLIST 格式
—	App.xaml:7	BgSidebar #121214	#141821 （PRD §7.4）
—	App.xaml:11	AccentBlue #40C4FF	#3B82F6 （PRD §7.4）
已修好的 Stage A 项： Title/Size/ShowInTaskbar ✓、TrayService 菜单文字 ✓、文件夹保留逻辑 ✓、Hotkey badge 绑定 ✓

🟡 Settings 窗口（只有骨架）
SettingsWindow.xaml 存在但内容是 <Grid />，PRD §6.22 要求的 7 个分组（General / Hotkey / Sources / List / Filters / Cache / About）全部未实现。

🟡 数据源（6 个仍为 Stub）
源	类别	PRD 优先级
UserFolderWatchSource	L1 补充监控	P0
UncFolderWatchSource	L1 UNC 映射目录	P0
JumpListSource	L2	P1
OfficeMruSource	L2	P1
OpenSavePidlMruSource	L3	P1
CustomDestinationsSource	L2	P1
前两个（UserFolder / UNC）是 P0 —— KnownFolderWatchSource 目前只监控已知系统文件夹，用户手动添加目录和 UNC 路径还未生效。

🟡 UI 缺失项
项目	位置	说明
文件类型徽章	MainWindow.xaml 卡片模板	RecentItemViewModel.FileType 已有值，但未渲染为 badge
Recent Folders 过滤逻辑	MainViewModel	侧边栏 Nav 按钮存在，但切换时是否真正过滤为文件夹仅 (is_folder=1) 需验证
紧凑/标准密度切换	Settings + ListView	PRD §6.22 density toggle 未实现
Settings 窗口完整内容	SettingsWindow.xaml	见上