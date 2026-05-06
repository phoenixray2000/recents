# Recents Clipboard 模块最终产品方案

## 1. 产品定位

Recents 的 Clipboard 模块用于管理近期剪贴板内容，并与 Recents 现有“最近文件 / 收藏 / 拖拽 / 打开方式 / 预览”体系融合。

核心目标：

```text
最近文件：解决“刚用过的文件在哪里”
剪贴板：解决“刚复制过的内容在哪里”
收藏：解决“这些内容我想长期留着”
Pop Paste：解决“我想快速粘贴历史内容”
```

设计原则：

- Clipboard 是 Recents 的一个 Tab，不是独立软件。
- 文件、文件夹、图片、HTML、富文本都应尽量支持拖放复用。
- 图片拖放到外部应用时，按图片文件处理，而不是只传 Bitmap。
- 所有 Clipboard item 都支持拖到 Favorites 收藏。
- 所有数据仅本地保存，不联网、不上传。

---

## 2. 功能总览

### 2.1 顶层入口

主窗口新增 Clipboard Tab


### 2.2 支持内容类型

| 类型 | 捕获 | 展示 | 复制回剪贴板 | 拖放到应用 | 拖放到收藏 |
|---|---|---|---|---|---|
| Text | 支持 | 文本摘要 | 支持 | 作为纯文本拖放 | 支持 |
| Files | 支持 | 文件列表 | 支持 FileDrop | 拖真实文件路径 | 支持 |
| Folders | 支持 | 文件夹列表 | 支持 FileDrop | 拖真实文件夹路径 | 支持 |
| Mixed Files | 支持 | 文件/文件夹混合 | 支持 FileDrop | 拖真实路径列表 | 支持 |
| Image | 支持 | 缩略图 | 支持图片 | 拖为临时图片文件 | 支持 |
| HTML | 支持 | HTML 片段预览 | 支持 HTML + PlainText | 拖为 HTML 文件或纯文本 | 支持 |
| Rich Text / RTF | 支持 | 富文本预览 | 支持 RTF + PlainText | 拖为 RTF 文件或纯文本 | 支持 |

---

## 3. 产品规则

### 3.1 Clipboard History 开关

设置项：

```text
Enable clipboard history
```

建议默认值：

```text
false
```

原因：剪贴板可能包含密码、验证码、合同、客户信息、Token、私钥等敏感内容，默认开启不合适。

用户开启后开始记录新内容；关闭后停止记录新内容，但不自动清空已有历史。

### 3.2 本地优先

所有 Clipboard 数据只保存在本机：

```text
%APPDATA%\Recents\clipboard\
```

不做：

- 云同步
- 账号登录
- 远程上传
- 后台联网分析
- 文件内容外发

### 3.3 去重规则

同一内容重复复制时，不新增重复条目，而是更新原条目的时间并移动到顶部。

Hash 规则：

```text
Text      = SHA256("text:" + 原始文本)
Files     = SHA256("files:" + 标准化路径列表)
Image     = SHA256("image:" + PNG bytes hash)
HTML      = SHA256("html:" + raw CF_HTML)
RichText  = SHA256("rtf:" + raw RTF)
```

文件路径列表标准化：

- `Path.GetFullPath`
- 去掉末尾斜杠
- 忽略大小写
- 保留顺序；顺序不同视为不同剪贴板事件

### 3.4 收藏优先

Clipboard item 可以进入 Favorites。

收藏方式：

- 右键 `Add to Favorites`
- 点击星标
- 将 Clipboard item 拖放到 Favorites 区域

收藏后：

- 始终出现在 Favorites
- 不受自动清理影响
- 不受隐藏系统/隐藏文件规则影响
- 原始 Clipboard item 与 Favorite item 保持同一个 ID

### 3.5 删除规则

删除 Clipboard item 只删除 Recents 记录，不影响剪贴板当前内容，不删除原始文件。

对于图片 / HTML / RTF blob：

