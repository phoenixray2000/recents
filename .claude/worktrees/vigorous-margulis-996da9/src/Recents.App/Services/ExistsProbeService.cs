namespace Recents.App.Services;

// PRD §6.20 文件存在性校验。
// File.Exists 在断网网络盘上会卡 5–30s，禁止在 UI 线程或同步路径调用。
// 实现：
//   - 索引中 ExistsState 三态（Missing / Exists / Unknown）
//   - 仅对进入可视区域的文件项触发后台 1.5s 超时探测
//   - UNC 整源不可达时整源批量标 Unknown，不再逐文件探测
public class ExistsProbeService
{
}
