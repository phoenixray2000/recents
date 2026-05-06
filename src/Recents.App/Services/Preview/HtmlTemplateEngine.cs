using Markdig;
using System.IO;
using System.Net;
using System.Text;

namespace Recents.App.Services.Preview;

public static class HtmlTemplateEngine
{
    // ── 公共 CSS 变量（与 PRD §7.4 颜色 token 对齐）──────────────────────
    private const string BaseStyle = """
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <style>
        :root {
            --bg:        #101216;
            --surface:   #191D26;
            --border:    #2B313D;
            --text:      #F3F4F6;
            --muted:     #A7ADBA;
            --tertiary:  #7E8491;
            --accent:    #3B82F6;
            --code-bg:   #151922;
            --success:   #63C554;
            --warning:   #F5B642;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        html, body { width: 100%; height: 100%; overflow: auto; }
        body {
            background: var(--bg);
            color: var(--text);
            font-family: "Segoe UI Variable Display", "Segoe UI", system-ui, sans-serif;
            font-size: 14px;
            line-height: 1.6;
        }
        a { color: var(--accent); }
        
        /* 自定义滚动条，与 WPF 样式一致 */
        ::-webkit-scrollbar {
            width: 10px;
            height: 10px;
            background: var(--bg);
        }
        ::-webkit-scrollbar-thumb {
            background: var(--tertiary);
            border-radius: 4px;
            border: 3px solid var(--bg); /* 模拟 WPF 的 Margin(3,2) */
        }
        ::-webkit-scrollbar-thumb:hover {
            background: var(--muted);
        }
        ::-webkit-scrollbar-corner {
            background: var(--bg);
        }
        </style>
        """;

    // ── 图片 ──────────────────────────────────────────────────────────────
    /// <param name="virtualUri">虚拟主机 URI，格式 https://preview.local/… 或 file://…</param>
    public static string RenderImage(string virtualUri, string fileName)
    {
        return $$"""
            <!DOCTYPE html><html><head>{{BaseStyle}}
            <style>
            body { margin: 0; padding: 0; height: 100vh; display: flex; flex-direction: column; background: #000; overflow: hidden; }
            .content { flex: 1; position: relative; overflow: hidden; cursor: grab; display: flex; align-items: center; justify-content: center; }
            .content:active { cursor: grabbing; }
            img { max-width: 100%; max-height: 100%; object-fit: contain; user-select: none; -webkit-user-drag: none; transition: transform 0.1s ease-out; }
            </style>
            </head><body>
            <div class="content" id="container">
              <img src="{{virtualUri}}" id="preview-img" alt="{{WebUtility.HtmlEncode(fileName)}}"
                   onerror="this.parentNode.innerHTML='<p style=color:var(--warning)>Image failed to load.</p>'">
            </div>
            <script>
            const container = document.getElementById('container');
            const img = document.getElementById('preview-img');
            
            let scale = 1;
            let translateX = 0;
            let translateY = 0;
            let isDragging = false;
            let startX, startY;

            img.onload = () => {
              if (window.chrome && window.chrome.webview) {
                const dim = `${img.naturalWidth} × ${img.naturalHeight}`;
                window.chrome.webview.postMessage({ type: 'dimensions', value: dim });
              }
            };

            function updateTransform() {
              img.style.transform = `translate(${translateX}px, ${translateY}px) scale(${scale})`;
            }

            container.addEventListener('wheel', e => {
              e.preventDefault();
              if (e.ctrlKey) {
                const delta = e.deltaY < 0 ? 1.1 : 0.9;
                const nextScale = Math.max(0.1, Math.min(20, scale * delta));
                scale = nextScale;
              } else {
                translateX -= e.deltaX;
                translateY -= e.deltaY;
              }
              updateTransform();
            }, { passive: false });

            container.addEventListener('mousedown', e => {
              if (e.button !== 0) return;
              isDragging = true;
              startX = e.clientX - translateX;
              startY = e.clientY - translateY;
              img.style.transition = 'none';
            });

            window.addEventListener('mousemove', e => {
              if (!isDragging) return;
              translateX = e.clientX - startX;
              translateY = e.clientY - startY;
              updateTransform();
            });

            window.addEventListener('mouseup', () => {
              isDragging = false;
              img.style.transition = 'transform 0.1s ease-out';
            });
            
            container.addEventListener('dblclick', () => {
                scale = 1;
                translateX = 0;
                translateY = 0;
                updateTransform();
            });
            </script>
            </body></html>
            """;
    }

