# NuGet Manager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建 Windows Native AOT 桌面 NuGet 管理器，按工作目录或单项目查看、搜索、安装、卸载和安全批量升级依赖。

**Architecture:** 沿用一个 MewUI 可执行项目，使用 SDK 子进程和结构化输出处理项目及包操作，不增加托管后台。项目发现、包数据、事务编排和界面状态分离，所有写操作先预检、确认、备份，再执行与还原验证。

**Tech Stack:** .NET 10、Aprillz.MewUI 0.22.1、System.Text.Json 源生成、BCL 文件/XML/ZIP/HTTP API；xUnit 测试项目由本机 SDK 模板生成。

**Spec:** [已批准设计](../specs/2026-11-04-nuget-manager-design.md)

## Global Constraints

- 第一版仅 Windows；使用者必须安装 .NET SDK。
- 有效 SDK 必须具备 .NET 10 命令能力；尊重 global.json。
- 不在 AOT 进程中加载 Microsoft.Build，不增加托管后台，不使用 WebView。
- 保留现有 net10.0、PublishAot=true、CS1591 警告规则与裁剪配置。
- 默认允许跨大版本；“包含预发布版本”默认关闭。
- 分组顺序：可升级、已安装、隐式依赖；可升级为空则整组隐藏。
- 批量升级按钮在可升级 Header 右侧；操作文案使用“安装”“卸载”。
- 单项目视角的中间列表和右侧项目列表都只显示该项目。
- 共享集中版本允许影响其他项目，但必须预览影响并扩大验证范围。
- 批量操作采用整批成功或整批撤回；并发编辑阻止安全回滚时明确报告。
- 保留用户原有修改，不使用 git reset/checkout，不递归删除用户目录。
- 使用现有 NuGet.Config 和源映射，不新增源或凭据管理界面。
- SDK 操作失败、部分源失败和详情不可用不可包装为成功或空结果。
- 只把还原成功报告为还原成功，不保证编译或业务兼容。

## Review Focus

1. 路径大小写、空格、目录链接及目录外解决方案项目：正确归一化，不循环扫描或丢项目；Task 1。
2. 多框架条件引用、导入引用和 VersionOverride：保留真实来源，不能新增无条件重复引用；Tasks 2、5。
3. 多个项目和不同版本共享集中条目：只写有效条目一次，不降级较新引用，验证所有已加载受影响项目；Tasks 3、5。
4. 用户在 SDK 执行中编辑文件或程序异常退出：不能覆盖未知修改，能定位未完成操作和备份；Task 4。
5. 源映射、认证失败和过期异步响应：不能查询错误来源，不能把未知结果说成全部最新，旧范围结果不能污染新 UI；Tasks 3、6。

---

## 文件与接口总图

保持单一产品项目，内部类型通过 InternalsVisibleTo 暴露给测试，
不为每个组件创建独立程序集。以下是计划新增文件；现有入口、工程和
解决方案按对应任务修改。所有路径相对仓库，命令使用 Windows 分隔符。

| 路径 | 职责 |
|---|---|
| `NugetManager\Domain\WorkspaceModels.cs` | 工作目录、项目和单项目/解决方案范围 |
| `NugetManager\Domain\PackageModels.cs` | 求值引用、包出现位置、更新候选和包详情 |
| `NugetManager\Sdk\ProcessRunner.cs` | 参数列表、输出、退出、取消和超时 |
| `NugetManager\Sdk\SdkClient.cs` | SDK 能力探测、求值、包列表及 SDK 命令构造 |
| `NugetManager\Sdk\SdkJsonContext.cs` | 内部 SDK JSON DTO 与源生成上下文 |
| `NugetManager\Workspaces\WorkspaceDiscovery.cs` | 解决方案/目录发现与路径去重 |
| `NugetManager\Packages\PackageCatalog.cs` | 汇总、更新、搜索和版本规则 |
| `NugetManager\Packages\PackageMetadataReader.cs` | 配置源、缓存/V3/本地包与清单读取 |
| `NugetManager\Operations\OperationModels.cs` | 写入预览、命令步骤、恢复状态 |
| `NugetManager\Operations\FileTransaction.cs` | 字节备份、所有权判断、撤回及恢复清单 |
| `NugetManager\Operations\PackageOperations.cs` | 预检、SDK 写入、影响范围和还原验证 |
| `NugetManager\Presentation\WorkspaceController.cs` | UI 状态、勾选、过期请求和操作互斥 |
| `NugetManager\Presentation\MainWindow.cs` | 三栏主界面、包列表与详情 |
| `NugetManager\Presentation\OperationDialog.cs` | 修改确认、进度、错误和恢复提示 |
| `NugetManager\Properties\AssemblyInfo.cs` | 仅声明测试程序集访问 |
| `NugetManager.Tests\NugetManager.Tests.csproj` | net10.0 xUnit、引用产品项目，测试本身不 AOT |
| `NugetManager.Tests\TestWorkspace.cs` | 隔离临时目录、本地包源和项目夹具 |
| `README.md` | 使用方式、SDK/AOT 构建要求和已知边界 |