- 删除普通历史时，删除索引记录。
- 如果该 item 没有被收藏，则删除对应 blob 文件。
- 如果已收藏，则保留 blob。

---

## 4. Clipboard Tab 交互设计

### 4.1 文本条目

展示：

```text
[Text] 这是一段刚复制的内容……
       2026-05-06 14:20 · 128 chars
```

操作：

| 操作 | 行为 |
|---|---|
| 单击 | 选中 |
| 双击 | 复制回剪贴板 |
| Enter | 复制回剪贴板 |
| Ctrl + Enter | 复制并粘贴到原活动窗口 |
| Space | 预览完整内容 |
| 拖到 Favorites | 收藏 |
| 拖到其他应用 | 作为纯文本 DataObject 拖放 |
| 右键 Copy | 复制回剪贴板 |
| 右键 Paste | 复制并自动粘贴 |
| 右键 Transform | 打开转换动作菜单 |
| 右键 Delete | 删除记录 |

### 4.2 文件条目

展示：

```text
[File] SalesContract.docx
       2026-05-06 14:18 · E:\Downloads\SalesContract.docx
```

操作：

| 操作 | 行为 |
|---|---|
| 双击文件 | 用默认应用打开 |
| 双击文件夹 | 打开或激活 Explorer |
| Enter | 打开 |
| Space | 预览 |
| 拖到 Favorites | 收藏 |
| 拖到其他应用 | 传递真实文件路径 |
| 右键 Open | 文件用默认应用打开；文件夹打开或激活 |
| 右键 Reveal | 打开所在位置 |
| 右键 Copy Path | 复制路径 |
| 右键 Delete | 删除 Clipboard 记录 |

文件类逻辑复用现有服务：

```text
FileActionService
FolderActivationService
DragDropService
FileIconService
SystemHiddenFilter
```

### 4.3 多文件条目

展示：

```text
[Files] 3 files copied
        2026-05-06 14:15 · E:\Project\
```

操作：

| 操作 | 行为 |
|---|---|
| 双击 | 展开详情或打开第一个文件 |
| Enter | 展开详情 |
| 拖到 Favorites | 收藏整个路径组 |
| 拖到其他应用 | 传递全部真实路径 |
| 右键 Copy Paths | 复制全部路径 |
| 右键 Open First | 打开第一个 |
| 右键 Delete | 删除记录 |

### 4.4 图片条目

展示：

```text
[Image thumbnail] Screenshot 1280×720
                  2026-05-06 14:20 · 341 KB
```

操作：

| 操作 | 行为 |
|---|---|
| 双击 | 复制图片回剪贴板 |
| Enter | 复制图片回剪贴板 |
| Space | 打开图片预览 |
| 拖到 Favorites | 收藏 |
| 拖到其他应用 | 当作图片文件拖放 |
| 右键 Copy Image | 复制图片回剪贴板 |
| 右键 Save As | 另存为图片 |
| 右键 Delete | 删除记录 |

关键规则：

```text
图片拖放到应用窗口时，不直接传 Bitmap DataObject，而是传一个临时图片文件路径。
```

原因：多数 Windows 应用对 FileDrop 图片文件兼容性更好；直接拖 Bitmap 的兼容性不稳定。

临时图片路径：

```text
%LOCALAPPDATA%\Recents\clipboard\drag-temp\{itemId}.png
```

拖拽开始前确保文件存在；拖拽结束后不立即删除，放入后台清理队列，避免目标应用延迟读取失败。

建议清理策略：

```text
drag-temp 文件保留 24 小时
启动时清理超过 24 小时的临时文件
```

-- 讨论项：blob的图片文件是否可以直接作为拖放文件

### 4.5 HTML 条目

展示：

```text
[HTML] Copied webpage fragment
       2026-05-06 14:22 · 2.3 KB · example.com
```

操作：

