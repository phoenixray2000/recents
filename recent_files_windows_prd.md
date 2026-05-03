# Windows Recent Files Launcher PRD

## 0. 文档定位

本文档是本项目的唯一产品真理源。后续设计、编码、测试、重构均以本文档为准。

项目目标：开发一个 Windows 桌面效率工具，通过全局快捷键呼出一个轻量窗口，展示**最近真实文件**与最近文件夹，支持搜索、筛选、拖拽、右键处理、收藏固定、排除规则和常驻托盘。

目标用户：Windows 桌面重度用户，需要频繁把最近文件拖入微信、飞书、浏览器、Office、PS、CAD、IDE 或其他应用中打开、上传、编辑或处理。

核心判断：本工具优先解决"快速找到最近真实文件并交给其他应用处理"的场景。所谓"最近文件"以**文件系统真实变更事件**为唯一权威来源（参考 macOS Trickster 通过 FSEvents 自建索引的做法），不依赖任何单一系统列表。

不做：全文搜索、内容预览、插件系统、复杂自动化、云同步、登录账号。

---

## 1. 产品名称

Recents。

---

## 2. 技术栈约束

首版技术栈固定为：

- 语言：C#
- 运行时：.NET 8+ (含 .NET 10)
- UI：WPF
- 平台：Windows 10 1903+ / Windows 11
- 架构：单机本地应用，单实例
- 数据存储：本地 JSON 配置 + 本地 SQLite 索引 + 本地图标缓存
- 后台常驻：系统托盘
- 快捷键：Windows API RegisterHotKey
- 文件监听：FileSystemWatcher
- 部署形态：独立 EXE（非 MSIX，避免沙盒访问受限）

不使用 Electron、Tauri、WebView 作为主 UI。

---

## 3. 核心用户场景

### 3.1 快速上传最近文件

用户刚保存或下载了一个文件，需要发到微信、飞书、邮件、网页表单或客户系统。

操作流程：
1. 按全局快捷键呼出窗口。
2. 最近文件列表默认按最近时间倒序显示。
3. 用户拖拽目标文件到其他应用。
4. 其他应用收到真实文件路径。

验收标准：拖拽传递的是原始文件路径，不是 .lnk 快捷方式。**刚下载完成的文件应在 2 秒内出现在列表顶部。**

### 3.2 快速用指定应用打开文件

用户需要把最近文件交给特定应用处理，例如用 Photoshop 打开图片，用 Excel 打开表格，用 VS Code 打开脚本。

操作流程：
1. 呼出窗口。
2. 右键目标文件。
3. 选择"打开方式"。
4. 系统弹出 Windows 原生打开方式选择器。

验收标准：打开方式作用于原始文件，不作用于 .lnk。

### 3.3 快速定位文件所在目录

用户知道刚处理过某个文件，但忘记路径。

操作流程：
1. 呼出窗口。
2. 搜索文件名或扩展名。
3. 右键选择"打开所在位置"。
4. Explorer 打开原文件所在目录并选中文件。

### 3.4 通过键盘快速打开文件

用户偏键盘操作。

操作流程：
1. 呼出窗口后自动聚焦搜索框。
2. 输入关键词。
3. 上下方向键选择。
4. Enter 打开文件。
5. Esc 隐藏窗口。

### 3.5 处理网络共享盘上的文件

用户公司有一台 NAS 或文件服务器（如 `\\10.0.0.100\claw\`），希望最近写入的素材也出现在 Recents 列表中。

操作流程：
1. 用户在设置 → Sources 中添加 `\\10.0.0.100\claw\` 为映射监听目录。
2. 工具开始监听该 UNC 路径。
3. 该路径下的新文件、被修改的文件按时间进入主列表。

验收标准：UNC 路径在网络可达时，文件能被索引；网络断开时，已索引的文件标记为 Unknown 状态，不阻塞 UI。

---

## 4. 首版功能范围

### 4.1 必须实现

- 全局快捷键呼出 / 隐藏
- 系统托盘常驻 + 单实例
- 开机自启开关
- **多源最近文件采集**（见 §6.3）
- 解析 .lnk 快捷方式为真实文件路径
- 最近文件列表展示
- 最近文件夹列表展示
- 文件存在性校验（异步、超时控制）
- 最近优先排序
- 搜索过滤
- 文件类型筛选
- 拖拽原始文件到其他 App
- 双击打开原始文件
- 右键基础菜单
- 图标显示（异步、缓存）
- 快速预览信息（元信息 Tooltip）
- **空格键快速预览**（WebView2，见 §6.25）
- 键盘操作
- 排除规则（扩展名、路径、关键词）
- 黑名单 / 白名单路径
- 自定义监听目录（含 UNC 路径，例 `\\10.0.0.100\claw\`）
- 最近文件数量限制
- 缓存机制（SQLite）
- 设置页
- 收藏文件 / 固定文件
- 高保真主界面控件全部功能化（见 §5.0；未实现的按钮 / 图标不得显示）

### 4.2 首版不做

- 全文内容搜索（索引 + 全文检索）
- OCR
- Office / PSD / CAD / 压缩包等非 WebView2 原生格式的内容预览
- 插件系统
- **云同步 / OneDrive / SharePoint 集成**
- 多设备同步
- 用户登录
- 文件版本管理
- 复杂自动化工作流
- 内置文件编辑器
- 浏览器历史 / 下载库 SQLite 解析
- UWP / MSIX 沙盒应用打开记录

明确说明：本工具**不做云盘**。OneDrive、Google Drive 等若已挂载为本地目录，可由用户**手动**添加为监听源；工具对其中的占位符（cloud-only）文件**不做特殊处理**，仅按本地文件对待，且不会触发联网下载（见 §6.19）。

---

## 5. 信息架构与主界面设计

本节按最新高保真界面图更新，是主界面实现的唯一依据。生成图中的视觉风格可参考，但产品文本统一使用 **Recents**；不得把示例图里的 `RecentDock`、示例文件名、示例路径硬编码进程序。

### 5.0 UI 功能完整性原则

任何出现在界面上的文字、图标、按钮、徽标、状态条、快捷键提示都必须满足以下规则：

- **有明确含义**：用户能从 Tooltip、状态栏或上下文菜单理解其作用。
- **有真实数据**：文件行、数量、路径、时间、大小、热键、状态均来自运行时数据。
- **有可执行动作**：按钮和菜单项必须绑定真实命令；暂未实现的功能不得显示。
- **有禁用原因**：因 Missing、Unknown、权限不足等无法执行的动作可以禁用，但必须有 Tooltip 说明。
- **无装饰性假图标**：导航图标、文件图标、操作图标均须对应真实视图、文件类型、命令或状态。
- **无运行期硬编码样例**：运行版本中不得出现 `recent_files_windows_prd.md`、`Antigravity.exe`、`SalesContract.docx` 等示例文件名 / 路径 / 时间。开发期 Mock 数据允许，**ship 前必须清空**。
- **图标只能来自系统真实图标**（`SHGetFileInfo` / `IShellItemImageFactory`）+ 项目内通用占位图标 + Segoe Fluent Icons 矢量图。任何二次重绘、AI 头像、风格化图标、按文件名 / 类型自动生成的卡通图标均不允许（详见 §7.6）。
- **DPI / 显示器变化必须即时响应**：切换窗口大小、显示器、DPI 后，所有元素按 §7.2 / §7.6 即时缩放，不得出现错位、模糊或裁剪。
- **未启用 ≠ 灰色禁用**：P0 阶段未实现的功能必须**完全不显示**对应的导航项 / 按钮 / 菜单项 / 排序选项，而不是显示一个灰色禁用控件。

### 5.1 主窗口结构

主窗口采用暗色、圆角、双栏布局：

```text
┌─────────────────────────────────────────────────────────┬──────────────┐
│ [AppIcon] Recents                           —  □  ×     │              │
├─────────────────────────────────────────────────────────┤  FAVORITES   │
│ Search recent files...                      Alt+Shift+Z │              │
├─────────────────────────────────────────────────────────┤ [icon] file  │
│ [All] [Docs] [Images] [Code] [Folders]    Newest first  │        time  │
├─────────────────────────────────────────────────────────┤ [icon] file  │
│ [icon] filename.ext                  [Open][Reveal][Pin]│        time  │
│        time · size · path                      [...]    │              │
│ [icon] filename.ext                  [Open][Reveal][Pin]│              │
│        time · size · path                      [...]    │              │
├─────────────────────────────────────────────────────────┤              │
│ ● Ready | 128 items              ↑↓ Navigate Enter Open │              │
└─────────────────────────────────────────────────────────┴──────────────┘
```

主窗口区域：

| 区域 | 必须显示内容 | 功能要求 |
|---|---|---|
| 标题栏 | App 图标、`Recents`、设置按钮、收藏开关、窗口控制按钮 | 设置按钮打开配置；收藏开关控制右侧抽屉 |
| 搜索区 | 搜索框、当前全局快捷键 Badge | 搜索框自动聚焦；显示实际热键 |
| 筛选与排序区 | 文件类型 Chip、排序下拉 | 立即筛选/排序当前列表 |
| 文件列表区 | 最近文件 / 文件夹 | 使用共享模板，支持拖拽和右键菜单 |
| 收藏抽屉 (Drawer) | 收藏的文件和文件夹 | **右侧展开**，显示独立持久化的收藏清单。当抽屉打开时，窗口宽度自动增加 280px，以确保主内容区域布局不被挤压（Preserve dimensions）。 |
| 底部状态栏 | 状态、可见数量、键盘提示 | 动态更新 |

### 5.2 标题栏

标题栏内容：

- 左侧：App 图标 + `Recents` + 设置按钮。
- 右侧：收藏开关（Drawer 开关）、最小化、最大化 / 还原、关闭。

要求：
- 标题栏支持拖动窗口，使用 `WindowChrome.CaptionHeight` + `IsHitTestVisibleInChrome` 配合：搜索框、热键 Badge、窗口控制按钮、行内按钮的命中区域必须屏蔽拖动。
- 关闭按钮默认隐藏到托盘，不退出进程。
- **第一次按关闭时**弹一次托盘气泡：`Recents 仍在托盘运行。可在设置中改为按关闭即退出。`，并写一条 `closed_to_tray_notice_shown=true` 到 settings.json，避免重复弹。
- 真正退出只能通过托盘菜单 `Exit` 或设置中的退出动作。
- 标题栏不得放置无功能的装饰按钮。
- 标题文本必须是 `Recents`。**不允许**出现 `RecentDock` 或其他旧名称（包括 `Title="..."` 在 XAML 里也不允许）。

### 5.3 搜索区

搜索框占据主内容顶部，视觉上是圆角输入框。

默认占位文案：

```text
Search recent files...
```

要求：
- 窗口呼出后自动聚焦搜索框。
- 输入后实时过滤文件名、扩展名、路径。
- 输入为空时显示当前视图的最近列表。
- 搜索框左侧放大镜图标仅表示搜索语义，不可点击；如果要可点击，点击后必须聚焦输入框。
- 搜索框右侧显示快捷键 Badge，**Badge 内容必须来自 `HotkeyService.ActiveLabel`**（不允许 XAML 内硬编码 `Ctrl + Alt + R`）。
- 如果热键注册失败，Badge 显示 `Hotkey unavailable`，点击打开 Hotkey 设置（设置页未实现时弹托盘气泡说明）。
- 有输入内容时显示清除按钮 `×`；点击后清空搜索并恢复列表。

### 5.4 左侧导航栏

左侧导航栏固定显示以下导航项；**未启用的项不得显示**（不显示 ≠ 灰色禁用）：

| 项目 | 图标语义 | 作用 | 启用阶段 |
|---|---|---|---|
| All Files | 层叠 / 列表 | 显示所有最近文件 | P0 |
| Favorites | 星标 | 显示用户固定的文件和文件夹 | P0 |
| Recent Folders | 文件夹 | 显示最近文件夹 | P0 |
| Settings | 齿轮 | 打开设置页 | **仅当设置页已实现时显示**；P0 不显示 |

要求：
- 当前选中项使用蓝色左侧指示条 + 深色圆角背景。
- 每个导航项必须有 Tooltip，文本与上表一致。
- `Favorites` 若无收藏，点击后显示空状态，不隐藏导航项。
- 原 Documents、Images、Code 导航项已移除，统一由主列表上方的筛选 Chip 提供。

### 5.5 顶部筛选 Chip

在主列表顶部显示轻量筛选 Chip：

```text
All Files | Docs | Images | Code | Folders
```

作用：在当前导航视图内做二次快速筛选。

要求：
- `All Files`：显示当前视图下的所有文件（排除文件夹）。
- `Docs` / `Images` / `Code`：筛选对应分类的文件。
- `Folders`：强制显示所有文件夹，不受侧边栏导航限制（除 Favorites 需满足收藏状态外）。
- **优先级**：Chip 筛选具有最高优先级。当 Chip 非 `All Files` 时，侧边栏导航（All Files / Recent Folders）的过滤规则被覆盖；当 Chip 为 `All Files` 时，遵循侧边栏规则（如 sidebar All 只显示文件，sidebar Folders 只显示文件夹）。
- `Favorites` 侧边栏与 Chip 筛选为叠加关系：选中 Favorites 侧边栏后再点 Docs Chip，仅显示收藏夹里的文档。
- 没有匹配结果时显示空状态。

### 5.6 排序与视图密度

右上角排序下拉默认显示：

```text
Newest first
```

排序选项（**未实现的不得出现在下拉中**）：

| 排序 | 含义 | 阶段 |
|---|---|---|
| Newest first | 按 `RecentTime` 倒序 | P0 |
| Name A-Z | 按 `DisplayName` 升序 | P0 |
| Oldest first | 按 `RecentTime` 正序 | P1 |
| Size largest | 按 `SizeBytes` 倒序，Unknown size 排最后 | P1 |

排序要求：
- 切换后立即更新列表。
- 当前排序写入配置。
- 未实现的排序不允许出现在下拉中。

**平滑滚动优化**：
- 列表支持平滑滚动，灵敏度经过优化（0.4x 灵敏度），提供丝滑的扫视体验。

视图密度（Standard / Compact）切换：采用 ▭ (U+25AD) 和 ═ (U+2550) 图标，放置在主界面筛选区右侧 "Newest first" 排序按钮的左侧。点击立即生效，状态写入配置。

### 5.7 文件列表项

文件列表项采用单行卡片式布局，包含：

```text
[FileIcon] DisplayName                         [Open] [Reveal] [Pin] [More]
           RecentTime · Size · NormalizedPath