    // ── 文本 / 代码（无语法高亮，P0 阶段）──────────────────────────────
    public static string RenderText(string content, string fileName, bool isCode)
    {
        var escaped = WebUtility.HtmlEncode(content);
        var lang = isCode ? Path.GetExtension(fileName).TrimStart('.') : "";
        return $$"""
            <!DOCTYPE html><html><head>{{BaseStyle}}
            <style>
            body { padding: 0; }
            .toolbar {
                position: sticky; top: 0; background: var(--surface);
                border-bottom: 1px solid var(--border);
                padding: 6px 12px; display:flex; gap:8px; align-items:center;
                font-size: 12px; color: var(--muted); z-index:10;
            }
            .toolbar span { flex:1; }
            button {
                background: transparent; border: 1px solid var(--border);
                color: var(--text); border-radius:4px; padding:2px 8px;
                cursor:pointer; font-size:11px;
            }
            button:hover { background: var(--border); }
            pre {
                margin: 0; padding: 12px 16px;
                font-family: "Cascadia Code","Consolas","Fira Mono",monospace;
                font-size: 13px; line-height: 1.5; white-space: pre-wrap;
                word-break: break-word; background: var(--code-bg); color: var(--text);
                min-height: calc(100vh - 36px);
            }
            </style>
            </head><body>
            <div class="toolbar">
              <span>{{WebUtility.HtmlEncode(fileName)}}{{(string.IsNullOrEmpty(lang) ? "" : $" · {lang}")}}</span>
              <button onclick="navigator.clipboard.writeText(document.querySelector('pre').innerText)">Copy</button>
            </div>
            <pre>{{escaped}}</pre>
            </body></html>
            """;
    }