测试文件与对应生产组件同名加 Tests，不创建运行期服务定位器、
动态插件系统、通用仓储或泛型事件总线。
用于测试替换的 IProcessRunner、IWorkspaceDiscovery、ISdkClient、
IPackageCatalog、IPackageMetadataReader、IPackageOperations 与各实现
同文件声明，接口成员就是对应任务的 Produces 方法，不另建接口目录。
FileTransaction 的静态工厂/生命周期不是这些服务接口的成员。

构造依赖固定为 SdkClient(IProcessRunner runner)、
PackageCatalog(ISdkClient sdk)、PackageMetadataReader(HttpClient http)、
PackageOperations(ISdkClient sdk, string recoveryDirectory)、
WorkspaceController(IWorkspaceDiscovery discovery, ISdkClient sdk,
IPackageCatalog catalog, IPackageMetadataReader metadata,
IPackageOperations operations)。ProcessRunner 和 WorkspaceDiscovery
无外部服务依赖；不在测试中伪造一个真实运行的原生窗口。
后续任务引用接口，不通过继承非虚方法来 fake 已实现组件。

### 跨任务契约

均为 internal 类型。字符串路径为规范化绝对路径；包 ID 比较不区分
大小写。版本语义比较不得按字符串排序。

```csharp
record ProjectDescriptor(string Path, string Name);
record Workspace(string Directory, string? SolutionPath,
    IReadOnlyList<ProjectDescriptor> Projects);
record WorkspaceScope(string? ProjectPath); // null 表示全部已加载项目

enum ReferenceKind { Direct, Transitive, SdkImplicit }
record PackageOccurrence(string ProjectPath, string Framework,
    string PackageId, string? RequestedVersion, string ResolvedVersion,
    ReferenceKind Kind, string? DeclarationPath);
record EvaluatedReference(string PackageId, string Framework,
    string DeclarationPath, string? CentralVersionPath,
    string? VersionExpression, string? VersionOverride, bool IsWritable,
    string? WriteBlockReason);
record ProjectEvaluation(ProjectDescriptor Project,
    IReadOnlyList<string> Frameworks, IReadOnlyList<EvaluatedReference> References,
    IReadOnlyList<string> LockFilePaths);
record PackageInventory(IReadOnlyList<PackageOccurrence> Occurrences);
record PackageUpgrade(EvaluatedReference Reference, string FromVersion,
    string ToVersion);
record UpdateCandidate(string PackageId, IReadOnlyList<PackageUpgrade> Upgrades);
record SourceDiagnostic(string SourceName, string Code, string Message);
record UpdateReport(IReadOnlyList<UpdateCandidate> Candidates,
    IReadOnlyList<SourceDiagnostic> Diagnostics, bool IsComplete);
record SearchHit(string PackageId, string Version, string SourceName,
    string? Description);
record SearchPage(IReadOnlyList<SearchHit> Items, int Skip, int Take,
    bool HasMore, IReadOnlyList<SourceDiagnostic> Diagnostics);
record DependencyItem(string PackageId, string VersionRange);
record DependencyGroup(string Framework, IReadOnlyList<DependencyItem> Items);
record PackageDetails(string PackageId, string Version, string? Authors,
    string? Description, string? License, Uri? ProjectUrl,
    IReadOnlyList<string> Frameworks, IReadOnlyList<DependencyGroup> Dependencies,
    IReadOnlyList<SourceDiagnostic> Diagnostics);
```

SDK 失败抛出携带退出码与脱敏输出的 SdkCommandException；
无效结构化数据抛出 SdkOutputException；取消传播
OperationCanceledException。UI 边界将其转成明确的错误/取消状态。
SourceDiagnostic 仅用于可以保留其他有效数据的部分源/详情失败。

## Task 1: 可验证的工作目录加载与 SDK 边界

**Files:** 创建 WorkspaceModels.cs、ProcessRunner.cs、WorkspaceDiscovery.cs、
AssemblyInfo.cs、测试工程、TestWorkspace.cs、WorkspaceDiscoveryTests.cs、
ProcessRunnerTests.cs；修改 NugetManager.slnx。

**Interfaces:**
- Produces: `Task<ProcessResult> ProcessRunner.RunAsync(ProcessRequest request, CancellationToken ct)`；
  ProcessRequest 包含 FileName、Arguments（字符串列表）、WorkingDirectory、Timeout；
  ProcessResult 包含 ExitCode、Stdout、Stderr。
- Produces: `Task<IReadOnlyList<string>> WorkspaceDiscovery.FindSolutionsAsync(string directory, CancellationToken ct)`。
- Produces: `Task<Workspace> WorkspaceDiscovery.LoadAsync(string directory, string? solutionPath, CancellationToken ct)`。
- Produces: TestWorkspace.CreateAsync；夹具实现 IAsyncDisposable，
  只删除它创建并记录的具体临时目录。