| 操作 | 行为 |
|---|---|
| 双击 | 复制 HTML + PlainText 回剪贴板 |
| Enter | 复制回剪贴板 |
| Space | WebView2 预览 HTML |
| 拖到 Favorites | 收藏 |
| 拖到其他应用 | 优先拖 HTML 文件；可选拖纯文本 |
| 右键 Copy as HTML | 复制 HTML |
| 右键 Copy as Plain Text | 复制纯文本 |
| 右键 Save as HTML | 保存为 HTML 文件 |
| 右键 Delete | 删除记录 |

HTML 拖放策略：

```text
默认拖放为 .html 临时文件
Shift + 拖放：拖放为纯文本
```

临时 HTML 文件：

```text
%LOCALAPPDATA%\Recents\clipboard\drag-temp\{itemId}.html
```

-- 讨论项：blob的html文件是否可以直接作为拖放文件

### 4.6 Rich Text / RTF 条目

展示：

```text
[Rich Text] Formatted text fragment
            2026-05-06 14:23 · 512 chars
```

操作：

| 操作 | 行为 |
|---|---|
| 双击 | 复制 RTF + PlainText 回剪贴板 |
| Enter | 复制回剪贴板 |
| Space | 预览富文本 |
| 拖到 Favorites | 收藏 |
| 拖到其他应用 | 拖为 .rtf 临时文件 |
| 右键 Copy as Rich Text | 复制 RTF |
| 右键 Copy as Plain Text | 复制纯文本 |
| 右键 Save as RTF | 保存为 RTF 文件 |
| 右键 Delete | 删除记录 |

---

## 5. 拖放设计

### 5.1 拖到 Favorites

所有 Clipboard item 都支持拖到 Favorites。

行为：

```text
Clipboard item 拖到 Favorites 区域
→ 创建副本并持久化
→ 设置 IsFavorite = true
→ Favorites 列表即时刷新
→ 原 Clipboard 列表中保留该条目
```

如果拖放的是多文件条目，Favorites 中作为一个组合项展示，而不是拆成多个文件。

### 5.2 拖到外部应用

拖放行为按类型处理：

| 类型 | DataObject |
|---|---|
| Text | UnicodeText + Text |
| Files/Folders | FileDrop 真实路径列表 |
| Image | FileDrop 临时 PNG 文件 |
| HTML | FileDrop 临时 HTML 文件 + UnicodeText fallback |
| RTF | FileDrop 临时 RTF 文件 + UnicodeText fallback |
| Mixed | 按最稳定格式 fallback；优先 FileDrop |

实现入口：

```text
ClipboardDragDropService
```

建议接口：

```csharp
public interface IClipboardDragDropService
{
    DataObject CreateDataObject(ClipboardItem item, ClipboardDragOptions options);
    Task PrepareDragFilesAsync(ClipboardItem item, CancellationToken ct);
}
```

### 5.3 拖放临时文件生命周期

-- 讨论项：blob的文件是否可以直接作为拖放文件

目录：

```text
%LOCALAPPDATA%\Recents\clipboard\drag-temp\
```

策略：

- 拖拽前生成。
- 拖拽过程中不删除。
- 拖拽结束后不立即删除。
- 启动时清理超过 24 小时的临时文件。
- 手动 Clear Clipboard History 时同步清理 drag-temp。

### 5.4 内部拖放识别

拖到 Favorites 时不应触发外部文件拖放逻辑。

内部拖放使用自定义格式：

```text
Recents.ClipboardItemId
```

外部拖放同时放入标准格式：

```text
UnicodeText
FileDrop
HTML Format
RTF
```

Favorites 区域优先识别 `Recents.ClipboardItemId`。

---

## 6. Pop Paste 设计

### 6.1 产品定义

Pop Paste 是一个小型弹窗，用于快速搜索并粘贴历史剪贴内容。

默认快捷键建议（在设置中可配置）：

```text
Alt + Shift + V
```

不要使用 `Win + V`，避免和系统剪贴板冲突。

### 6.2 弹出流程