```

字段要求：

| 字段 | 显示规则 |
|---|---|
| FileIcon | 优先显示系统图标 / 缓存图标；加载前显示通用占位图标 |
| DisplayName | 粗体；长文件名尾部省略；必须来自真实路径 |
| RecentTime | 格式 `yyyy-MM-dd HH:mm`；来自融合后的 `RecentTime` |
| Size | 文件显示大小；文件夹显示 `Folder`；Unknown 显示 `Unknown size` |
| NormalizedPath | 中段省略；Tooltip 显示完整路径 |
| Source 状态 | 默认不在行内显示；Tooltip 展开来源列表 |

行内快捷按钮：

| 图标 | Tooltip | 命令 | 禁用条件 |
|---|---|---|---|
| 打开箭头 | Open | 默认程序打开目标路径 | Missing / Unknown 且不可达 |
| 文件夹 | Reveal in Explorer | Explorer 选中原文件或打开文件夹 | Missing |
| 图钉 | Pin / Unpin favorite | 切换收藏状态 | 无 |
| 三点 | More actions | 打开右键菜单 | 无 |

多选交互：

- `Ctrl + 点击`：切换单行选择。
- `Shift + 点击`：选择从锚点到当前行的连续区间。
- 多选时行内单项按钮（Open / Reveal / Pin）禁用，批量操作通过右键菜单或拖拽完成。
- 多选状态下三点 More 仍可用，菜单内容自动切换为多选语义（如"打开全部 N 个"、"复制 N 条路径"）。

要求：
- 行内按钮可常显或悬停显示；若常显，视觉权重必须低于文件名。
- 窄窗口下按 §7.2 隐藏 Reveal 与 Pin 时，三点 More **必须始终包含** Reveal 与 Pin / Unpin。
- **收藏独立逻辑**：收藏夹逻辑与主列表解耦。收藏后的条目将完整元数据保存到独立表中，即使主列表条目被修剪（Prune）或暂时从 DB 移除，收藏项依然保留。

### 5.8 选中、悬停与文件状态

状态样式：

| 状态 | UI 表现 |
|---|---|
| Hover | 行背景略亮，显示行内快捷按钮 |
| Selected | 蓝色描边 + 深蓝半透明背景 |
| Favorite | Pin 图标蓝色 |
| Missing | 整行灰显，Open / Drag 禁用 |
| Unknown | 路径 / 大小显示 Unknown，行略灰；Tooltip 说明未确认可达 |
| Offline UNC | 行略灰；Tooltip 显示 `Network source disconnected` |

要求：
- Missing 与 Unknown 不得混用。
- 选中态不改变列表排序。
- 文件状态变化后必须刷新当前行，不重建整个列表。

### 5.9 底部状态栏

底部状态栏左侧显示索引状态和当前可见数量：

```text
● Ready | 128 items
```

状态文本：

| 状态 | 文案 | 触发条件 |
|---|---|---|
| Ready | `Ready` | 所有启用数据源无阻塞错误 |
| Indexing | `Indexing...` | 后台扫描或重建索引中 |
| Watching | `Watching sources` | FileSystemWatcher 正常运行 |
| Partial | `Some sources unavailable` | 某些源失败或 UNC 断开 |
| Error | `Action failed` | 最近一次用户动作失败 |

要求：
- 绿色点表示 Ready / Watching。
- 黄色点表示 Partial / Indexing。
- 红色点表示 Error。
- `128 items` 必须是当前过滤后可见数量，不是数据库总量。

底部状态栏右侧显示键盘提示：

```text
↑↓ Navigate   Enter Open   Drag to share
```

要求：
- 键盘提示必须与当前焦点上下文一致。
- 没有选中文件时不显示 `Enter Open`。
- 当前项不可拖拽时不显示 `Drag to share`。
- 文案不得写死；由 `StatusHintService` 根据状态输出。

### 5.10 空状态、加载态、错误态

空状态：

```text
No recent files found
Try changing filters or rebuilding the index.
```

空状态包含两个快速操作按钮：
- `Clear filters`: 清除搜索框、筛选 Chip 和侧边栏分类，恢复全量列表。
- `Rebuild index`: 触发 SQLite 索引重扫。

加载态：

```text
Indexing recent files...
```

错误态示例：

```text
Some sources are unavailable
Open Settings to review source status.
```

要求：
- 空状态按钮最多两个：`Clear filters`、`Rebuild index`。`Rebuild index` 点击后**无需确认对话框**，直接后台执行并把状态栏切到 Indexing。
- 错误态必须可点击进入 Settings → Sources（设置页未启用时降级为 Tooltip 列出具体故障源）。
- 不得用无意义插画占据主要空间。

### 5.11 主界面功能验收标准

- 界面中所有示例文字必须替换为真实数据或本节规定文案。
- 界面中所有图标必须有 Tooltip。
- 点击任一可见按钮必须产生正确动作或明确错误提示。
- 任何未实现功能不得出现在主界面。
- 任何禁用按钮必须说明原因。
- **`.lnk` 不展示是系统硬规则**：`%APPDATA%\Microsoft\Windows\Recent` 下的 `.lnk` 仅作为解析来源，不直接进入文件列表；只有当用户索引到的**真实文件本身**就是 `.lnk` 文件（例如桌面上的某个 `xxx.lnk` 用户当文档使用），才作为文件项展示。该规则**不能被用户黑名单 / 白名单 / 排除扩展名规则关闭**（与 §6.14 用户排除规则的优先级对比见 §6.14）。
- **项目自身的 `bin\Debug` / `bin\Release` / `obj\` 输出文件**默认排除，不允许出现在列表中。

---

## 6. 核心功能详细要求

### 6.1 全局快捷键

默认快捷键：`Alt + Shift + Z`。

要求：
- 支持用户在设置页修改快捷键。
- 注册失败时自动尝试候选序列：`Win + ;` → `Ctrl + Shift + Space` → `Ctrl + Alt + Space`，最终失败在托盘气泡和设置页提示。
- 按快捷键时，如果窗口未显示，则显示并聚焦搜索框。
- 按快捷键时，如果窗口已显示，则隐藏窗口。
- 窗口显示位置默认在当前鼠标所在屏幕中央。
- `HotkeyService.ActiveLabel` 必须暴露为可绑定属性，供 §5.3 搜索框 Badge 使用。

实现建议：使用 Windows API `RegisterHotKey`。封装为 `HotkeyService`。

### 6.2 托盘常驻

要求：
- 应用启动后常驻系统托盘。
- 关闭主窗口时默认隐藏到托盘，不退出程序。
- 托盘菜单包含：显示窗口、设置（仅当设置页已实现时显示）、重新扫描、开机自启、退出。
- 退出必须真正结束进程并释放全局热键。
- 托盘菜单文案首版统一英文：`Show / Settings / Rescan / Launch at startup / Exit`；中文环境下用户可在设置中切换。
- **未实现的菜单项不得显示**，禁止出现 `Settings (待实现)` 这种占位文本。

### 6.3 最近文件来源（多源融合，**核心改动**）

本工具不依赖单一系统列表。所有数据源统一抽象为 `IRecentSource`，由 `RecentIndexService` 融合去重，写入本地 SQLite 索引（`%LOCALAPPDATA%\Recents\index.db`）。

#### 6.3.1 数据源分层

| 层级 | 数据源 | 主要覆盖 | 实时性 | 必选 |
|---|---|---|---|---|
| L1 | **直接文件系统监听**（已知文件夹 + 用户自定义目录） | 下载、保存、生成、复制等所有写入 | 实时 | ✅ 必选 |
| L1 | `%APPDATA%\Microsoft\Windows\Recent` 中的 .lnk | Explorer / Office 显式"打开"信号 | 准实时 | ✅ 必选 |
| L2 | **Office MRU 注册表** `HKCU\Software\Microsoft\Office\*\User MRU` | Office 文件打开历史 | 写入即变 | 可选（首版做） |
| L3 | **`OpenSavePidlMRU` 注册表** `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU` | 通用文件对话框历史 | 写入即变 | 可选（首版做） |

L1 是数据底座，L2/L3 用来补充"用户操作意图"信号。每条最终 RecentItem 的 `RecentTime` 取所有命中来源里 `max(LastWriteTime, ExplicitOpenTime)`，并附带 `SourceKinds`（位掩码）用于调试与"从来源 X 隐藏"功能。Jump List / AutomaticDestinations / CustomDestinations 不作为数据源：其跨应用 MRU 噪声过高，容易把低相关条目推到列表顶部。

#### 6.3.2 L1：默认监听目录

启动时通过 `SHGetKnownFolderPath` 解析以下已知文件夹，递归监听：

| KNOWNFOLDERID | 默认含义 |
|---|---|
| `FOLDERID_Downloads` | 下载 |
| `FOLDERID_Desktop` | 桌面 |
| `FOLDERID_Documents` | 文档 |
| `FOLDERID_Pictures` | 图片 |
| `FOLDERID_Videos` | 视频 |
| `FOLDERID_Music` | 音乐 |

> 必须用 `SHGetKnownFolderPath`，不得硬编码 `C:\Users\<x>\Downloads`。组策略、用户重定向、OneDrive Known Folder Move 都会改写真实路径。

启动时增量扫描（只采近 `RecentLookbackDays` 天内 `LastWriteTime`，默认 30 天），运行期由 `FileSystemWatcher` 推送变更。

#### 6.3.3 L1：用户自定义监听目录（含 UNC）

用户可在 **Settings → Sources** 中追加自定义监听路径，包括：

- 本地路径，如 `D:\Projects\`
- 网络共享 / 映射路径，如 `\\10.0.0.100\claw\`
- 已挂载的网络盘符，如 `Z:\share\`

要求：
- 每个自定义来源独立可启用 / 禁用。
- 添加 UNC 路径时，先做一次可达性测试（带 3s 超时）；不可达只警告，不拒绝添加。
- UNC 路径在网络断开时，对应的 `FileSystemWatcher` 会失效，必须监听 `Error` 事件并自动重试（指数回退，最长间隔 5 分钟）。
- UNC 来源建议默认 `RecentLookbackDays = 7`，避免初次扫描拉取过多远端文件。
- 不对 UNC 路径上的文件读取大小、缩略图，全部走"懒加载"——只在文件进入可视区域时才查 `FileInfo`。

> **不做云盘集成**：OneDrive、Google Drive、Dropbox 客户端如果在本地把目录挂载为普通文件夹，用户可像普通本地路径一样手动添加监听；本工具不识别其登录态、不读取占位符元数据、不触发联网下载（见 §6.19 OneDrive 占位符处理）。

#### 6.3.4 L1：Recent 文件夹

继续读取 `%APPDATA%\Microsoft\Windows\Recent` 下的 .lnk，作为"显式打开"信号融合进索引。.lnk 解析见 §6.4。

**列表只展示 .lnk 解析出的目标真实文件**；.lnk 本体不进入列表（§5.11）。

#### 6.3.5 [Deleted]

Jump List / AutomaticDestinations / CustomDestinations 不作为数据源：其跨应用 MRU 噪声过高，容易把低相关条目推到列表顶部。首版明确不予支持。

#### 6.3.6 L2/L3：注册表 MRU

读取以下键，每个 value 是一条历史记录：

```text
HKCU\Software\Microsoft\Office\<ver>\<App>\User MRU\<sid>\File MRU
HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU\*
HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs
```

- Office MRU 是带时间戳的字符串，可直接拿到打开时间。
- `OpenSavePidlMRU` 内是 PIDL，需要 `SHGetPathFromIDList` 解析。
- `RecentDocs` 仅作降级补充。

注册表读取**只读**，不做监听（注册表 `RegNotifyChangeKeyValue` 在 WPF 中代价不低），改为定时（5 分钟）轮询 + 启动时刷新。

#### 6.3.7 数据源融合规则

- 主键：`NormalizedPath`（统一为长路径、大写盘符、去除尾随分隔符；UNC 保留 `\\host\share\` 前缀）。
- 同 key 多来源：`RecentTime = max(各来源时间)`；`SourceKinds |= 各来源标志位`。
- 文件夹来源：L1 监听目录中的所有目标路径所在父目录会被聚合到 Recent Folders；.lnk 直指文件夹时也进入 Recent Folders。
- 删除：仅当某来源**显式**报告删除（FileSystemWatcher.Deleted）才从索引移除；其他来源消失只移除该 SourceKind 标志，不删条目。
- **文件夹条目必须保留**：融合时不允许把 `IsFolder=true` 的条目剔除，它们用于 Recent Folders 视图（§6.24）。
- **保护用户态**：后台扫描触发的更新使用 `UpsertDiscovery` 策略，仅更新文件元数据（大小、时间、来源掩码），**严禁覆盖**用户手动设置的 `is_favorite`、`favorite_time`、`is_hidden` 等状态位。同时，通过位或运算（OR）合并 `source_kinds`。

#### 6.3.8 重新扫描

- 用户在托盘菜单或设置页可触发"Rebuild Index"，**无需二次确认**，直接重置 SQLite 后并发跑所有数据源，状态栏切到 `Indexing...`。
- 用户在设置页修改数据源开关、添加或删除来源时，只重启数据源监听 / 后台扫描，不清空 SQLite 和当前 UI 列表；避免来源配置变更导致列表周期性清空和重绘。
- 每条来源失败不影响其他来源。

### 6.4 .lnk 解析

要求：
- 展示原始文件，不展示 .lnk。
- 所有打开、拖拽、右键菜单均作用于原始文件。
- 缓存 .lnk 路径、目标路径、目标参数、工作目录、最近时间。
- 如果目标是文件夹，归入 Recent Folders。
- 如果目标不存在，标记 Missing。

实现：
- **首选**：纯托管解析 MS-SHLLINK 二进制格式（NuGet `Securify.ShellLink` / `securifybv.ShellLink` 或同类库）。无 COM 依赖、可在线程池并发批量解析、不要求 STA。
- **备选**：P/Invoke `IShellLinkW + IPersistFile`。
- **不使用** `WSH IWshRuntimeLibrary`（性能差、STA 限制、对损坏的 .lnk 容错差）。

性能目标：1000 个 .lnk 在 ThreadPool 上并发解析 ≤ 1.5 秒。

### 6.5 最近优先排序

默认排序：`RecentTime` 倒序，`RecentTime = max(L1 LastWriteTime, L1 CreationTime, .lnk LastWriteTime, MRU 时间)`。

备选排序（首版仅 P0：Newest first / Name A-Z；其余见 §5.6）：
- 原文件 LastWriteTime（P1）
- 文件名 A-Z（P0）
- 文件类型（不在首版排序中暴露，仅作筛选）
- 文件大小（P1）

### 6.6 搜索过滤

搜索范围：
- 文件名
- 扩展名
- 原始路径

搜索语法（首版固定，不暴露给用户配置）：

| 输入示例 | 解释 |
|---|---|
| `report` | 文件名 / 路径子串模糊匹配（不区分大小写） |
| `.docx` 或 `pdf` 当首字符为 `.` | 扩展名精确匹配 |
| `D:\work` 或 含 `\` `/` | 路径片段匹配 |
| `claw report` | 多 token AND |

性能要求：
- 1 万条以内输入无明显卡顿。
- 搜索在内存索引上执行，不重新扫描磁盘。
- 排序与过滤后渲染走 UI 虚拟化（`VirtualizingStackPanel`）。

### 6.7 文件类型筛选

类型分类（可在设置页修改）：

- Documents：.doc, .docx, .xls, .xlsx, .ppt, .pptx, .pdf, .txt, .md, .rtf
- Images：.png, .jpg, .jpeg, .gif, .bmp, .webp, .svg, .heic
- Videos：.mp4, .mov, .avi, .mkv, .wmv, .webm
- Audio：.mp3, .wav, .flac, .aac, .m4a
- Archives：.zip, .rar, .7z, .tar, .gz
- Code：.py, .js, .ts, .tsx, .cs, .cpp, .c, .h, .java, .go, .rs, .json, .xml, .yaml, .yml, .html, .css, .sql
- Other：未归类

要求：
- 左侧导航支持按类型筛选。
- 搜索与类型筛选可叠加。
- **每条 RecentItem 在写入索引前必须按上表分类**，不允许 `FileType="Other"` 硬编码（除非真不属于任一类）。分类逻辑由 `FileTypeClassifier` 统一实现。

### 6.8 拖拽原文件

要求：
- 拖拽时 `DataObject` **同时**附 `DataFormats.FileDrop`（`string[]`）和 `Shell IDList Array`（`CFSTR_SHELLIDLIST`），保证微信、飞书、Outlook、浏览器、资源管理器全部兼容。
- 支持多选拖拽多个文件。
- Missing 文件禁止拖拽（拖拽前判定，禁拖时鼠标显示禁止图标）。
- 文件夹也支持拖拽。
- UNC 路径文件可拖拽，由目标 App 自行处理远程访问。

实现建议：封装 `DragDropService`，验收必须覆盖：微信、飞书、Outlook、Edge、Explorer 五个目标。

### 6.9 双击打开

要求：
- 双击文件项，用系统默认程序打开原始文件。
- Missing 文件双击时提示文件不存在。
- 打开失败时展示错误提示。

实现：`ProcessStartInfo { UseShellExecute = true }`。

### 6.10 右键 / More 菜单

文件右键菜单分为「基础项」（P0 必做）和「扩展项」（P1+）两组。三点 More 按钮在 P0 阶段**只显示基础项**。

#### 6.10.1 基础项（P0 必做）

- Open
- Reveal in Explorer
- Copy full path
- Copy file name
- Pin / Unpin

#### 6.10.2 扩展项（P1+，未实现时不显示）

- Open With...
- Hide from list
- Remove Once

说明：
- "Hide from list"写入隐藏规则（`hidden_paths`），不删除原文件，不影响其他用户。
- "Remove Once"只删 `%APPDATA%\Microsoft\Windows\Recent` 中对应的 .lnk；**不动**原文件，不动 Office MRU / 注册表 MRU（这些由各应用或系统自己管理，强删风险高）。
- "Open With..."必须作用于原始文件。

打开方式实现（P1）：优先 `SHOpenWithDialog`（Win10/11 现代选择器），失败回退到：

```text
rundll32.exe shell32.dll,OpenAs_RunDLL "原始文件路径"
```

打开所在位置实现：

```text
explorer.exe /select,"原始文件路径"
```

### 6.11 图标显示

要求：
- 占位图标：用 `SHGetFileInfo` + `SHGFI_USEFILEATTRIBUTES`（不访问磁盘）按扩展名取通用图标，立即填入 UI。
- 真实图标：异步用 `IShellItemImageFactory.GetImage` 拉取（支持高分辨率缩略图）。
- 图标按 `扩展名 + isFolder + DPI` 缓存到 `%LOCALAPPDATA%\Recents\icons\`。
- UI 永远不被图标加载阻塞。
- UNC 路径文件不预拉取真实图标，仅用占位图标，可视进入再异步拉取，超时 3s 放弃。
- Missing 文件使用灰色默认图标。
- **图标来源严格遵守 §7.6 硬规则**：不允许任何二次重绘 / AI 头像 / 风格化图标。

### 6.12 快速预览信息

首版只展示元信息，不做内容预览。

展示字段：
- 文件名
- 原始路径
- 文件大小
- 文件类型
- 最近时间
- 原文件修改时间
- 文件存在状态
- 数据来源（小图标，悬停展开多个来源）

可在鼠标悬停 Tooltip 或右侧详情面板展示。

### 6.13 键盘操作

必须支持：

- `Alt+Shift+Z`：呼出 / 隐藏（默认值，可改）
- `Esc`：隐藏窗口（预览打开时仅关闭预览，再按才隐藏主窗口）
- `Enter`：打开当前选中文件
- `↑ / ↓`：移动选择（预览打开时同时刷新预览）
- `Space`：切换当前选中文件的预览窗口（见 §6.25）
- `Ctrl + C`：复制原始文件完整路径
- `Ctrl + Shift + C`：复制文件名
- `Ctrl + O`：打开所在位置
- `Ctrl + F`：聚焦搜索框

P0 阶段必须实现：`Esc / Enter / ↑↓ / Space / Ctrl+C / Ctrl+Shift+C / Ctrl+O / Ctrl+F`。

### 6.14 排除规则

排除规则包括：

- 排除扩展名
- 排除路径
- 排除文件名关键词
- 隐藏不存在文件
- 隐藏系统路径
- 隐藏临时文件

默认排除扩展名建议：

```text
.tmp, .temp, .lnk, .url, .ini, .log, .crdownload, .partial, .part, .opdownload, .!ut
```

默认排除路径关键词建议：

```text
AppData\Local\Temp
Windows\Temp
$Recycle.Bin
node_modules
.git
__pycache__
bin\Debug
bin\Release
obj\Debug
obj\Release
```

要求：
- 默认排除规则在首次运行时写入 settings.json，用户可后续修改。
- **§5.11 ".lnk 不展示" 是系统硬规则，优先级高于本节用户规则**：用户即便从黑名单删除 `.lnk`，列表中也不会出现来自 `%APPDATA%\Microsoft\Windows\Recent` 的 .lnk（其作为解析来源照常工作）。
- 项目自身的 `bin\Debug` / `bin\Release` / `obj\` 默认排除，避免开发期把构建产物索引进列表。

### 6.15 黑名单路径

- 命中黑名单路径前缀的文件不展示。
- 支持精确路径和路径前缀。
- 支持用户在设置中添加、删除。

### 6.16 白名单路径

- 如果白名单为空，不限制来源。
- 如果白名单非空，只展示命中白名单前缀的文件。
- 白名单优先级：高于普通分类，低于"文件存在性 / Missing 显示开关"。

> 注意：白名单是**展示过滤器**，不是数据源。要让一个目录被采集，必须在 **Sources** 中添加（§6.3.3）。

### 6.17 最近文件数量限制

默认显示：200 条。

设置可选：100 / 200 / 500 / 1000 / Unlimited（不建议）。

### 6.18 缓存与索引

存储分两层：
- **索引**：SQLite，`%LOCALAPPDATA%\Recents\index.db`
- **配置**：JSON，`%APPDATA%\Recents\settings.json`
- **图标缓存**：`%LOCALAPPDATA%\Recents\icons\`
- **日志**：`%LOCALAPPDATA%\Recents\logs\recents.log`

启动流程：
1. 读取 `settings.json`。
2. 打开 `index.db`（如损坏自动备份并重建）。
3. UI 立即用 SQLite 中已有数据渲染。
4. 后台并发跑各 `IRecentSource`，增量更新索引并通知 UI。

SQLite schema（核心表）：

```sql
CREATE TABLE recent_items (
    normalized_path      TEXT PRIMARY KEY,
    display_name         TEXT NOT NULL,
    extension            TEXT,
    category_source      TEXT,               -- 对应 DB 字段 category_source
    recent_time          INTEGER NOT NULL,   -- unix epoch ms
    target_modified_time INTEGER,
    size_bytes           INTEGER,
    exists_state         INTEGER NOT NULL,   -- 0=Missing,1=Exists,2=Unknown
    is_folder            INTEGER NOT NULL,
    is_favorite          INTEGER NOT NULL,
    favorite_time        INTEGER,
    favorite_order       INTEGER NOT NULL DEFAULT 0,
    is_hidden            INTEGER NOT NULL,
    source_kinds         INTEGER NOT NULL,   -- bitmask
    icon_cache_key       TEXT,
    last_seen_time       INTEGER NOT NULL
);