- [ ] **1. 写失败测试并建立测试工程。**
  使用 `dotnet new xunit -n NugetManager.Tests -f net10.0 --no-restore`，
  添加 ProjectReference 和 InternalsVisibleTo；测试按模板已有测试平台运行。
  断言：SlnxIncludesExternalProject 返回目录外引用；
  SolutionWinsOverLooseProjects 排除未加入解决方案的项目；
  DirectoryScanDeduplicatesAndSkipsLinks 排除 bin/obj/.git 和循环链接；
  MultipleSolutionsRequireSelection 不任意选第一个。
  ProcessArgumentsPreserveSpaces 验证参数不被 shell 拆分；
  BothOutputStreamsDrain 验证 stdout/stderr 均完整且不死锁；
  CancellationStopsOwnedChild、TimeoutReportsFailure 验证只停止自己启动的进程。
  代表性断言（项目文件由 TestWorkspace 在 Arrange 阶段创建）：
  ```csharp
  // SlnxIncludesExternalProject
  Assert.Contains(result.Projects, p => p.Path == externalProjectPath);
  // SolutionWinsOverLooseProjects
  Assert.DoesNotContain(result.Projects, p => p.Path == looseProjectPath);
  // ProcessArgumentsPreserveSpaces
  Assert.Equal("argument with spaces", result.Stdout.Trim());
  ```
- [ ] **2. 运行失败测试。**
  `dotnet test NugetManager.Tests\NugetManager.Tests.csproj --filter "FullyQualifiedName~WorkspaceDiscoveryTests|FullyQualifiedName~ProcessRunnerTests"`
  预期测试失败或因为尚未实现的类型编译失败；模板包缺失后再执行 restore。
- [ ] **3. 实现上述签名。**
  .slnx 用禁用外部实体的 XML；.sln 读取标准项目声明并筛选 C# 项目。
  使用 Path.GetFullPath、Windows 路径比较和显式错误，不解析本地化 sln 表格。
  ProcessStartInfo.ArgumentList、并行读取双输出流、关联超时令牌；
  失败保留退出码，禁止把异常转成空 Workspace。
- [ ] **4. 重跑同一过滤命令。** 预期全部通过；链接测试不能无声跳过，
  Windows 无创建链接权限时使用可注入文件枚举验证循环检测，并报告限制。
- [ ] **5. 提交本任务文件。** `test: add workspace discovery and SDK process boundary`；
  所有任务提交都包含规定的 Co-authored-by trailer，不 add 全仓库。

## Task 2: 真实项目求值与依赖分类

**Files:** 创建 PackageModels.cs、SdkClient.cs、SdkJsonContext.cs、
SdkClientTests.cs、PackageInventoryTests.cs；扩展 TestWorkspace.cs。

**Interfaces:**
- Consumes: IProcessRunner.RunAsync、Workspace、ProjectDescriptor。
- Produces: `Task SdkClient.VerifyCapabilitiesAsync(string directory, CancellationToken ct)`。
- Produces: `Task<ProjectEvaluation> SdkClient.EvaluateAsync(ProjectDescriptor project, CancellationToken ct)`。
- Produces: `Task<PackageInventory> SdkClient.ReadInventoryAsync(Workspace workspace, CancellationToken ct)`。
- Produces: `Task SdkClient.RestoreAsync(IReadOnlyList<ProjectDescriptor> projects, bool lockedMode, CancellationToken ct)`。

- [ ] **1. 写失败测试。** MultiFrameworkKeepsBothVersions 断言同包在
  net8.0/net10.0 的两条出现记录不合并；CentralReferenceKeepsDeclarationAndVersionSource
  断言 .csproj 声明与 props 版本路径分别保留；
  ImportedDirectReferenceIsNotTransitive、SdkImplicitIsNotUserDirect 验证分类；
  ConditionalOverrideIsNotFlattened 保留条件和 VersionOverride；
  InvalidJsonFails、PinnedUnsupportedSdkIsBlocked 验证无空结果回退；
  ReadOnlyLoadDoesNotRewriteLockFile 断言加载前后已有锁文件字节相同。
  ```csharp
  // MultiFrameworkKeepsBothVersions
  Assert.Equal(2, inventory.Occurrences.Count(p => p.PackageId == "Example.Json"));
  // CentralReferenceKeepsDeclarationAndVersionSource
  Assert.Equal(projectPath, reference.DeclarationPath);
  Assert.Equal(centralPropsPath, reference.CentralVersionPath);
  // ReadOnlyLoadDoesNotRewriteLockFile
  Assert.Equal(originalLockBytes, await File.ReadAllBytesAsync(lockPath));
  ```
- [ ] **2. 运行。**
  `dotnet test NugetManager.Tests\NugetManager.Tests.csproj --filter "FullyQualifiedName~SdkClientTests|FullyQualifiedName~PackageInventoryTests"`
  预期未实现接口导致失败。