```text
用户按 Pop Paste 快捷键
→ 记录当前 foreground window hwnd
→ 弹出 ClipboardPopupWindow
→ 自动聚焦搜索框
→ 用户输入关键词
→ 上下选择条目
→ Enter 执行动作
```

### 6.3 Enter 行为

设置项：

```text
Pop Paste enter behavior
- Copy only
- Paste to active app
```

默认：

```text
Copy only
```

原因：自动粘贴依赖 `SendInput Ctrl+V`，在管理员窗口、远程桌面、游戏、安全软件中可能失败。

### 6.4 自动粘贴流程

```text
选择 Clipboard item
→ ClipboardPasteService.SetClipboard(item)
→ 激活原 foreground window
→ 延迟 80-150ms
→ SendInput Ctrl+V
→ 关闭 popup
```

### 6.5 粘贴后恢复剪贴板

设置项：

```text
Restore previous clipboard after paste
```

默认：

```text
false
```

可选高级行为：

```text
粘贴后 800ms 恢复原剪贴板
大图片、大文件、HTML、RTF 不自动恢复
```

### 6.6 防止自触发记录

Recents 自己写入剪贴板时，会触发剪贴板更新事件。必须抑制自触发。

策略：

```text
设置剪贴板前记录 suppress hash
接下来 500ms 内收到相同 hash 的剪贴板事件则忽略
```

---
---

## 8. 数据模型

### 8.1 ClipboardPayloadType

```csharp
public enum ClipboardPayloadType
{
    Text,
    Files,
    Folders,
    MixedFiles,
    Image,
    Html,
    RichText,
    Unknown
}
```

### 8.2 ClipboardItem

```csharp
public sealed class ClipboardItem
{
    public string Id { get; set; } = "";
    public ClipboardPayloadType Type { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime LastUsedAt { get; set; }

    public string Hash { get; set; } = "";
    public string PreviewText { get; set; } = "";
    public int UseCount { get; set; }

    public string? PlainText { get; set; }
    public int? TextLength { get; set; }

    public List<string> FilePaths { get; set; } = new();

    public string? BlobPath { get; set; }
    public string? HtmlPath { get; set; }
    public string? RawHtmlPath { get; set; }
    public string? RtfPath { get; set; }
    public string? ImagePath { get; set; }
    public string? ThumbnailPath { get; set; }

    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }
    public long? SizeBytes { get; set; }

    public bool IsFavorite { get; set; }
    public bool IsPinned { get; set; }
    public bool IsDeleted { get; set; }
}
```

### 8.3 AppSettings 新增字段

```csharp
public bool EnableClipboardHistory { get; set; } = false;
public int MaxClipboardItems { get; set; } = 500;
public int ClipboardRetentionDays { get; set; } = 30;

public bool CaptureTextClipboard { get; set; } = true;
public bool CaptureFileClipboard { get; set; } = true;
public bool CaptureImageClipboard { get; set; } = true;
public bool CaptureHtmlClipboard { get; set; } = true;
public bool CaptureRichTextClipboard { get; set; } = true;

public int MaxTextChars { get; set; } = 50000;
public long MaxImageBytes { get; set; } = 20 * 1024 * 1024;

public bool IgnoreSensitiveText { get; set; } = true;
public bool IgnoreSystemAndHiddenFilesInClipboard { get; set; } = true;

public string PopPasteHotkey { get; set; } = "Alt+Shift+V";
public string PopPasteEnterBehavior { get; set; } = "CopyOnly";
public bool RestoreClipboardAfterPaste { get; set; } = false;
```

---

## 9. 存储方案

使用 SQLite + blob 文件，不要把图片、HTML、RTF 大内容塞进 JSON。

目录结构：

```text
%APPDATA%\Recents\clipboard\
  clipboard.db
  blobs\
    {id}.txt
    {id}.html
    {id}.cfhtml
    {id}.rtf
  images\
    {id}.png
  thumbs\
    {id}.jpg
```

临时拖放文件：

