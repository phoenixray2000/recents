using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using Serilog;

namespace Recents.App.Services;

public enum SingleInstanceCommandKind
{
    ShowMainWindow,
    PreviewPath,
}

public sealed record SingleInstanceCommand(SingleInstanceCommandKind Kind, string? Path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static SingleInstanceCommand ShowMainWindow() =>
        new(SingleInstanceCommandKind.ShowMainWindow, null);

    public static SingleInstanceCommand PreviewPath(string path) =>
        new(SingleInstanceCommandKind.PreviewPath, path);

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static bool TryParse(string message, out SingleInstanceCommand command)
    {
        command = ShowMainWindow();

        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (string.Equals(message.Trim(), "show", StringComparison.OrdinalIgnoreCase))
        {
            command = ShowMainWindow();
            return true;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<SingleInstanceCommand>(message, JsonOptions);
            if (parsed is null)
                return false;

            command = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

// PRD §13 单实例保证。
// 用命名 Mutex 检测已有实例；若已有则通过命名管道发「show」信号后退出。
// 首实例在后台保持管道服务器，持续监听「show」信号并转发给主窗口。
public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Global\Recents.SingleInstance";
    private const string PipeName  = "Recents.ShowWindow";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private bool _isFirst;

    // 当收到第二实例「show」信号时触发；UI 层订阅并显示主窗口。
    public event Action? ShowWindowRequested;
    public event Action<SingleInstanceCommand>? CommandRequested;

    // 返回 true 表示本进程是首实例，可继续启动；false 表示已有实例，应退出。
    public bool TryClaimInstance(SingleInstanceCommand? startupCommand = null)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        _isFirst = createdNew;

        if (!createdNew)
        {
            // 通知首实例执行命令，然后本进程退出
            TrySendCommand(startupCommand ?? SingleInstanceCommand.ShowMainWindow());
        }
        else
        {
            // 首实例：启动管道服务器监听后续实例的通知
            _cts = new CancellationTokenSource();
            StartPipeServer(_cts.Token);
        }

        return _isFirst;
    }

    // 向首实例管道发送「show」信号（带 2s 超时，失败静默）
    private static void TrySendCommand(SingleInstanceCommand command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            using var writer = new StreamWriter(client);
            writer.Write(command.Serialize());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SingleInstance: 发送命令失败");
        }
    }

    // 首实例后台循环：接受管道连接，读取「show」信号，触发事件
    private void StartPipeServer(CancellationToken ct)
    {
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        transmissionMode: PipeTransmissionMode.Byte,
                        options: PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    var msg = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    if (SingleInstanceCommand.TryParse(msg, out var command))
                    {
                        Log.Information("SingleInstance: 收到第二实例命令 {Kind}", command.Kind);
                        CommandRequested?.Invoke(command);

                        if (command.Kind == SingleInstanceCommandKind.ShowMainWindow)
                            ShowWindowRequested?.Invoke();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "SingleInstance: 管道服务器异常");
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        if (_isFirst)
            _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