- [ ] **3. 实现上述签名。**
  用 package list --include-transitive --format json --output-version 1；
  用 msbuild -getProperty/-getItem 获取框架、引用和 DefiningProjectFullPath，
  多框架按框架求值，解析真实有效集中版本文件及导入链。
  VerifyCapabilitiesAsync 在工作目录执行，尊重 global.json，缺命令就阻止；
  创建带 JsonSerializable 特性的 DTO，不用反射序列化任意对象。
  先验证现有资产与锁文件，允许的只读还原使用 lockedMode；
  需要修改声明/已有锁文件则交 Task 5 确认操作，不在这里偷偷修改。
- [ ] **4. 重跑同一过滤命令并执行 `dotnet build NugetManager\NugetManager.csproj --no-restore`。**
  预期全部通过且产品工程无新增编译错误。
- [ ] **5. 提交。** `feat: read evaluated project dependencies with SDK JSON`。

## Task 3: 更新检测、搜索及包详情

**Files:** 创建 PackageCatalog.cs、PackageMetadataReader.cs、
PackageCatalogTests.cs、PackageMetadataReaderTests.cs；扩展 SdkClient.cs、
SdkJsonContext.cs、TestWorkspace.cs；必要时修改产品依赖清单。

**Interfaces:**
- Consumes: ISdkClient、Workspace、ProjectEvaluation、PackageInventory。
- Produces: `Task<UpdateReport> PackageCatalog.FindUpdatesAsync(Workspace workspace, IReadOnlyList<ProjectEvaluation> evaluations, WorkspaceScope scope, bool includePrerelease, CancellationToken ct)`。
- Produces: `Task<SearchPage> PackageCatalog.SearchAsync(Workspace workspace, string query, int skip, int take, bool includePrerelease, CancellationToken ct)`。
- Produces: `Task<PackageDetails> PackageMetadataReader.ReadAsync(Workspace workspace, string packageId, string version, string? sourceName, CancellationToken ct)`。

- [ ] **1. 写失败测试。** DefaultsToStableAllowsMajor 断言
  1.0.0 升级至 3.0.0，排除 4.0.0-preview.1；
  EnabledPrereleaseIncludesPreview 开启后纳入候选；
  DoesNotDowngradeProjectAlreadyNewer 验证同包不同项目版本；
  TransitivePackagesAreNotUpgradeTargets 断言隐式包不进入候选；
  SourceFailureMakesReportIncomplete 断言 IsComplete=false 且有诊断；
  SearchPaginationPreservesSource 验证 skip/take 和来源；
  LocalPackageShowsFrameworkGroups 验证 ZIP 内 ref/lib 和依赖组；
  MappedSourceDoesNotLeakToOtherFeed、DisabledSourceIsNotQueried、
  UncachedAuthenticatedDetailsAreExplicit 验证配置和认证边界；
  XmlExternalEntityIsRejected、MissingMetadataIsNotInvented 验证清单安全。
  ```csharp
  // DefaultsToStableAllowsMajor
  Assert.Equal("3.0.0", Assert.Single(report.Candidates).Upgrades[0].ToVersion);
  Assert.DoesNotContain(report.Candidates.SelectMany(p => p.Upgrades),
      p => p.ToVersion == "4.0.0-preview.1");
  // SourceFailureMakesReportIncomplete
  Assert.False(report.IsComplete);
  Assert.NotEmpty(report.Diagnostics);
  // LocalPackageShowsFrameworkGroups
  Assert.Contains(details.Dependencies, g => g.Framework == "net10.0");
  ```
- [ ] **2. 运行。**
  `dotnet test NugetManager.Tests\NugetManager.Tests.csproj --filter "FullyQualifiedName~PackageCatalogTests|FullyQualifiedName~PackageMetadataReaderTests"`
  预期上述目标方法缺失或测试失败。
- [ ] **3. 实现上述签名。**
  查询用 SDK outdated/search JSON，不自行猜最新版本。
  如需版本排序/配置合并，优先选小型 NuGet.Versioning、
  NuGet.Configuration 稳定包，显式记录解析出的固定版本；
  不引入 NuGet.Commands 或 Microsoft.Build。加包后才 restore。
  配置读取按 NuGet 标准合并层级、clear/remove/disabled、源映射和环境值；
  不把凭据复制到普通模型/日志。详情先缓存和本地包，再公共 V3；
  使用选中 SearchHit.SourceName 保留来源，认证不支持则返回明确诊断。
  XML 关闭外部实体，ZIP 只读取指定条目，不向工作区解压未知路径。
- [ ] **4. 重跑同一过滤命令。** 预期所有版本、映射和元数据断言通过。
  使用自建 Example.* 包和本地/loopback 源，不依赖公网最新版本变化。
- [ ] **5. 提交。** `feat: detect updates and search package metadata`。

## Task 4: 字节级安全事务与异常恢复

**Files:** 创建 OperationModels.cs、FileTransaction.cs、
FileTransactionTests.cs；扩展 TestWorkspace.cs。

**Interfaces:**
- Produces: `record PlannedFile(string Path, byte[]? OriginalBytes)`；
  null 表示原先不存在；快照对象不能直接暴露可修改备份给调用者。