```text
%LOCALAPPDATA%\Recents\clipboard\drag-temp\
  {id}.png
  {id}.html
  {id}.rtf
```

SQLite 表：

```sql
CREATE TABLE clipboard_items (
  id TEXT PRIMARY KEY,
  type TEXT NOT NULL,
  created_at TEXT NOT NULL,
  last_used_at TEXT,
  hash TEXT NOT NULL,
  preview_text TEXT,
  plain_text TEXT,
  text_length INTEGER,
  blob_path TEXT,
  html_path TEXT,
  raw_html_path TEXT,
  rtf_path TEXT,
  image_path TEXT,
  thumbnail_path TEXT,
  image_width INTEGER,
  image_height INTEGER,
  size_bytes INTEGER,
  is_favorite INTEGER NOT NULL DEFAULT 0,
  is_pinned INTEGER NOT NULL DEFAULT 0,
  use_count INTEGER NOT NULL DEFAULT 0,
  is_deleted INTEGER NOT NULL DEFAULT 0
);

CREATE UNIQUE INDEX idx_clipboard_items_hash
ON clipboard_items(hash);

CREATE INDEX idx_clipboard_items_created_at
ON clipboard_items(created_at DESC);

CREATE TABLE clipboard_files (
  item_id TEXT NOT NULL,
  path TEXT NOT NULL,
  ordinal INTEGER NOT NULL,
  PRIMARY KEY (item_id, ordinal)
);
```

---

## 10. 捕获实现

### 10.1 剪贴板监听

使用：

```text
AddClipboardFormatListener
WM_CLIPBOARDUPDATE
```

服务：

```text
ClipboardMonitorService
ClipboardCaptureService
ClipboardHistoryService
ClipboardStoreService
ClipboardFilterService
```

流程：

```text
WM_CLIPBOARDUPDATE
→ debounce 100ms
→ 判断 EnableClipboardHistory
→ 读取 IDataObject
→ 识别格式
→ 捕获内容
→ 过滤敏感/超限/重复
→ 写入内存
→ 异步写入 SQLite / blob
→ UI 刷新
```

### 10.2 格式优先级

同一次剪贴板可能包含多种格式。

建议优先级：

```text
FileDrop
Image
HTML
RTF
UnicodeText
Text
Unknown
```

说明：

- FileDrop 优先，因为文件/文件夹是 Recents 的核心场景。
- HTML 通常也有 PlainText fallback，但应保存 HTML。
- RTF 通常也有 PlainText fallback，应保存 RTF。
- Text 作为最低稳定 fallback。

### 10.3 文本捕获

读取：

```csharp
Clipboard.ContainsText()
Clipboard.GetText()
```

限制：

```text
超过 MaxTextChars 不保存完整内容，只保存预览和提示
```

### 10.4 文件捕获

读取：

```csharp
Clipboard.ContainsFileDropList()
Clipboard.GetFileDropList()
```

规则：

- 文件/文件夹只保存路径。
- 不复制文件本体。
- 不读取文件内容。
- 目标路径不存在也可保存，但标记 missing。
- 系统/隐藏文件过滤规则可应用。

### 10.5 图片捕获

读取：

```csharp
Clipboard.ContainsImage()
Clipboard.GetImage()
```

保存：

```text
images/{id}.png
thumbs/{id}.jpg
```

规则：

- 超过 MaxImageBytes 可拒绝保存。
- 缩略图用于列表展示。
- 原图用于预览、复制回剪贴板和拖放临时文件。

### 10.6 HTML 捕获

读取格式：

```text
DataFormats.Html
HTML Format
```

保存：

```text
raw CF_HTML
fragment HTML
plain text fallback
```

预览：

- 用 WebView2。
- 禁用脚本。
- 清理 script、iframe、object、embed。
- 默认不加载远程资源。

### 10.7 RTF 捕获

读取：

```text
DataFormats.Rtf
```

保存：

```text
raw RTF
plain text fallback
```

预览：