-- 独立收藏表：解耦存储，保证收藏项的持久性
CREATE TABLE favorites (
    normalized_path      TEXT PRIMARY KEY,
    display_name         TEXT NOT NULL,
    extension            TEXT,
    category_source      TEXT,
    recent_time          INTEGER NOT NULL,
    target_modified_time INTEGER,
    size_bytes           INTEGER,
    exists_state         INTEGER NOT NULL,
    is_folder            INTEGER NOT NULL,
    is_favorite          INTEGER NOT NULL,
    favorite_time        INTEGER,
    favorite_order       INTEGER NOT NULL DEFAULT 0,
    is_hidden            INTEGER NOT NULL,
    source_kinds         INTEGER NOT NULL,
    icon_cache_key       TEXT,
    last_seen_time       INTEGER NOT NULL
);

CREATE INDEX idx_recent_time ON recent_items(recent_time DESC);
CREATE INDEX idx_extension   ON recent_items(extension);
```

**连接健壮性策略**：
1. **禁用连接池**：为了防止在数据库自动重建（TryRebuild）时因句柄锁定导致 `File.Move` 失败，连接字符串强制使用 `Pooling=False`。
2. **强制清理**：在尝试删除或移动数据库文件前，必须调用 `SqliteConnection.ClearAllPools()` 并配合 `GC.Collect()` 确保句柄释放。
3. **数据原子性**：单条更新采用 `UPSERT` 逻辑。后台扫描使用 `UpsertDiscovery` 保护收藏/隐藏状态位，确保 Single Point of Truth。

### 6.19 文件系统监听

每个 L1 监听目录起一个 `FileSystemWatcher`。

监听事件：
- Created
- Changed
- Deleted
- Renamed

要求：
- `IncludeSubdirectories = true`。
- `InternalBufferSize = 64 * 1024`（默认 8KB 在大量并发写入下会溢出）。
- 必须订阅 `Error` 事件；溢出 / 失效后，标记该来源 `Stale`，**触发该来源全量增量重扫**（按 `LastWriteTime` 比对）。
- 默认 debounce **1200ms**（兼容浏览器 `.crdownload → .ext` 重命名 + 杀毒扫描的尾部抖动）。
- 增量解析：仅对变化路径更新索引。
- 解析失败不影响主程序运行。

**OneDrive / 占位符 / 重解析点保护**：

- 枚举或 `FileInfo` 调用前，先用 `GetFileAttributes` 检查：
  - `FILE_ATTRIBUTE_RECALL_ON_OPEN` (0x40000)
  - `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` (0x400000)
  - `FILE_ATTRIBUTE_OFFLINE` (0x1000)
- 命中任一标志位的文件：**只取 Win32 元数据**（路径、名称、`LastWriteTime`、`AttributesSize`），不读 `Length`、不读缩略图、不打开文件流，避免触发系统拉云端文件下载。
- 此类文件在 UI 上**不特别标注云图标**（首版不做云盘集成），按普通本地文件展示，但内部标记跳过缩略图。

**UNC 路径监听**：

- `FileSystemWatcher` 支持 UNC，但 SMB 通知需要服务端配合。当 watcher 触发 `Error`：
  - 标记该源 `Disconnected`。
  - 启动指数回退重连（1s, 2s, 4s, ..., 上限 5min）。
  - 重连成功后做一次增量比对（`LastWriteTime > lastSeenTime` 的文件）。
- 已索引的 UNC 文件在网络断开时 `exists_state` 设为 `Unknown`（不是 Missing），UI 灰显加"⚠ 离线"小标。

### 6.20 文件存在性校验

- `File.Exists` 在断开的网络盘上会卡 5–30 秒，**禁止**在 UI 线程或同步路径上调用。
- 实现：
  - 索引中保留 `exists_state` 三态（Missing / Exists / Unknown）。
  - 进入可视区域的文件项，触发后台 1.5s 超时的 `Exists` 探测。
  - 超时统一回 `Unknown`。
  - 网络盘整源不可达时，整源批量标 `Unknown`，不再逐文件探测。

### 6.21 开机自启

要求：
- 设置页提供开机自启开关（P1）。
- 当前用户级别自启，不需要管理员权限。
- 实现：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下 `Recents` 值。
- 启动参数 `--minimized` 让进程直接进入托盘。

### 6.22 设置页

设置页分组：

1. **General**
   - Launch at startup
   - Show on active monitor
   - Hide when focus lost
   - Always on top
   - Close button behavior（隐藏到托盘 / 退出）

2. **Hotkey**
   - Current hotkey
   - Record new hotkey
   - Reset to default

3. **Sources**
   - 默认已知文件夹（Downloads / Desktop / Documents / Pictures / Videos / Music），各自可启用 / 禁用
   - "Add Folder…" 添加自定义本地目录
   - "Add Network Path…" 添加 UNC 路径，例 `\\10.0.0.100\claw\`
   - 各源 `RecentLookbackDays` 可配
   - 显示每个源的状态（Active / Disconnected / Disabled / Stale）

4. **List**
   - Max recent items
   - Show folders
   - Default sort

5. **Filters**
   - File type groups
   - Excluded extensions
   - Excluded paths
   - Excluded keywords
   - Whitelisted paths

6. **Cache**
   - Rebuild index
   - Clear icon cache
   - Clear hidden items

7. **About**
   - Version
   - Data paths
   - Open log folder

#### 6.22.1 设置页布局优化 (Compact Design)

为了保持工具的轻量感与高效，设置页采用了紧凑型设计：
- **移除冗余标题**：移除右侧内容区头部的 "Recents Settings" 文字，仅保留标题栏左侧的动态状态提示（StatusMessage）。
- **紧凑边距**：设置项内容左边距（Margin-left）设定为 32px，移除大面积空白，使交互焦点更集中。
- **窗口尺寸**：设置窗口默认大小优化为 700x540，最小尺寸为 600x480。
- **侧边栏宽度**：侧边栏宽度固定为 180px。
- **视觉一致性**：热键录制框在聚焦时应有高亮底色和边框提示，代表处于“监听”状态。
- **操作反馈**：设置项变更后，标题栏应实时显示“Settings saved”并随后淡回“Ready”或当前状态。

### 6.23 收藏文件 / 固定文件

- **独立持久化**：用户可将文件固定到 Favorites。收藏项存储在独立的 `favorites` 表中，确保即使主列表条目因老化被修剪，收藏依然永久保留。
- **展示位置**：Favorites 始终可通过右侧抽屉（Drawer）访问，也可在左侧导航栏的 Favorites 视图中查看。
- **收藏状态**：收藏状态保存到 SQLite `is_favorite` 字段。
- **存在性表现**：收藏文件不存在仍显示，灰显。
- **取消固定**：支持在主列表、左侧视图或右侧抽屉中取消固定。
- **重排序**：右侧抽屉支持通过鼠标拖拽进行手动重排序，顺序持久化保存到 `favorite_order` 字段。
- **外部拖入**：支持将外部文件（如从资源管理器或其他应用）直接拖入收藏栏（抽屉）进行收藏。

### 6.24 最近文件夹

来源：
- L1 监听目录中文件的父目录聚合（按最近文件时间）。
- .lnk 中目标为文件夹的条目。
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths`（地址栏输入历史，作为补充）。