- Produces: `enum RecoveryStatus { Restored, Conflict, Failed }`、
  `record FileRecovery(string Path, RecoveryStatus Status, string? Message)`。
- Produces: `Task<FileTransaction> FileTransaction.BeginAsync(IReadOnlyList<string> paths, string recoveryDirectory, CancellationToken ct)`。
- Produces: `Task FileTransaction.RecordOwnedChangesAsync(CancellationToken ct)`、
  `Task<IReadOnlyList<FileRecovery>> FileTransaction.RollbackAsync(CancellationToken ct)`、
  `Task FileTransaction.CommitAsync(CancellationToken ct)`；
  实现 IAsyncDisposable，不把 Dispose 当作已成功回滚。
- Produces: `Task<IReadOnlyList<string>> FileTransaction.FindIncompleteAsync(string recoveryDirectory, CancellationToken ct)`。

- [ ] **1. 写失败测试。** PreservesDirtyOriginalBytes 断言未提交内容及
  BOM/换行逐字节恢复；NewFileRemovedOnlyWhenOwned 验证新文件归属；
  ConcurrentEditDuringCommandBlocksRollback 验证执行期间外部写入未被覆盖；
  EditAfterCommandAlsoBlocksRollback 验证后续修改保护；
  CrashManifestSurvivesRestart 断言启动能定位未完成记录；
  BackupFailurePreventsAnyWrite、RollbackFailureRemainsVisible 验证失败状态。
  ```csharp
  // PreservesDirtyOriginalBytes
  Assert.Equal(dirtyOriginalBytes, await File.ReadAllBytesAsync(projectPath));
  // ConcurrentEditDuringCommandBlocksRollback
  Assert.Contains(recovery, r => r.Path == projectPath
      && r.Status == RecoveryStatus.Conflict);
  Assert.Equal(externalEditBytes, await File.ReadAllBytesAsync(projectPath));
  // CrashManifestSurvivesRestart
  Assert.Contains(manifestPath, incomplete);
  ```
- [ ] **2. 运行。**
  `dotnet test NugetManager.Tests\NugetManager.Tests.csproj --filter FullyQualifiedName~FileTransactionTests`
  预期失败。
- [ ] **3. 实现上述签名。**
  原始字节、原存在状态、规范化路径和阶段落入应用恢复目录；
  记录预检基线和修改窗口，不用操作结束后的单次哈希证明归属。
  SDK 修改窗口中的未知改动保守判为冲突；在已支持的修改形式中，
  只将预先允许的声明变更及经过验证的 SDK 格式变化认作本批写入。
  做不到安全判断就保留备份并报告 Conflict，不猜测归属。
  回滚使用独立恢复令牌，不继承已取消的操作令牌；
  所有文件恢复结果都必须返回，只有无冲突才可标已撤回。
- [ ] **4. 重跑同一过滤命令。** 预期字节一致与并发改动保护全部通过。
- [ ] **5. 提交。** `feat: protect package edits with recoverable file transactions`。

## Task 5: 集中版本感知的安装、卸载与整批升级

**Files:** 创建 PackageOperations.cs、PackageOperationsTests.cs；
扩展 OperationModels.cs、SdkClient.cs、TestWorkspace.cs。

**Interfaces:**
- Consumes: UpdateCandidate、ISdkClient、ProjectEvaluation、FileTransaction。
- Produces: `enum PackageAction { Upgrade, Install, Uninstall }`、
  `record PackageChange(PackageAction Action, string ProjectPath, string PackageId, string? Version)`。
- Produces: `record OperationPlan(IReadOnlyList<PackageChange> Changes, IReadOnlyList<string> Files, IReadOnlyList<ProjectDescriptor> AffectedProjects, IReadOnlyList<string> Warnings, IReadOnlyList<string> Blockers)`。
- Produces: `enum OperationStatus { Succeeded, RolledBack, RecoveryRequired }`、
  `record OperationResult(OperationStatus Status, string? Error, IReadOnlyList<FileRecovery> Recovery)`。
- Produces: `Task<OperationPlan> PackageOperations.PlanAsync(Workspace workspace, IReadOnlyList<PackageChange> changes, CancellationToken ct)`。
- Produces: `Task<OperationResult> PackageOperations.ExecuteAsync(OperationPlan plan, IProgress<string> progress, CancellationToken ct)`。