    // ── CSV（表格视图，最多 500 行）─────────────────────────────────────
    public static string RenderCsv(string content, string fileName)
    {
        var rows = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Take(500).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head>" + BaseStyle + """
            <style>
            body { padding: 0; overflow-x: auto; }
            .toolbar { position:sticky;top:0;background:var(--surface);
                border-bottom:1px solid var(--border);padding:6px 12px;
                font-size:12px;color:var(--muted);z-index:10; }
            table { border-collapse:collapse; width:100%; font-size:13px; }
            th { background:var(--surface);border:1px solid var(--border);
                 padding:4px 8px;text-align:left;font-weight:600;color:var(--accent);
                 position:sticky;top:36px; }
            td { border:1px solid var(--border);padding:4px 8px; }
            tr:nth-child(even) { background:var(--surface); }
            </style></head><body>
            """);
        sb.AppendLine($"<div class='toolbar'>{WebUtility.HtmlEncode(fileName)} · {rows.Count} rows shown (max 500)</div>");
        sb.AppendLine("<table>");
        bool isHeader = true;
        foreach (var raw in rows)
        {
            var cells = ParseCsvLine(raw);
            sb.Append(isHeader ? "<thead><tr>" : "<tr>");
            foreach (var cell in cells)
            {
                var tag = isHeader ? "th" : "td";
                sb.Append($"<{tag}>{WebUtility.HtmlEncode(cell)}</{tag}>");
            }
            sb.AppendLine(isHeader ? "</tr></thead><tbody>" : "</tr>");
            isHeader = false;
        }
        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static string[] ParseCsvLine(string line)
    {
        // 极简 CSV 解析：处理带引号的字段
        var result = new List<string>();
        bool inQuote = false;
        var current = new StringBuilder();
        foreach (char ch in line)
        {
            if (ch == '"') { inQuote = !inQuote; }
            else if (ch == ',' && !inQuote) { result.Add(current.ToString()); current.Clear(); }
            else { current.Append(ch); }
        }
        result.Add(current.ToString());
        return result.ToArray();
    }

    // ── Markdown ─────────────────────────────────────────────────────────
    public static string RenderMarkdown(string content, string fileName)
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()   // 表格、删除线、任务列表等
            .Build();
        var body = Markdown.ToHtml(content, pipeline);
        return $$"""
            <!DOCTYPE html><html><head>{{BaseStyle}}
            <style>
            .md { max-width: 820px; margin: 0 auto; padding: 24px 32px; }
            h1,h2,h3,h4 { color:var(--accent); margin:1em 0 .4em; }
            h1 { font-size:1.6em; border-bottom:1px solid var(--border); padding-bottom:.3em; }
            h2 { font-size:1.3em; }
            p  { margin: .5em 0; }
            blockquote { border-left:3px solid var(--accent);
                         margin-left:0;padding-left:12px;color:var(--muted); }
            pre, code { font-family:"Cascadia Code","Consolas",monospace; }
            pre  { background:var(--code-bg);border-radius:6px;padding:12px;overflow-x:auto; }
            code { background:var(--code-bg);border-radius:3px;padding:2px 4px;font-size:.9em; }
            pre code { background:none;padding:0; }
            table { border-collapse:collapse;width:100%; }
            th,td { border:1px solid var(--border);padding:6px 10px;text-align:left; }
            th { background:var(--surface);color:var(--accent); }
            tr:nth-child(even) { background:var(--surface); }
            input[type=checkbox] { margin-right:4px; }
            hr { border:none;border-top:1px solid var(--border);margin:1.5em 0; }
            img { max-width:100%; }
            </style>
            </head><body>
            <div class="md">{{body}}</div>
            </body></html>
            """;
    }

    // ── 媒体（音频 / 视频）─────────────────────────────────────────────
    public static string RenderMedia(string virtualUri, string fileName, bool isVideo)
    {
        var tag = isVideo ? "video" : "audio";
        var attrs = isVideo
            ? """"controls autoplay style="max-width:100%;max-height:80vh;border-radius:6px;""""
            : """"controls autoplay style="width:100%;"""";
        return $$"""
            <!DOCTYPE html><html><head>{{BaseStyle}}
            <style>
            body { display:flex;flex-direction:column;align-items:center;
                   justify-content:center;min-height:100vh;gap:12px; }
            .name { color:var(--muted);font-size:13px; }
            </style>
            </head><body>
            <{{tag}} {{attrs}} src="{{virtualUri}}"
              onerror="this.outerHTML='<p style=color:var(--warning)>Cannot play this file format.</p>'">
            </{{tag}}>
            <div class="name">{{WebUtility.HtmlEncode(fileName)}}</div>
            </body></html>
            """;
    }

    // ── 不支持 ───────────────────────────────────────────────────────────
    public static string RenderUnsupported(string fileName, string ext) => $$"""
        <!DOCTYPE html><html><head>{{BaseStyle}}
        <style>body{display:flex;align-items:center;justify-content:center;
        min-height:100vh;flex-direction:column;gap:8px;}
        .icon{font-family:"Segoe Fluent Icons";font-size:48px;color:var(--muted);}
        .msg{color:var(--muted);font-size:14px;}
        .hint{color:var(--muted);font-size:12px;margin-top:4px;}
        </style></head><body>
        <div class="icon">&#xE8A5;</div>
        <div class="msg">This file type ({{WebUtility.HtmlEncode(ext)}}) cannot be previewed.</div>
        <div class="hint">Press <b>Enter</b> to open with the default app.</div>
        </body></html>
        """;

    public static string RenderFolder(FolderPreviewSummary summary)
    {
        var rows = new StringBuilder();
        foreach (var entry in summary.Entries)
        {
            var icon = entry.IsFolder ? "&#xE8B7;" : "&#xE8A5;";
            var type = entry.IsFolder ? "Folder" : "File";
            var size = entry.IsFolder ? "" : FormatSize(entry.Size ?? 0);
            rows.AppendLine($"""
                <tr>
                  <td class="name"><span class="item-icon">{icon}</span><span>{WebUtility.HtmlEncode(entry.Name)}</span></td>
                  <td>{type}</td>
                  <td class="size">{WebUtility.HtmlEncode(size)}</td>
                  <td>{WebUtility.HtmlEncode(entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm"))}</td>
                </tr>
                """);
        }

        var countSuffix = summary.IsTruncated ? "+" : "";
        var error = string.IsNullOrWhiteSpace(summary.ErrorMessage)
            ? ""
            : $"""<div class="warning">{WebUtility.HtmlEncode(summary.ErrorMessage)}</div>""";
        var empty = summary.Entries.Count == 0 && string.IsNullOrWhiteSpace(summary.ErrorMessage)
            ? """<div class="empty">This folder is empty.</div>"""
            : "";
        var table = summary.Entries.Count == 0
            ? ""
            : $$"""
              <table>
                <thead>
                  <tr><th>Name</th><th>Type</th><th>Size</th><th>Modified</th></tr>
                </thead>
                <tbody>{{rows}}</tbody>
              </table>
              """;

        return $$"""
            <!DOCTYPE html><html><head>{{BaseStyle}}
            <style>
            body{min-height:100vh;padding:18px 20px;}
            .header{display:flex;align-items:flex-start;gap:12px;margin-bottom:14px;}
            .folder-icon{font-family:"Segoe Fluent Icons";font-size:34px;color:var(--accent);line-height:1;}
            .title{font-size:18px;font-weight:600;line-height:1.2;word-break:break-word;}
            .path{color:var(--muted);font-size:12px;margin-top:4px;word-break:break-all;}
            .stats{display:flex;flex-wrap:wrap;gap:8px;margin:0 0 14px 46px;}
            .stat{border:1px solid var(--border);border-radius:6px;background:var(--surface);padding:5px 9px;color:var(--muted);}
            .stat b{color:var(--text);font-weight:600;}
            table{width:100%;border-collapse:collapse;font-size:13px;}
            th,td{border-bottom:1px solid var(--border);padding:7px 8px;text-align:left;vertical-align:middle;}
            th{color:var(--muted);font-weight:600;background:rgba(255,255,255,.025);position:sticky;top:0;}
            td{color:var(--muted);}
            .name{color:var(--text);display:flex;align-items:center;gap:8px;min-width:0;}
            .name span:last-child{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}
            .item-icon{font-family:"Segoe Fluent Icons";color:var(--muted);flex:0 0 auto;}
            .size{white-space:nowrap;}
            .hint,.empty,.warning{color:var(--muted);font-size:12px;margin-left:46px;margin-bottom:12px;}
            .warning{color:var(--warning);}
            </style></head><body>
            <div class="header">
              <div class="folder-icon">&#xE8B7;</div>
              <div>
                <div class="title">{{WebUtility.HtmlEncode(summary.Name)}}</div>
                <div class="path">{{WebUtility.HtmlEncode(summary.Path)}}</div>
              </div>
            </div>
            <div class="stats">
              <div class="stat"><b>{{summary.FolderCount}}{{countSuffix}}</b> folders</div>
              <div class="stat"><b>{{summary.FileCount}}{{countSuffix}}</b> files</div>
              <div class="stat"><b>{{summary.Entries.Count}}</b> shown</div>
            </div>
            <div class="hint">Press <b>Enter</b> to open in Explorer.</div>
            {{error}}
            {{empty}}
            {{table}}
            </body></html>
            """;
    }

    public static string RenderTooLarge(string fileName, long size) => $$"""
        <!DOCTYPE html><html><head>{{BaseStyle}}
        <style>body{display:flex;align-items:center;justify-content:center;
        min-height:100vh;flex-direction:column;gap:8px;}
        .icon{font-family:"Segoe Fluent Icons";font-size:48px;color:var(--warning);}
        .msg{color:var(--muted);font-size:14px;}
        </style></head><body>
        <div class="icon">&#xE898;</div>
        <div class="msg">File is too large to preview ({{FormatSize(size)}}).</div>
        <div class="hint" style="color:var(--muted);font-size:12px;">Press <b>Enter</b> to open.</div>
        </body></html>
        """;

    public static string RenderMissing(string path) => $$"""
        <!DOCTYPE html><html><head>{{BaseStyle}}
        <style>body{display:flex;align-items:center;justify-content:center;
        min-height:100vh;flex-direction:column;gap:8px;}
        .icon{font-family:"Segoe Fluent Icons";font-size:48px;color:var(--warning);}
        .msg{color:var(--muted);font-size:14px;}
        </style></head><body>
        <div class="icon">&#xE7BA;</div>
        <div class="msg">File not found.</div>
        </body></html>
        """;

    private static string FormatSize(long bytes) =>
        bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:F1} GB" :
        bytes >= 1_048_576     ? $"{bytes / 1_048_576.0:F1} MB" :
                                 $"{bytes / 1024.0:F1} KB";
}