展示逻辑：
- 双击文件夹用 Explorer 打开。
- 文件夹支持拖拽。
- **展示各子目录（文件的直接父目录）**，不做更高级别的父目录聚合。
- **`IsFolder=true` 的条目必须保留在索引中**（融合时不可剔除），否则 Recent Folders 视图无数据。

---

### 6.25 空格键快速预览

#### 6.25.1 设计原则

- **触发方式**：在主列表中选中任一文件行后按 `Space`，弹出预览浮层；再按 `Space` 或 `Esc` 关闭。
- **不抢焦点**：预览窗口以 `ShowActivated = false` 显示，键盘焦点始终保留在主窗口，`↑/↓/Enter/Esc/Space` 等按键继续由主窗口响应。
- **预热机制**：主窗口首次显示后，在后台静默初始化一个持久化的 `PreviewWindow` 实例并完成 WebView2 运行时初始化（`EnsureCoreWebView2Async`），使第一次按 `Space` 也能在 200ms 内响应。
- **单一实例**：`PreviewWindow` 全程只有一个实例，切换文件只替换内容，不销毁重建窗口。
- **文件夹不预览**：选中文件夹时按 Space，窗口显示"Folders cannot be previewed."。

#### 6.25.2 支持的预览格式

| 分类 | 扩展名 | 预览方式 |
|---|---|---|
| 图片 | `.png .jpg .jpeg .gif .bmp .webp .ico` | VirtualHost + `<img>` HTML |
| SVG | `.svg` | VirtualHost + `<img>` HTML（矢量直接渲染） |
| PDF | `.pdf` | `webView.Source = fileUri`（WebView2 内置 PDF 查看器） |
| 纯文本 | `.txt .log .ini .conf .env` | 读取内容 → HTML 转义 → `<pre>` |
| CSV | `.csv` | 读取 → 解析 → `<table>`（最多 500 行） |
| 代码 | `.cs .ts .js .py .go .rs .java .cpp .c .h .json .xml .yaml .yml .html .css .sql .toml .sh .ps1` | 读取内容 → HTML 转义 → `<pre><code>` |
| Markdown | `.md .markdown` | Markdig 转 HTML → `NavigateToString` |
| 音频 | `.mp3 .wav .m4a .aac .flac .ogg` | VirtualHost + `<audio controls autoplay>` |
| 视频 | `.mp4 .webm` | VirtualHost + `<video controls autoplay>` |