- 使用 RichTextBox 只读预览，或转换为简化 HTML 后 WebView2 预览。
- 首选 RichTextBox，稳定性更高。

---

## 11. 复制回剪贴板实现

### 11.1 文本

```csharp
Clipboard.SetText(item.PlainText ?? "");
```

### 11.2 文件 / 文件夹

```csharp
var collection = new StringCollection();
collection.AddRange(item.FilePaths.ToArray());
Clipboard.SetFileDropList(collection);
```

### 11.3 图片

```csharp
var image = LoadBitmapSource(item.ImagePath);
Clipboard.SetImage(image);
```

### 11.4 HTML

```csharp
var dataObject = new DataObject();
dataObject.SetData(DataFormats.Html, rawCfHtml);
dataObject.SetData(DataFormats.UnicodeText, plainText);
Clipboard.SetDataObject(dataObject, true);
```

### 11.5 RTF

```csharp
var dataObject = new DataObject();
dataObject.SetData(DataFormats.Rtf, rawRtf);
dataObject.SetData(DataFormats.UnicodeText, plainText);
Clipboard.SetDataObject(dataObject, true);
```

---

## 12. UI 设计


### 12.3 右键菜单

通用：

```text
Copy
Paste
Toggle Pin
Delete
```

文本额外：

```text
Copy as Plain Text
```

图片额外：

```text
Copy Image
Save Image As...
```

HTML 额外：

```text
Copy as HTML
Copy as Plain Text
Save as HTML...
```

RTF 额外：

```text
Copy as Rich Text
Copy as Plain Text
Save as RTF...
```

文件额外：

```text
Open
Reveal in Explorer
Copy Path
```

---

## 13. 隐私和过滤

### 13.1 敏感文本过滤

默认开启：

```text
Ignore sensitive text
```

建议过滤：

```text
password=
token=
secret=
api_key=
private key
BEGIN RSA PRIVATE KEY
BEGIN OPENSSH PRIVATE KEY
Authorization: Bearer
```

疑似敏感内容不保存，并可在日志中只记录：

```text
Clipboard item skipped by sensitive filter
```

不要记录原文。

### 13.2 系统/隐藏路径过滤

设置项：

```text
Ignore system and hidden files in clipboard
```

关闭显示系统/隐藏文件时，文件类剪贴板忽略：

- Windows System 属性文件
- Windows Hidden 属性文件
- 位于 `.git`、`.claude`、`.obsidian`、`.vscode` 等点号目录下的文件

收藏项例外。

### 13.3 私密模式

提供：

```text
Pause clipboard history for 10 minutes
Pause clipboard history for 1 hour
Resume clipboard history
```

暂停期间不记录新内容。

---

## 14. 服务结构

```text
Services/
  ClipboardMonitorService.cs
  ClipboardCaptureService.cs
  ClipboardHistoryService.cs
  ClipboardStoreService.cs
  ClipboardFilterService.cs
  ClipboardDragDropService.cs
  ClipboardPasteService.cs
  ClipboardImageService.cs
  ClipboardHtmlService.cs
  ClipboardRichTextService.cs
  ClipboardTempFileService.cs

Models/
  ClipboardItem.cs
  ClipboardPayloadType.cs
  ClipboardCaptureResult.cs
  ClipboardDragOptions.cs
  TextTransformAction.cs
  TextTransformResult.cs

ViewModels/
  ClipboardViewModel.cs
  ClipboardItemViewModel.cs
  ClipboardPopupViewModel.cs

Views/
  ClipboardView.xaml
  ClipboardPopupWindow.xaml
  ClipboardPreviewWindow.xaml
```

---

## 15. 性能要求

```text
主窗口呼出：< 100ms
Clipboard Tab 切换：< 100ms
搜索 1000 条历史：无明显卡顿
图片缩略图异步加载
SQLite 查询分页
大 blob 不在列表渲染时读取
拖放临时文件按需生成
```

列表默认只加载最近 200 条，向下滚动分页加载。

---

## 16. 错误处理

