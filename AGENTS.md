# Project rules

## Build / Restore

- 默认不要运行 `dotnet restore`。
- 默认验证命令使用：
  `dotnet build Recents.sln --no-restore`
- 只有修改以下文件时才允许运行 `dotnet restore Recents.sln`：
  - `*.csproj`
  - `*.sln`
  - `Directory.Packages.props`
  - `Directory.Build.props`
  - `Directory.Build.targets`
  - `NuGet.config`
  - `packages.lock.json`
  - 新增、删除或修改 NuGet 包引用
- 普通 C#、XAML、UI、业务逻辑修改，不要 restore。 