**不支持格式**（显示"This file type cannot be previewed."）：

```
.doc .docx .xls .xlsx .ppt .pptx
.psd .ai .dwg .dxf .sketch
.zip .rar .7z .tar .gz
.exe .dll .sys
以及其他二进制格式
```

#### 6.25.3 文件大小限制

| 类型 | 读取上限 | 超出行为 |
|---|---|---|
| 文本 / 代码 / Markdown / CSV | 5 MB | 显示 "File is too large to preview (> 5 MB). Press Enter to open." |
| 图片 | 100 MB | 同上 |
| PDF | 不限（WebView2 流式） | — |
| 音视频 | 不限（浏览器流式） | — |

#### 6.25.4 预览窗口布局

```text
┌─────────────────────────────────────────────────────────┐
│  [FileIcon] filename.ext          ×                      │  ← 标题栏（28px，不抢焦点）
│  Modified: 2026-05-01 14:23  ·  2.4 MB  ·  Images      │  ← 元信息栏（20px）
├─────────────────────────────────────────────────────────┤
│                                                         │
│                  [ WebView2 内容区 ]                     │  ← 自适应填满
│                                                         │
├─────────────────────────────────────────────────────────┤
│  D:\full\path\to\filename.ext          Enter to Open    │  ← 底部栏（28px）
└─────────────────────────────────────────────────────────┘
```

窗口规格：
- 默认尺寸：860×640，可拖动调整大小（最小 400×300）
- 位置：优先出现在主窗口右侧（如屏幕空间不足则出现在左侧；均不足则覆盖于主窗口中央）
- 圆角 `CornerRadius=12`，无系统边框（`WindowStyle=None`），暗色背景与主窗口视觉一致
- **不在任务栏显示**（`ShowInTaskbar=False`）
- **不置顶**（不强制 `Topmost`，跟随普通窗口层叠顺序）

#### 6.25.5 交互行为

| 操作 | 效果 |
|---|---|
| `Space`（主窗口） | 打开预览 / 关闭预览 |
| `Esc`（主窗口，预览开启时） | 关闭预览（不关主窗口） |
| `Esc`（主窗口，预览关闭时） | 隐藏主窗口 |
| `↑ / ↓`（主窗口，预览开启时） | 切换选中文件，100ms debounce 后刷新预览内容 |
| `Enter`（主窗口） | 用默认程序打开文件（预览开启不受影响） |
| 滚轮（预览窗口内） | 滚动预览内容 |
| 点击 `×`（预览窗口标题栏） | 关闭预览 |
| 点击 "Enter to Open"（预览底部） | 用默认程序打开文件 |
| 拖动预览窗口 | 可自由移动；位置不记忆（每次打开复位） |
| 主窗口隐藏时 | 预览窗口同步关闭 |

#### 6.25.6 HTML 模板视觉规范

所有生成的 HTML 页面须遵循以下样式，与主窗口保持视觉一致：

```css
/* 暗色基底 */
background: #191D26;
color: #F3F4F6;
font-family: "Segoe UI Variable Display", "Segoe UI", "Microsoft YaHei UI", sans-serif;

/* 代码 / 文本区 */
font-family: "Cascadia Code", "Consolas", monospace;
font-size: 13px;
line-height: 1.6;
white-space: pre-wrap;
word-break: break-all;

/* 图片容器 */
display: flex; justify-content: center; align-items: center;
min-height: 100vh; background: #101216;

/* 滚动条 */
scrollbar-color: #2B313D #101216;
scrollbar-width: thin;
```

代码预览 P0 不做语法高亮（避免引入 highlight.js 等重型库）；P1 可通过纯 CSS 样式增强可读性。

#### 6.25.7 WebView2 运行时依赖处理

WebView2 运行时（`Microsoft.Web.WebView2`）在 Windows 10 1903 + 最新版 Edge 的机器上通常已预装。但需处理缺失情况：

- 启动时异步检查 `CoreWebView2Environment.GetAvailableBrowserVersionString()`
- 若抛异常（未安装）：`AppSettings.PreviewEnabled` 自动置 `false`，主窗口 Space 键按下时提示：
  ```
  Quick preview requires WebView2. 
  Download at: aka.ms/webview2
  ```
- 不因缺少 WebView2 而影响主功能的任何部分。

#### 6.25.8 性能要求

| 场景 | 目标 |
|---|---|
| 首次按 Space（WebView2 已预热） | < 200ms 显示窗口并开始渲染 |
| 切换文件刷新预览（文本 / 代码 ≤ 1MB） | < 150ms |
| 切换文件刷新预览（图片 ≤ 10MB） | < 300ms |
| PDF 首屏可见 | < 500ms（WebView2 自身 PDF 渲染） |
| WebView2 预热（后台，不阻塞主功能） | < 3s（允许后台进行，不影响启动速度） |