必须处理：

- 剪贴板被其他程序占用
- Clipboard.GetDataObject 失败
- 图片保存失败
- HTML 格式解析失败
- RTF 读取失败
- SQLite 损坏
- blob 文件丢失
- 临时拖放文件生成失败
- SendInput 粘贴失败
- 目标文件不存在

处理原则：

- 单条失败不影响应用。
- 捕获失败只跳过本次。
- blob 丢失时条目显示为 missing。
- 自动粘贴失败时保留已复制状态，提示用户手动 Ctrl+V。

---

## 17. 设置页新增项

```text
Clipboard

[ ] Enable clipboard history

Capture
[x] Text
[x] Files and folders
[x] Images
[x] HTML
[x] Rich text

Limits
Max clipboard items: 500
Retention days: 30
Max text chars: 50000
Max image size: 20 MB

Privacy
[x] Ignore sensitive text
[x] Ignore system and hidden files
[ ] Pause clipboard history

Pop Paste
Hotkey: Alt + Shift + V
Enter behavior: Copy only / Paste to active app
[ ] Restore previous clipboard after paste

Maintenance
Clear clipboard history
Open clipboard data folder
Clear drag temp files
```

---

## 18. 验收标准

### 18.1 捕获

- [ ] 开启 Clipboard History 后，复制文本会出现在 Clipboard Tab。
- [ ] 复制文件会生成 Files 类型条目。
- [ ] 复制文件夹会生成 Folders 类型条目。
- [ ] 复制图片会生成 Image 类型条目并显示缩略图。
- [ ] 复制网页富文本会优先保存 HTML。
- [ ] 复制 RTF 内容会保存 Rich Text。
- [ ] 重复复制同一内容不会新增重复条目。
- [ ] 关闭 Clipboard History 后不再记录新内容。

### 18.2 复用

- [ ] 双击文本条目可复制回剪贴板。
- [ ] 双击文件条目可打开文件。
- [ ] 双击文件夹条目可打开或激活 Explorer。
- [ ] 图片条目可复制回剪贴板。
- [ ] HTML 条目可复制 HTML + PlainText 回剪贴板。
- [ ] RTF 条目可复制 RTF + PlainText 回剪贴板。

### 18.3 拖放

- [ ] 文本 item 可拖放到支持文本的应用。
- [ ] 文件 item 可拖放到其他应用，传递真实文件路径。
- [ ] 文件夹 item 可拖放到其他应用，传递真实文件夹路径。
- [ ] 图片 item 拖放到其他应用时按 PNG 文件处理。
- [ ] HTML item 拖放到其他应用时按 HTML 文件处理。
- [ ] RTF item 拖放到其他应用时按 RTF 文件处理。
- [ ] 所有 Clipboard item 可拖放到 Favorites 收藏。

### 18.4 搜索与筛选

- [ ] 搜索能匹配文本内容。
- [ ] 搜索能匹配文件名和路径。
- [ ] 搜索能匹配 HTML / RTF 的 plain text fallback。
- [ ] 类型筛选 All / Text / Files / Folders / Images / HTML / Rich Text 可用。
- [ ] 收藏项显示在 Favorites。

### 18.5 Pop Paste

- [ ] Alt + Shift + V 呼出 Pop Paste。
- [ ] Pop Paste 可搜索历史。
- [ ] Enter 默认复制所选 item。
- [ ] 设置为 Paste to active app 后，Enter 会尝试粘贴到原活动窗口。
- [ ] 自动粘贴失败时不崩溃，并提示用户手动 Ctrl+V。
- [ ] Recents 自己复制/粘贴历史 item 时，不产生重复历史。

### 18.6 隐私

- [ ] 默认 Clipboard History 关闭。
- [ ] Clear clipboard history 删除索引和 blob。
- [ ] 敏感文本默认不保存。
- [ ] 系统/隐藏/点号目录文件默认不保存，除非已收藏。
- [ ] 所有数据仅保存在本地。

---