- [ ] **1. 写失败测试。** CentralBatchWritesEntryOnce 断言同一共享
  PackageVersion 有一个有效版本写入；
  SingleProjectCentralUpdateRestoresAllAffected 断言还原所有已加载受影响项目；
  InstallTransitiveAddsDirectReference、UninstallKeepsSharedVersionEntry
  验证安装/卸载语义；ConditionalOverrideDoesNotCreateUnconditionalDuplicate
  验证可安全写入或明确 Blocker；
  PreflightDetectsStalePreview 验证确认后外部修改导致重新预检；
  SecondCommandFailureRollsBackFirst、RestoreFailureRollsBackBatch、
  CancellationRollsBackWithFreshToken 验证整批字节回退；
  ConflictReturnsRecoveryRequired 验证不能报告假回滚；
  LockFileChangesUseSameTransaction 验证已有/新建锁文件均在范围内。
  ```csharp
  // SingleProjectCentralUpdateRestoresAllAffected
  Assert.Equal(new[] { apiPath, corePath },
      plan.AffectedProjects.Select(p => p.Path).Order().ToArray());
  // SecondCommandFailureRollsBackFirst
  Assert.Equal(OperationStatus.RolledBack, result.Status);
  Assert.Equal(originalPropsBytes, await File.ReadAllBytesAsync(centralPropsPath));
  // ConflictReturnsRecoveryRequired
  Assert.Equal(OperationStatus.RecoveryRequired, result.Status);
  ```
- [ ] **2. 运行。**
  `dotnet test NugetManager.Tests\NugetManager.Tests.csproj --filter FullyQualifiedName~PackageOperationsTests`
  预期失败。
- [ ] **3. 实现上述签名。**
  PlanAsync 重新求值并固定文件集合、版本变化和影响范围；
  共享条目按实际文件和有效声明去重，只对代表项目执行必要更新，
  不对每个消费项目重复写入集中版本。
  普通安装/升级用 package add --version；卸载用 package remove。
  预检验证这些 SDK 操作不会覆盖条件、表达式或导入语义；
  不支持的形态作为具体 Blocker，不能新增无条件引用绕过。
  快照成功才启动命令；全部写入后还原全部受影响项目。
  命令、还原、取消失败都回滚并尝试原配置还原；
  保留原失败与恢复失败，不把异常吞掉。
  成功后查询刷新失败与写入失败区分：已成功提交的修改不能因为
  查询网络失败再次改写，UI 显示“操作成功，更新检测未完成”。
- [ ] **4. 重跑同一过滤命令。**
  预期使用真实 SDK 和自建本地包的安装/升级/卸载测试通过，
  故障注入也逐字节满足 Task 4 的恢复规则。
- [ ] **5. 提交。** `feat: install remove and batch upgrade packages atomically`。

## Task 6: 可测的界面状态与范围联动

**Files:** 创建 WorkspaceController.cs、WorkspaceControllerTests.cs。

**Interfaces:**
- Consumes: IWorkspaceDiscovery、ISdkClient、IPackageCatalog、
  IPackageMetadataReader、IPackageOperations。
- Produces: `Task WorkspaceController.OpenAsync(string directory, string? solutionPath, CancellationToken ct)`、
  `Task SelectScopeAsync(WorkspaceScope scope, CancellationToken ct)`、
  `void SelectAllUpgrades(bool selected)`、`void SetUpgradeSelected(string packageId, bool selected)`、
  `Task SelectPackageAsync(string packageId, string version, string? sourceName, CancellationToken ct)`。
- Produces: `Task SearchAsync(string query, int skip, CancellationToken ct)`、
  `Task RefreshAsync(CancellationToken ct)`、
  `Task SetIncludePrereleaseAsync(bool include, CancellationToken ct)`、
  `Task<OperationPlan> PreviewChangesAsync(IReadOnlyList<PackageChange> changes, CancellationToken ct)`、
  `Task<OperationPlan> PreviewSelectedUpgradesAsync(CancellationToken ct)`、
  `Task ExecuteConfirmedAsync(OperationPlan plan, CancellationToken ct)`。
- Produces: 不可变 `WorkspaceViewState`，属性为 Workspace? Workspace、
  WorkspaceScope Scope、IReadOnlyList<UpdateCandidate> Upgrades、
  IReadOnlyList<PackageOccurrence> Installed、IReadOnlyList<PackageOccurrence> Implicit、
  IReadOnlyList<PackageOccurrence> SdkImplicit、
  PackageDetails? Details、SearchPage? Search、
  IReadOnlyList<ProjectDescriptor> VisibleProjects、
  IReadOnlySet<string> SelectedPackageIds、bool IncludePrerelease、bool IsBusy、
  WorkspaceStatus Status、IReadOnlyList<SourceDiagnostic> Diagnostics。
  WorkspaceStatus 为 Idle/Loading/Ready/Writing/Cancelled/Failed/RecoveryRequired；
  `event Action<WorkspaceViewState> StateChanged` 发布，UI Task 7 负责 dispatcher 调度。