#### 6.25.9 错误处理

| 错误 | 展示内容 |
|---|---|
| 文件不存在 | "File not found." |
| 无读取权限 | "Cannot read file: Access denied." |
| 文件过大 | "File is too large to preview (> X MB). Press Enter to open." |
| 不支持格式 | "This file type cannot be previewed. Press Enter to open." |
| 文件夹 | "Folders cannot be previewed." |
| WebView2 渲染超时（> 5s） | "Preview timed out. Press Enter to open." |
| WebView2 未安装 | "Quick preview requires WebView2 runtime. Download at aka.ms/webview2" |

所有错误页使用与预览窗口一致的暗色主题 HTML 模板，不弹系统对话框。

---

## 7. UI 风格要求

### 7.1 总体风格

UI 以最新高保真界面图为视觉参考：暗色、现代、紧凑、Windows 桌面效率工具风格。视觉目标是"可长期常驻、可快速扫视、可拖拽操作"，不得做成网页后台或普通文件管理器。

关键词：
- 暗色优先
- 圆角窗口
- 轻微毛玻璃 / 阴影层次
- 清晰的文件列表层级
- 低干扰行内操作
- 蓝色作为唯一主强调色

### 7.2 窗口尺寸与响应式

默认尺寸：

```text
宽度：1040px
高度：760px
```

最小尺寸：

```text
宽度：760px
高度：520px
```

响应式规则：
- 宽度 ≥ 900px：显示完整左侧导航文字。
- 宽度 760–899px：左侧导航折叠为图标模式，Tooltip 显示文字。
- 宽度不足以显示所有行内按钮时，保留 `Open` 和 `More`，隐藏 `Reveal` 与 `Pin` 到 More 菜单中。
- 列表始终启用虚拟化，窗口缩放不得造成滚动卡顿。
- 低于最小尺寸不允许（拖动手柄硬限）。

DPI 与多显示器：
- `app.manifest` 已声明 `PerMonitorV2`。
- 窗口在不同 DPI 显示器之间拖动时，行高、图标尺寸、字号、内边距按新 DPI **即时**重算，不允许出现一次错位再补救。
- 4K 与 1080p 混用是基础场景，必须实测。

### 7.3 窗口行为

- 默认置顶。
- 默认不显示在任务栏（`ShowInTaskbar="False"`），可在设置中切换。
- 默认失焦不自动隐藏，可在设置中开启。
- `Esc` 隐藏窗口。
- 点击托盘图标显示窗口。
- 窗口位置使用鼠标当前所在显示器的 work area 居中。
- **边界持久化**：自动记录并恢复窗口的显示位置（Left/Top）、基本宽度（BaseWidth）、高度（Height）以及最大化状态。

### 7.4 颜色 Token

暗色主题使用以下设计 Token。**§7.4 是颜色实现的真理源**；高保真设计图仅作视觉参考，与本节有差异时以本节为准；如设计图实测吸色与本节差异 > 1 个色阶（Δ ≥ 16），需提交修订请求并更新本节后再实现：

```text
WindowBackground：#101216
TitleBarBackground：#151922
SidebarBackground：#141821
PanelBackground：#191D26
PanelElevated：#202633
SearchBackground：#252A34
RowBackground：#191D26
RowHover：#222836
RowSelected：#1E3556
RowSelectedBorder：#3B82F6
Divider：#2B313D
PrimaryText：#F3F4F6
SecondaryText：#A7ADBA
TertiaryText：#7E8491
Accent：#3B82F6
AccentHover：#60A5FA
Success：#63C554
Warning：#F5B642
Danger：#E15B64
Disabled：#5D6470
```

要求：
- 主强调色只用于选中态、活动 Chip、活动导航条、Pin 已收藏态。
- 文件路径、时间、大小使用 SecondaryText 或 TertiaryText。
- 不使用高饱和荧光青作为大面积文本色，避免阅读疲劳。

### 7.5 字体与字号

默认字体：

```text
Segoe UI
Microsoft YaHei UI
```

字号：

| 元素 | 字号 | 字重 |
|---|---:|---:|
| App 标题 | 15 | Semibold |
| 搜索框 | 18 | Regular |
| 导航文字 | 14 | Regular |
| 文件名 | 15 | Semibold |
| 元信息 | 12.5 | Regular |
| 状态栏 | 12 | Regular |
| Tooltip | 12 | Regular |

### 7.6 图标规范（硬规则）

图标来源**只允许**：

1. 文件图标：Windows Shell 系统图标或系统缩略图（`SHGetFileInfo` 占位 + `IShellItemImageFactory` 真实缩略图）。
2. 导航 / 操作图标：Segoe Fluent Icons、Segoe MDL2 Assets 或项目内 SVG 矢量图。
3. 缺省文件图标：项目内通用占位图标。

**禁止：**

- 任何二次重绘、风格化、AI 生成、按文件名 / 类型自动生成的卡通图标 / 头像图标。
- 为 `.exe` 文件强行显示机器人 / 风格化角色图标，**除非该 EXE 自身资源里就是该图标**（即调用 `SHGetFileInfo` / `IShellItemImageFactory` 返回的就是该图标）。
- 使用 emoji 作为功能图标。
- 使用与功能无关的装饰图标。

导航与操作图标若用项目内 SVG，色值必须来自 §7.4 Token，不得与系统主题冲突。

### 7.7 间距与圆角

建议值：

```text
Window corner radius：12
Panel corner radius：10
Search box radius：8
Chip radius：16
Row radius：6
Sidebar item radius：6
Outer padding：12
Sidebar width：180
Sidebar icon-only width：56
Row horizontal padding：12
Row vertical padding：8
Settings Content Padding：32,24
```

### 7.8 文案规范

固定 UI 文案：

| 位置 | 文案 |
|---|---|
| App 标题 | `Recents` |
| 搜索占位 | `Search recent files...` |
| 默认排序 | `Newest first` |
| 状态正常 | `Ready` |
| 重建索引 | `Rebuild index` |
| 清除筛选 | `Clear filters` |
| 打开 | `Open` |
| 打开所在位置 | `Reveal in Explorer` |
| 收藏 | `Pin` |
| 取消收藏 | `Unpin` |
| 更多 | `More actions` |
| 托盘菜单显示 | `Show` |
| 托盘菜单退出 | `Exit` |
| 托盘菜单重扫 | `Rescan` |
| 关闭按钮气泡 | `Recents 仍在托盘运行。可在设置中改为按关闭即退出。` |

要求：
- 主界面文案默认英文，保证简洁；中文文件名、路径按原文显示。
- 不能出现开发占位文案，例如 `TODO`、`Button`、`TextBlock`、`Search by extension/file name`。
- **不能出现与当前产品名不一致的标题**，例如 `RecentDock`。
- **不能出现"待实现"、"敬请期待"等占位文本**。

### 7.9 长路径与本地化

- `app.manifest` 声明 `<longPathAware>true</longPathAware>` + `dpiAwareness=PerMonitorV2`。
- 所有路径处理走 Unicode。
- UI 显示长路径中段省略，例如：

```text
D:\very\long\…\Contracts\file.docx
```

- Tooltip 显示完整路径。
- 路径复制必须复制完整路径，不复制省略后的 UI 文本。

---

## 8. 数据模型

### 8.1 RecentItem

```csharp
public class RecentItem
{
    public string NormalizedPath { get; set; }   // 主键
    public string DisplayName { get; set; }
    public string Extension { get; set; }
    public string FileType { get; set; }         // 由 FileTypeClassifier 填充
    public DateTime RecentTime { get; set; }
    public DateTime? TargetModifiedTime { get; set; }
    public long? SizeBytes { get; set; }
    public ExistsState Exists { get; set; }      // Missing / Exists / Unknown
    public bool IsFolder { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsHidden { get; set; }
    public SourceKinds Sources { get; set; }     // Flags 位掩码
    public string IconCacheKey { get; set; }
}

[Flags]
public enum SourceKinds
{
    None              = 0,
    KnownFolderWatch  = 1 << 0,   // Downloads/Desktop/...
    UserFolderWatch   = 1 << 1,   // 自定义本地目录
    UncFolderWatch    = 1 << 2,   // 自定义 UNC 路径
    RecentLnk         = 1 << 3,
    OfficeMru         = 1 << 6,
    OpenSavePidlMru   = 1 << 7,
    RecentDocsReg     = 1 << 8,
}

public enum ExistsState { Missing = 0, Exists = 1, Unknown = 2 }
```

### 8.2 SourceConfig

```csharp
public class SourceConfig
{
    public string Id { get; set; }                  // GUID
    public SourceKinds Kind { get; set; }
    public string Path { get; set; }                // 本地或 UNC（KnownFolder 留空，由 KnownFolderGuid 解析）
    public string KnownFolderGuid { get; set; }     // 已知文件夹 GUID（仅 KnownFolderWatch）
    public string DisplayName { get; set; }         // 设置页 Sources 列表展示
    public bool Enabled { get; set; } = true;
    public int RecentLookbackDays { get; set; } = 30;
}
```

### 8.3 AppSettings

```csharp
public class AppSettings
{
    public bool LaunchAtStartup { get; set; } = false;
    public bool AlwaysOnTop { get; set; } = true;
    public bool HideOnFocusLost { get; set; } = false;
    public bool ShowMissingFiles { get; set; } = true;
    public bool ShowFolders { get; set; } = true;
    public int MaxRecentItems { get; set; } = 200;
    public string Hotkey { get; set; } = "Alt+Shift+Z";
    public bool ClosedToTrayNoticeShown { get; set; } = false;  // §5.2 关闭气泡只弹一次
    public bool VerboseLogging { get; set; } = false;

    public List<SourceConfig> Sources { get; set; } = new();

    public List<string> ExcludedExtensions { get; set; } = new();
    public List<string> ExcludedPaths { get; set; } = new();   // 默认含 bin\Debug / obj\
    public List<string> ExcludedKeywords { get; set; } = new();
    public List<string> WhitelistedPaths { get; set; } = new();
    public Dictionary<string, List<string>> FileTypeGroups { get; set; } = new();
}
```

---

## 9. 推荐代码结构

