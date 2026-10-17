# 本仓库用 .NET 10、xunit.v3 4.x 与 Microsoft Testing Platform v2，不跟随旧仓库的 .NET 8 基线

旧线各仓库按 `8005-agv-program` 的 ADR-cross-0056 统一在 .NET 8、xunit.v3 3.2.2 加 VSTest，并且规定不许个别仓库单独升级。本仓库是这条规定的例外：SDK 用 .NET 10（LTS，支持到 2028-11-14），测试用 xunit.v3 4.x，跑在 Microsoft Testing Platform（MTP）v2 上，不用 VSTest。理由是 .NET 8 和 .NET 9 都在 2026-11-10 结束支持，而本仓库从零开始，没有要兼容的旧代码；xunit 和微软也都在往 MTP 走，xunit 4.0 起不再支持 MTP v1。旧仓库留在 .NET 8 直到退役，不因本仓库而升级。

**Status**: accepted（2026-10-14）

**Considered Options**:

- 跟随 0056 用 .NET 8：不选。一个月内就结束支持，等于本仓库一建好就要再升级一次。
- .NET 11：不选。它是 STS（短期支持），而且当时还在 RC 阶段。
- .NET 10（采纳）。

**Consequences**:

- 车载终端维持 Windows 10 专业版 22H2。它不在 .NET 10 的官方支持列表里（Windows 10 只列了 LTSC 与 IoT 版），产品负责人知情后接受。
- 测试筛选的写法和旧仓库相反：MTP 用 `--filter-class`、`--filter-method`、`--filter-trait`、`--filter-query`，VSTest 的 `--filter` 在这里不能用。`global.json` 里必须有 `"test": { "runner": "Microsoft.Testing.Platform" }`，否则 .NET 10 SDK 上的 `dotnet test` 不走 MTP。
- 0056 的四层机制本仓库都保留：`global.json` 锁 SDK；`Directory.Packages.props` 集中管理版本；`Directory.Build.props` 统一目标框架等属性；`Directory.Build.targets` 的禁用包守卫在构建期报 `SIMPLE0001`，名单换成了 VSTest 的包（`Microsoft.NET.Test.Sdk`、`xunit.runner.visualstudio`）和其他测试框架。0056 记下的限制（项目文件里内联写的 `Version=` 会被中央版本静默盖掉，构建期拦不住，只能靠审查）在本仓库不成立：SDK 10.0.302 上，`PackageReference` 内联写 `Version=`，restore 报 `NU1008` 失败；`Directory.Packages.props` 关掉了 `CentralPackageVersionOverrideEnabled`，写 `VersionOverride=` 报 `NU1013` 失败（#19、#22 实测，2026-10-17 更正）。所以不另写扫描 `Version=` 的脚本。
- `global.json` 锁 `10.0.303`，`rollForward: latestPatch`：同一 feature band（主、次版本号相同，补丁号的百位相同，这里是 10.0.3xx）里装着更新的补丁就用更新的，比 `10.0.303` 旧的补丁或别的 feature band 都不行。每台机器（开发机、vm01 以及以后 CI 所在的机器、simple01）都要保持这个 feature band 的最新补丁。CI 第一步（`scripts/ci/Assert-SdkVersion.ps1`）核对实际解析出的 SDK 和 `global.json` 同属一个 feature band、并且不低于它，不满足就失败并写明两个版本；日志里打印实际用的版本。（2026-10-17，#43）
  - 来历：起初是 `latestFeature`，因为 CI 机器还没装 .NET 10 SDK；vm01 在 #20 装好 10.0.302 后，#28 收紧成 `10.0.302` 加 `disable`，和 0056 一样要求开发机和 CI 装着同一个 SDK，差一个补丁版本也不行。同一天中午 Microsoft Update 把 vm01 上的 10.0.302 换成了 10.0.303，从此每个 PR 的 CI 都在第一步失败（例如 PR #38 的 run 39007602875）。0056 早就记过这条教训（Windows Update 会替换同一 feature band 里的旧补丁版本），但对策只是“升级时开发机和 CI 一起升”，挡不住自动更新先动手。
  - 取舍：产品负责人在“各台机器逐字节可重现”和“不被自动更新打断”之间选了后者（2026-10-17）。代价是几台机器可能短时间用着不同的补丁版本，编译结果不再保证逐字节一致，这个代价已经接受。
  - 以后：Microsoft Update 在同一 feature band 里换补丁，CI 不再变红，也不用改 `global.json`。只有换 feature band（例如 10.0.4xx）或要求更高的最低补丁时才改 `global.json`，这时各台机器要先装上新版本，再合并改 `global.json` 的 PR。
- 旧仓库在 2026-11-10 之后不再有安全补丁。旧线退役拖得越久，这个风险越大。