- [ ] **1. 写失败测试。** SingleProjectFiltersRightPane 断言
  VisibleProjects 只有所选项目；WholeWorkspaceRestoresAllProjects 验证切回；
  EmptyUpdatesHideHeader、DirectAndTransitiveUseSeparateGroups 验证分组；
  ScopeChangeClearsSelection、SelectAllThenExcludeOne 验证勾选；
  OldSearchCannotOverwriteNewScope、OldDetailsCannotOverwriteNewVersion
  验证逆序完成响应；WriteBusyBlocksRangeChanges 验证操作互斥；
  PartialDetectionIsNeverAllLatest 验证不完整查询；
  UninstallThenTransitiveRemains 验证刷新分类；
  SuccessfulWriteFailedRefreshKeepsSuccessfulStatus 验证写入与查询状态分离。
  ```csharp
  // SingleProjectFiltersRightPane
  Assert.Equal(corePath, Assert.Single(state.VisibleProjects).Path);
  // ScopeChangeClearsSelection
  Assert.Empty(state.SelectedPackageIds);
  // DirectAndTransitiveUseSeparateGroups
  Assert.All(state.Installed, p => Assert.Equal(ReferenceKind.Direct, p.Kind));
  Assert.All(state.Implicit, p => Assert.Equal(ReferenceKind.Transitive, p.Kind));
  // PartialDetectionIsNeverAllLatest
  Assert.NotEmpty(state.Diagnostics);
  ```
- [ ] **2. 运行。**
  `dotnet test NugetManager.Tests\NugetManager.Tests.csproj --filter FullyQualifiedName~WorkspaceControllerTests`
  预期失败。
- [ ] **3. 实现上述签名。**
  查询用单调请求代号和取消令牌，状态发布前验证范围和版本；
  使用 fake SDK/catalog 依赖测试，不在 UI 状态层启动线程阻塞。
  SDK 自动引用保留在 SdkImplicit，界面用只读提示展示，
  不混进 Installed 或 Implicit，也不当作源查询错误。
  默认 IncludePrerelease=false；筛选基于真实 ReferenceKind，
  详情项目状态按框架保留；所有操作 await，不使用 async void 业务方法。
  新包安装和卸载由同一 PlanAsync/ExecuteConfirmedAsync 流程触发。
- [ ] **4. 重跑同一过滤命令。** 预期范围、旧响应及错误状态测试全部通过。
- [ ] **5. 提交。** `feat: coordinate package manager state and scoped selection`。

## Task 7: 真实 MewUI 三栏桌面界面

**Files:** 创建 MainWindow.cs、OperationDialog.cs；
修改 Program.cs；创建 MainWindowStateTests.cs。

**Interfaces:**
- Consumes: WorkspaceController、WorkspaceViewState、OperationPlan。
- Produces: `Window MainWindow.Create(WorkspaceController controller)`；
  主界面构建时挂接 MewUI IDispatcher.BeginInvoke 更新 UI。
- Produces: `Task<bool> OperationDialog.ConfirmAsync(Window owner, OperationPlan plan)`；
  确认对话框同时展示 Warnings/Blockers，Blockers 存在时不得确认执行。

- [ ] **1. 写失败测试。** ExtractedPresentationStateUsesInstalledLabels
  断言三个分组标题、安装/卸载文案与分组排序；
  NoUpgradeMeansNoUpgradeHeader 断言 Header 和升级按钮一起隐藏；
  UpgradeButtonLivesInHeader 断言布局树/可测构建模型的父级；
  DirectShowsUninstallTransitiveShowsInstall 验证项目动作；
  ConfirmationListsOutsideScopeAffectedProjects 验证风险提示不扩展右侧列表。
  测试不在无 UI 消息循环环境创建原生窗口；
  测试可测布局/动作模型，实际原生窗口另做手动验收。
  ```csharp
  // DirectShowsUninstallTransitiveShowsInstall
  Assert.Equal("卸载", directProjectActionLabel);
  Assert.Equal("安装", transitiveProjectActionLabel);
  // NoUpgradeMeansNoUpgradeHeader
  Assert.False(upgradeHeaderVisible);
  Assert.False(upgradeButtonVisible);
  ```
- [ ] **2. 运行。**
  `dotnet test NugetManager.Tests\NugetManager.Tests.csproj --filter FullyQualifiedName~MainWindowStateTests`
  预期相关界面构建状态尚未实现导致失败。
- [ ] **3. 实现入口与界面。**
  保持 STAThread，Program.Main 组合依赖并 Application.Run。
  使用已安装 0.22.1 的 Grid、ListBox/GridView、ScrollViewer、
  ComboBox、CheckBox、Button；先核对随包 XML 的精确签名，
  不照搬其他 UI 框架绑定 API。目录选择使用 FileDialog.SelectFolder。
  左侧工作区/项目，中间合并分组与搜索页，右侧版本与项目操作；
  列表独立滚动，最小窗口避免详情遮住批量 Header。
  事件边界捕获并展示业务异常，所有异步结果用 IDispatcher.BeginInvoke；
  给加载、查询、操作、验证、取消和恢复状态真实反馈。
  将参数错误、未支持平台、启动失败明确展示，不能空 Main 或静默退出。
- [ ] **4. 验证。**
  重跑本任务过滤测试，执行 `dotnet build NugetManager\NugetManager.csproj`；
  在 Windows 实际打开隔离测试目录，检查全选/取消、单项目右侧、
  三分组滚动、版本切换、搜索安装、卸载及失败确认。
  预期窗口可用、按钮位置符合设计、网络任务不冻结 UI。