```text
Recents/
  Recents.sln
  src/
    Recents.App/
      App.xaml
      App.xaml.cs
      app.manifest                  // PerMonitorV2 + longPathAware
      MainWindow.xaml
      MainWindow.xaml.cs
      SettingsWindow.xaml
      SettingsWindow.xaml.cs
      Views/
      ViewModels/
        MainViewModel.cs
        SettingsViewModel.cs
        RecentItemViewModel.cs
        SourcesViewModel.cs
      Models/
        RecentItem.cs
        SourceConfig.cs
        AppSettings.cs
      Services/
        RecentIndexService.cs        // SQLite 索引 + 融合
        StatusHintService.cs         // 底部状态栏文案动态输出
        Sources/
          IRecentSource.cs
          KnownFolderWatchSource.cs  // L1 默认已知文件夹
          UserFolderWatchSource.cs   // L1 自定义本地目录
          UncFolderWatchSource.cs    // L1 UNC 路径
          RecentLnkSource.cs         // L1 Recent .lnk
          OfficeMruSource.cs         // L2
          OpenSavePidlMruSource.cs   // L3
        ShellLinkResolver.cs
        FileIconService.cs
        HotkeyService.cs
        TrayService.cs
        DragDropService.cs
        SettingsService.cs
        StartupService.cs
        FileActionService.cs
        ExistsProbeService.cs        // 异步 + 超时
        SingleInstanceService.cs
      Utils/
        Debouncer.cs
        PathNormalizer.cs
        PathMatcher.cs
        FileTypeClassifier.cs
        CloudPlaceholderDetector.cs
      Resources/
        Icons/
```

架构要求：
- MVVM。
- UI 层不直接扫描文件，不直接读注册表，不直接访问 SQLite。
- 所有数据源实现 `IRecentSource { Task InitialScanAsync(); IObservable<RecentChange> Watch(); }`。
- 耗时操作必须异步。
- 主窗口呼出时只读内存视图，不阻塞 UI 线程。

---

## 10. 性能要求

目标：

- 快捷键呼出窗口：< 100ms。
- 初次完整扫描（默认 6 个已知文件夹 + Recent + Office MRU / OpenSave MRU）：可接受 1–3 秒，必须后台执行；UI 立即用上次缓存渲染。
- 后续呼出：使用 SQLite + 内存视图，不重新全量扫描。
- 搜索过滤 1 万条：无明显输入卡顿。
- 图标加载：异步，不阻塞列表显示。
- 内存占用：常驻状态 < 150MB，不含 SQLite 页缓存。

性能原则：
- 呼出窗口只读内存列表。
- 不在 UI 线程解析 .lnk、读注册表、读图标、查 `File.Exists`。
- 不在每次搜索时访问磁盘。
- 不在每次呼出时重新扫描。
- 列表渲染走虚拟化。

---

## 11. 错误处理

必须处理：

- 已知文件夹路径解析失败（`SHGetKnownFolderPath` 报错）
- 自定义目录不存在 / 权限拒绝
- UNC 路径不可达
- `FileSystemWatcher` 缓冲区溢出（`Error` 事件）
- .lnk 解析失败
- 注册表 MRU 读取失败
- 目标文件不存在
- 目标路径无权限访问
- 图标提取失败
- 快捷键注册失败
- 配置文件损坏
- SQLite 索引损坏
- 开机自启设置失败
- OneDrive 占位符触发拉取（应预防而非处理）

处理原则：
- 单个文件失败不影响整个列表。
- 单个数据源失败不影响其他数据源。
- 配置损坏：自动备份坏文件并重建默认配置。
- SQLite 损坏：自动重命名并重建。
- 快捷键注册失败：托盘气泡 + 设置页双重提示。

---

## 12. 日志

日志路径：

```text
%LOCALAPPDATA%\Recents\logs\recents.log
```

记录内容：
- 应用启动 / 退出
- 单实例检测结果
- 快捷键注册状态（含候选回退）
- 各数据源初始化、扫描计数、错误
- .lnk / 注册表 MRU 解析失败统计（不记内容）
- SQLite 读写失败
- 设置读写失败
- 文件操作失败

隐私：
- 默认不记完整路径，对路径做"盘符 + 截断 + 哈希"处理（如 `D:\…\<sha1:8>\file.docx`）。
- 仅当用户在设置中开启 "Verbose logging" 才记完整路径。
- 不记录文件内容。

---

## 13. 隐私与安全

要求：
- 所有数据仅保存在本机。
- 不联网（除用户主动添加 UNC 路径，访问目标网络）。
- 不上传文件路径。
- 不读取文件内容。
- 不删除原始文件。
- "Remove Once"只删除 `%APPDATA%\Microsoft\Windows\Recent` 中的 .lnk，不动 Office MRU、不动注册表 MRU。
- 不识别 OneDrive / Google Drive 登录态，不读其元数据；用户挂载到本地的文件夹按普通文件夹处理（但跳过占位符的耗资源调用，见 §6.19）。
- 单实例（命名 Mutex `Global\Recents.SingleInstance`），第二次启动转发显示窗口请求并退出。

---

## 14. 首版验收清单

### 14.1 基础功能

- [ ] 应用启动后出现在系统托盘，且 Mutex 防双开。
- [ ] `Alt + Shift + Z` 可以呼出 / 隐藏窗口。
- [ ] 默认热键被占用时，自动回退候选并提示。
- [ ] 窗口打开后搜索框自动聚焦。
- [ ] 搜索框右侧 Badge 显示实际注册成功的快捷键（来自 `HotkeyService.ActiveLabel`，非硬编码）。
- [ ] 启动后所有启用的数据源均开始扫描 / 监听（Downloads/Desktop/Documents/Pictures/Videos/Music + Recent + Office MRU + OpenSave MRU）。
- [ ] 文件列表按最近时间倒序展示。
- [ ] 多源命中同一路径时，仅展示一条，且 Tooltip 列出所有来源。
- [ ] 显示文件图标、名称、路径、时间、大小。

### 14.2 主界面 UI 验收（按高保真图）

