# TCast Desktop

基于 .NET 10 / WPF 的 TCL/雷鸟电视 Windows 桌面客户端，提供局域网电视发现、遥控、APK 本地安装与诊断日志。

## 使用要求

- Windows x64，电脑与支持 TCast 协议的电视位于同一局域网。
- 选择与电视同网段的 IPv4 网卡后发现设备，或输入电视 IP 连接；切换控制目标前需先断开。
- APK 安装要求电视能够访问本机所选网卡上的 HTTP 服务。请仅在受信任的专用网络中放行防火墙；应用不会自动修改防火墙规则，也不要求管理员权限。

## 构建与运行

在 Windows 上安装 .NET 10 SDK，以下命令均在本仓库根目录执行：

```powershell
dotnet restore ./TCast.Desktop.sln --configfile ./NuGet.config
dotnet build ./TCast.Desktop.sln --configuration Release --no-restore
dotnet run --project ./src/TCast.Desktop/TCast.Desktop.csproj
```

依赖版本由 `Directory.Packages.props` 集中管理，`NuGet.config` 仅使用 nuget.org。构建将警告视为错误。

## 测试与检查

```powershell
dotnet test ./TCast.Desktop.sln --configuration Release --no-build --no-restore --logger trx --results-directory ./artifacts/test-results
dotnet format analyzers ./TCast.Desktop.sln --verify-no-changes --no-restore --severity warn
```

测试覆盖协议编解码、网络回环、APK 解析与传输、诊断及 WPF 界面逻辑。WPF 测试需要 Windows 环境。

## GitHub Actions

[Windows build](.github/workflows/build.yml) 在推送、Pull Request 和手动触发时运行：

1. 安装 .NET 10 SDK，还原依赖并执行 Release 构建。
2. 运行测试及分析器检查，上传 TRX 测试结果。
3. 发布并上传 `win-x64` 框架依赖版和自包含版。

成功运行后，可在 Actions 的 **Artifacts** 中下载发布包；测试结果和发布包保留 14 天。

推送任意 tag 时，构建、测试及分析器检查全部通过后，会自动创建对应 tag 的正式 **GitHub Release**（非草稿、非预发布），自动生成发布说明并附带以下 ZIP：

- `TCast-Desktop-win-x64-framework-dependent.zip`：框架依赖版。
- `TCast-Desktop-win-x64-self-contained.zip`：自包含版，无需另装 .NET 运行时。

在包含 workflow 的提交上创建并推送 tag，例如：

```powershell
git tag v0.1.0
git push origin v0.1.0
```

普通分支推送、Pull Request 和手动运行不会创建 Release。重新运行 tag 工作流会更新同名附件；若仓库启用了不可变 Release，已发布的附件无法覆盖，应使用新 tag 发布。Release 附件不受 Actions 的 14 天保留期限制。tag 用于 Release 标识，应用版本仍由 `Directory.Build.props` 管理，发布前应同步更新。发布包不包含安装器或代码签名。

## 发布

框架依赖版：目标机需安装 x64 的 **.NET 10 Desktop Runtime 与 ASP.NET Core 10 Runtime**，后者用于 Kestrel HTTP 服务。

```powershell
dotnet publish ./src/TCast.Desktop/TCast.Desktop.csproj --configuration Release --runtime win-x64 --self-contained false --output ./artifacts/publish/win-x64-framework-dependent
```

自包含版：目标机无需单独安装 .NET 运行时。

```powershell
dotnet publish ./src/TCast.Desktop/TCast.Desktop.csproj --configuration Release --runtime win-x64 --self-contained true --output ./artifacts/publish/win-x64-self-contained
```

分发对应输出目录的完整内容，运行其中的 `TCast.Desktop.exe`。APK 解析使用内置 .NET API，无需附带 `aapt2` 或其他外部解析工具。构建、测试和发布产物不会提交到仓库。

## 功能限制

- 仅支持本地完整 APK，不自动合并 split APK；拒绝 ZIP64、文本清单和无法安全判定的容器结构，默认文件上限为 1 GiB。
- APK 资源仅解析默认配置；不支持或缺失的名称资源回退为包名，版本名称回退为空。
- 发送安装命令后不等待电视回包，结果以电视显示为准；下载进度达到 100% 也不等于安装成功。停止本地文件服务不会取消电视安装。
- 同一连接可重复发送同一文件；本地文件服务与端口保留到手动停止或下一次发送，期间切换网卡与连接仍受限制。
- 遥控发送成功仅表示本地网络写入完成，不代表电视已执行。连接长时间无响应时会提示，但不会自动断开。
- 诊断日志在写入前脱敏，内存最多保留 500 条，支持筛选和导出。
- 自动化测试使用回环网络及合成 APK 样本，不替代真实设备验收。发布前仍需验证真实 APK 与电视兼容性、多网卡及防火墙场景、不同 DPI、输入法和干净目标机运行情况；最低 Windows 版本尚待确认。

## 项目结构

```text
.github/workflows/            GitHub Actions 构建与发布产物
src/TCast.Protocol/           协议编解码与命令模型
src/TCast.Infrastructure/     发现、连接、APK、HTTP 与诊断
src/TCast.Desktop/            WPF 界面与应用入口
tests/                       协议、基础设施与桌面测试
scripts/Generate-AppIcon.ps1  从 WPF 主题生成图标资源
```

图标资源已包含在源码中，正常构建无需重新生成。修改主题中的电视标识后，可运行：

```powershell
powershell -NoProfile -STA -File ./scripts/Generate-AppIcon.ps1
```