- [ ] **5. 提交。** `feat: build the MewUI package manager desktop workflow`。

## Task 8: 恢复入口、端到端验证与 AOT 发布

**Files:** 扩展 Program.cs、OperationDialog.cs、TestWorkspace.cs；
创建 PackageWorkflowTests.cs、README.md；
仅必要时修改 NugetManager.csproj 和 Trimming.rd.xml。

**Interfaces:**
- Consumes: 所有上述接口，特别是 FindIncompleteAsync 和明确的恢复状态。
- Produces: 启动时未完成操作提示、可运行 win-x64 AOT 发布、
  用户与开发者使用文档；不新增无关平台支持。

- [ ] **1. 写失败端到端测试。**
  WorkspaceToStableBatchToRefresh 断言打开后检测 1.0.0→3.0.0、
  全选取消一个包、仅选中目标更新并重新检测；
  SearchInstallUninstallRefresh 验证搜索后直接安装再卸载；
  SharedCentralBatchWithFailureKeepsOriginalFiles 验证跨项目共享版本回退；
  ReopenAfterInterruptedOperationShowsRecovery 验证异常退出恢复入口。
  使用至少两个项目、两个框架、集中版本及本地自建包源。
  ```csharp
  // WorkspaceToStableBatchToRefresh
  Assert.All(refreshed.Occurrences.Where(p => p.PackageId == selectedId),
      p => Assert.Equal("3.0.0", p.ResolvedVersion));
  Assert.Equal("1.0.0", refreshed.Occurrences
      .First(p => p.PackageId == excludedId).ResolvedVersion);
  // SharedCentralBatchWithFailureKeepsOriginalFiles
  Assert.Equal(originalPropsBytes, await File.ReadAllBytesAsync(centralPropsPath));
  ```
- [ ] **2. 运行。**
  `dotnet test NugetManager.Tests\NugetManager.Tests.csproj --filter FullyQualifiedName~PackageWorkflowTests`
  未实现的恢复/端到端接线必须先失败，不能只断言 fake 被调用。
- [ ] **3. 完成恢复入口和直接相关文档。**
  提示备份路径、每个文件恢复状态和手动恢复选项，
  绝不自动覆盖冲突文件；保留失败清单。
  README 写工作目录规则、SDK/global.json、集中版本影响、
  私有源详情限制、还原不等于编译及实际验证的发布架构。
  .NET JSON 源生成与小型 NuGet 依赖验证后再决定是否需裁剪根，
  不用 preserve all 新增依赖来掩盖 AOT 问题。
- [ ] **4. 验证整体结果。**
  运行 `dotnet test NugetManager.Tests\NugetManager.Tests.csproj`，
  预期全部通过；再运行
  `dotnet publish NugetManager\NugetManager.csproj -c Release -r win-x64 -p:PublishAot=true`。
  预期退出码 0，无未处理的新增 AOT/裁剪告警。
  若报告缺少 Windows Native AOT C++ 工具链，明确记录阻塞，
  取得安装许可或使用已配置环境验证，不把 JIT build 说成 AOT 成功。
  实际启动 `NugetManager\bin\Release\net10.0\win-x64\publish\NugetManager.exe`，
  在隔离目录重复加载/搜索/安装/卸载/批量升级及取消；确认窗口响应，
  SDK 子进程退出且 UI 无反射缺失错误，再关闭本次启动的具体进程。
  最后 `git diff --check`，确认仅计划文件被修改、没有测试目录或备份入库。
- [ ] **5. 提交。** `feat: verify recovery and Windows Native AOT release`。

## 执行与提交规则

任务按 1→2→3→4→5→6→7→8 执行。Task 4 只依赖 Task 1 的夹具与路径，
概念上可独立，但默认由同一实现者顺序完成，避免共享工作区竞争。
每项先红后绿；失败只修本任务或紧密相关根因，不做无关重构。
代码、状态和文档全部完成且真实 AOT 验证通过后才宣布产品完成。

示例提交格式：

```powershell
git add -- '<本任务的明确文件路径>'
git commit -m '<该任务的提交消息>' -m 'Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>'
```

执行前按 chosen skill 建立隔离工作区；不得覆盖用户未提交改动。
测试依赖只在生成/修改清单后或缺包失败后 restore。
计划不要求引入额外 lint/build 系统，不在本阶段安装依赖或创建产品代码。

## 计划自审

- 设计的发现/范围、依赖分类、版本/源、事务、UI、错误和验收分别落在
  Tasks 1、2、3、4–5、6–7、8；初始还原锁文件保护在 Tasks 2、5。
- 跨任务类型集中定义，写入只经 OperationPlan 和统一事务；
  UI 刷新失败与写入失败分离，复用相同安装/卸载/升级管道。
- Review Focus 的五类输入各有命名测试，不留“处理边界情况”的空步骤。
- 未承诺私有源完整认证管理、packages.config、其他平台或业务自动兼容。
- 每项独立红绿验证；全量测试与真实 AOT 运行只在最后一项升级验证。