- [ ] 标题栏显示 `Recents`，**XAML / 二进制 / 可执行体内不存在 `RecentDock` 字符串**。
- [ ] 左侧导航 `All / Favorites / Recent Folders / Documents / Images / Code` 均可点击并进入真实视图。
- [ ] **`Settings` 导航项仅在设置页已实现时出现；P0 阶段验收要求"不显示 Settings"**。
- [ ] 顶部 Chip `All / Docs / Images / Code / Folders` 均可点击并改变列表过滤结果。
- [ ] `Newest first` 下拉可切换排序，且排序立即生效；下拉只列已实现的排序选项。
- [ ] **P0 视图密度按钮不显示**；若 P1 已启用，必须 Comfortable / Compact 即时切换。
- [ ] 每个文件行右侧的 `Open / Reveal / Pin / More` 均可执行真实动作。
- [ ] Pin 图标蓝色只表示真实收藏状态，不得作为装饰高亮。
- [ ] 三点菜单打开的菜单项与 §6.10.1 一致；P0 不显示 §6.10.2 扩展项。
- [ ] 底部 `Ready | n items` 中的 `n` 是当前过滤后可见数量。
- [ ] 底部键盘提示随焦点和选择状态变化，不显示不可用操作。
- [ ] 所有可见图标均有 Tooltip。
- [ ] 未实现功能不得显示为假按钮、假图标或假菜单项。
- [ ] 列表不得硬编码示例文件名、示例路径、示例时间。
- [ ] **列表不出现 `%APPDATA%\Microsoft\Windows\Recent` 中的 .lnk 条目**（除非用户索引到的真实文件本身就是 .lnk）。
- [ ] **列表不出现项目自身的 `bin\Debug` / `bin\Release` / `obj\` 输出文件**。
- [ ] **收藏抽屉开启时，窗口宽度自动增加 280px，主列表宽度保持不变**。
- [ ] **列表滚动具有平滑的惯性/灵敏度控制（0.4x）**。
- [ ] **关闭按钮首次按下时弹一次"已隐藏到托盘"气泡**，第二次起不再弹。
- [ ] **拖动到 4K 与 1080p 显示器之间，UI 元素即时按 DPI 缩放，无错位**。
- [ ] **`.exe` 文件图标必须来自系统真实资源**，无任何二次重绘 / 风格化。

### 14.3 数据源覆盖（关键）

- [ ] **从浏览器下载文件到 Downloads，2 秒内出现在列表顶部**（这是 Recent 文件夹方案漏掉的核心场景）。
- [ ] 在 VS Code 中打开 / 保存文件，相应文件出现在列表（来源标 KnownFolderWatch 或 RecentLnk 之一即可）。
- [ ] 从微信 / 飞书接收文件到默认目录，相应文件出现在列表。
- [ ] 在 Office 中打开文件，相应文件出现在列表（来源含 OfficeMru）。
- [ ] 添加 `\\10.0.0.100\claw\` 为自定义 UNC 来源后，远端写入的文件能进列表。
- [ ] UNC 网络断开时，已索引的远端文件标 Unknown 灰显，UI 不卡顿；网络恢复后自动重新可达。

### 14.4 文件操作

- [ ] 双击文件可用默认程序打开。
- [ ] 右键可打开文件 / 打开方式 / 打开所在位置 / 复制完整路径（"打开方式"P1）。
- [ ] 行内 Open 按钮与双击行为一致。
- [ ] 行内 Reveal 按钮能在 Explorer 中选中原文件。
- [ ] 行内 Pin / Unpin 能写入并读取真实收藏状态。
- [ ] 拖拽到微信、飞书、Outlook、Edge、Explorer 五处目标 App 时均传递原始文件路径（DataObject 同时附 FileDrop + Shell IDList Array）。
- [ ] Missing 文件灰显，禁止拖拽，Open 按钮禁用并显示原因 Tooltip。

### 14.5 搜索与筛选

- [ ] 输入文件名子串可筛选。
- [ ] 输入 `.docx` 或 `pdf`（首字符为 `.`）按扩展名精确筛选。
- [ ] 输入路径片段可筛选。
- [ ] 左侧文件类型筛选可用。
- [ ] 顶部 Chip 筛选可用。
- [ ] 搜索、左侧导航、顶部 Chip、排序可叠加。
- [ ] 筛选无结果时显示空状态和 `Clear filters` 动作。

### 14.6 设置（P1）

- [ ] 可设置开机自启。
- [ ] 可设置最大显示数量。
- [ ] 可设置是否显示 Missing 文件。
- [ ] 可在 **Sources** 启用 / 禁用每个默认已知文件夹。
- [ ] 可在 **Sources** 添加 / 移除自定义本地目录。
- [ ] 可在 **Sources** 添加 / 移除 UNC 路径。
- [ ] 可设置排除扩展名 / 路径 / 关键词。
- [ ] 可设置白名单路径。
- [ ] 可重建索引（SQLite，无需二次确认）。

### 14.8 Quick Preview (Space-bar)

- [ ] 按 `Space` 呼出预览窗口，窗口包含：文件名标题、元信息（大小、时间、类型）、WebView2 渲染区、底部全路径显示。
- [ ] 预览窗口不抢主窗口焦点（`ShowActivated="False"`）。
- [ ] 支持图片（PNG, JPG, SVG 等）原生渲染。
- [ ] 支持 PDF 在预览窗内流式打开。
- [ ] 支持文本、代码文件等自动读取并转义 HTML 展示。
- [ ] 支持 Markdown 文件实时渲染（基于 Markdig）。
- [ ] 支持 音频、视频文件在窗内播放。
- [ ] 支持 CSV 文件以表格形式展示（最多 500 行）。
- [ ] 选中文件夹时按 Space 显示"Folders cannot be previewed."。
- [ ] 切换选中项时预览内容在 150ms 内完成刷新（含 100ms 防抖）。
- [ ] 应用启动后后台静默预热 WebView2 实例，确保初次按 Space 响应时间 < 200ms。

### 14.7 性能

- [ ] 快捷键呼出 < 100ms。
- [ ] 搜索输入无明显卡顿（1 万条规模）。
- [ ] 图标异步加载不阻塞 UI。
- [ ] FileSystemWatcher 缓冲区溢出后能自动恢复（人为触发 64KB 内大量并发写入测试）。
- [ ] OneDrive 占位符不被工具触发联网下载（关键回归点）。

---

## 15. 开发优先级

### P0（第一阶段必做）

P0 必须交付一个"界面上没有假功能"的可用版本。**若某个功能未完成，对应按钮 / 菜单 / 导航项 / 排序选项必须完全不显示，不允许灰色禁用占位**。

- WPF .NET 8+ 项目骨架 + app.manifest（PerMonitorV2 + longPathAware）
- 单实例 Mutex
- 托盘常驻
- 全局快捷键（含候选回退）+ Badge 显示 `HotkeyService.ActiveLabel`
- 主窗口暗色 UI，必须包含：标题栏（标题 `Recents`）、搜索框、左侧导航、Chip 筛选、排序下拉、文件列表、底部状态栏
- 关闭按钮首次按下提示气泡，写入 `ClosedToTrayNoticeShown`
- 左侧导航：All / Favorites / Recent Folders / Documents / Images / Code（**P0 不显示 Settings**）
- 顶部 Chip：All / Docs / Images / Code / Folders
- 排序：`Newest first` + `Name A-Z`，下拉只列这两项
- 视图密度切换：**P0 不做，对应图标不显示**
- 文件行操作：Open / Reveal / Pin / More
- Pin / Favorites 基础能力：写入 SQLite `is_favorite` 并可在 Favorites 视图读取
- More 菜单：§6.10.1 基础项（Open / Reveal in Explorer / Copy full path / Copy file name / Pin / Unpin）
- 底部状态栏：Ready / Indexing / Partial / Error + 当前可见数量 + 动态键盘提示（`StatusHintService`）
- **`IRecentSource` 抽象 + `RecentIndexService` SQLite 索引融合**（含 `IsFolder=true` 条目保留）
- **L1 数据源：KnownFolderWatchSource（Downloads / Desktop / Documents / Pictures / Videos / Music）**
- L1 数据源：RecentLnkSource（含纯托管 .lnk 解析；列表中**不展示 .lnk 本体**，仅归并到目标真实文件）
- 主窗口列表（虚拟化）
- 搜索过滤
- 双击打开
- 拖拽原始文件（**FileDrop + Shell IDList Array 同时附**）
- 文件图标占位加载：按扩展名显示系统通用图标；真实缩略图可放 P1
- 默认排除规则首次运行写入 settings.json，含 `bin\Debug` / `bin\Release` / `obj\` / `node_modules` 等
- 键盘：`Esc / Enter / ↑↓ / Ctrl+C / Ctrl+Shift+C / Ctrl+O / Ctrl+F`
- `FileTypeClassifier` 在数据源写入索引前完成分类，禁止 `FileType="Other"` 全量硬编码

### P1

- 完整右键菜单（含 §6.10.2 扩展项：Open With... / Hide from list / Remove Once）
- 文件图标真实缩略图（占位 + 异步真实图标 + 缓存）
- 设置页完整实现（含 Settings 导航项启用、General / Hotkey / Sources / List / Filters / Cache / About）
- 文件类型筛选配置化
- L1 数据源：UserFolderWatchSource（自定义本地目录）
- L1 数据源：UncFolderWatchSource（UNC 路径，含断网重连）
- L2 数据源：OfficeMruSource
- 排序：Oldest first / Size largest
- 视图密度按钮（Comfortable / Compact）
- 开机自启
- `Delete` 键隐藏记录

### P2

- 最近文件夹增强（Quick Access、TypedPaths）
- L3 数据源：OpenSavePidlMruSource、RecentDocs
- 黑白名单路径 UI 完善
- 排除规则 UI 完善
- 快速预览信息增强
- 键盘快捷操作完善
- Verbose logging 开关

---

## 16. 编码与质量要求

编码时必须遵守：

1. 不一次性生成过度复杂的完整系统；分阶段保证可编译可运行。
2. 每完成一个数据源，必须能在主列表中看到其贡献的条目。
3. 所有路径、快捷键、显示数量、监听目录都必须可配置。
4. 所有文件操作必须作用于原始文件，不作用于 .lnk。
5. 所有耗时任务必须异步，使用 `async/await` + `IProgress<T>` 通知 UI。
6. UI 不得因扫描、解析、图标加载、`File.Exists` 而卡顿。
7. 不联网（用户主动添加的 UNC 路径除外）。
8. 不删除原始文件。
9. 不集成任何云盘 SDK、不读取浏览器/聊天工具的私有数据库。
10. 代码结构按 Services / ViewModels / Models / Views 分层，数据源都放 `Services/Sources/`。
11. 路径处理统一走 `PathNormalizer`，禁止散落 `ToLower()`、`TrimEnd('\\')` 等就地处理。
12. 所有外部 IO 必须可单元测试（数据源接口注入、文件系统抽象）。
13. 文件类型分类统一走 `FileTypeClassifier`，禁止 `FileType="Other"` 在各数据源中硬编码。
14. UI 文案统一从 §7.8 表中取，禁止散落字符串字面量。
15. **TargetFramework 以 `net8.0-windows` 作为最低目标框架，运行时必须允许 roll-forward 到兼容的更高 .NET 主版本（如 .NET 9 / .NET 10）**；不得要求用户机器必须安装且只能安装 .NET 8。

---

## 17. 第一阶段实现范围

第一阶段（P0）必须实现一个可日常使用的版本，且界面不得遗留无意义文字、无功能图标、硬编码样例文件。

必须实现：

- 创建 WPF .NET 8+ 项目，app.manifest 配置 DPI 与长路径，并允许兼容的更高 .NET 运行时
- 单实例 Mutex
- 主窗口暗色 UI，按 §5 和 §7 实现：
  - 标题栏显示 `Recents`，关闭按钮首次按下提示气泡
  - 搜索框占位 `Search recent files...`
  - 搜索框右侧 Badge 显示 `HotkeyService.ActiveLabel`（数据绑定，非硬编码）
  - 左侧导航 `All Files / Favorites / Recent Folders`（**不显示 Documents / Images / Code / Settings**）
  - 顶部 Chip：All Files / Docs / Images / Code / Folders
  - 排序下拉：仅 `Newest first` 和 `Name A-Z`
  - 文件列表虚拟化
  - 行内 Open / Reveal / Pin / More
  - 底部状态栏 Ready / Indexing / Partial / Error + 可见数量 + 动态键盘提示
- 托盘常驻（`Show / Rescan / Exit`，不显示 `Settings`，不显示"待实现"项）
- `Alt + Shift + Z` 呼出 / 隐藏 + 候选回退
- `IRecentSource` 抽象 + SQLite 索引融合（**保留文件夹条目**）
- **`KnownFolderWatchSource` 监听 Downloads / Desktop / Documents / Pictures / Videos / Music**
- `RecentLnkSource` 解析 `%APPDATA%\Microsoft\Windows\Recent` 的 .lnk → 归并到目标真实文件，**列表中不展示 .lnk 本体**
- `FileTypeClassifier` 给每条记录分类，不允许 FileType 全量为 "Other"
- 显示最近 200 个文件，时间倒序
- 搜索文件名 / 扩展名 / 路径
- 左侧导航与顶部 Chip 的叠加筛选（注：Chip 筛选优先，详见 §5.5）
- 双击打开原文件
- 拖拽原文件（FileDrop + Shell IDList Array **同时附**）
- 行内 Open：默认程序打开原文件
- 行内 Reveal：Explorer 选中原文件或打开文件夹
- 行内 Pin：收藏 / 取消收藏，写入 SQLite
- More 菜单：§6.10.1 基础项
- 文件图标：P0 至少显示系统通用图标；异步真实缩略图可留到 P1
- 默认排除规则首次运行写入 settings.json，含 `bin\Debug` / `bin\Release` / `obj\` / `node_modules` 等
- 键盘：`Esc / Enter / ↑↓ / Ctrl+C / Ctrl+Shift+C / Ctrl+O / Ctrl+F`

P0 阶段如未实现以下能力，对应 UI **不得显示**（不得灰色禁用，必须完全不渲染）：

- Settings 导航项（设置页未实现）
- 视图密度按钮
- Open With...
- Hide from list
- Remove Once
- UNC 来源添加
- Office MRU / OpenSave MRU 来源状态
- 黑名单 / 白名单 UI
- 开机自启开关
- Oldest first / Size largest 排序选项

第一阶段**不做**：白名单 / 黑名单 UI、真实缩略图。Jump List 明确不予支持且不作为数据源。

> 第一阶段就必须解决"下载文件不出现"问题：`KnownFolderWatchSource` 是核心，**不允许把 Downloads 监听挪到后续阶段**。

---

## 18. 已知不支持场景（首版）

明确列出，避免反复争议：

- UWP / MSIX 沙盒应用打开记录（如 Microsoft Photos、Mail App）。
- OneDrive / SharePoint / Google Drive 的云端文件元数据，未本地缓存的占位符不读取。
- 浏览器内"下载历史"列表中**未实际写入磁盘**的条目。
- 加密容器（VeraCrypt / BitLocker To Go 未挂载）内的文件。
- 虚拟驱动器（Dokan / WinFsp）若不支持 `FileSystemWatcher` 通知，依赖定时增量扫描。
- 完全离线编辑且通过第三方同步工具回写、不修改 LastWriteTime 的文件。
