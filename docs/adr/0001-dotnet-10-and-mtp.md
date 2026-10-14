# 本仓库用 .NET 10、xunit.v3 4.x 与 Microsoft Testing Platform v2，不跟随旧仓库的 .NET 8 基线

旧线各仓库按 `8005-agv-program` 的 ADR-cross-0056 统一在 .NET 8、xunit.v3 3.2.2 加 VSTest，并且规定不许个别仓库单独升级。本仓库是这条规定的例外：SDK 用 .NET 10（LTS，支持到 2028-11-14），测试用 xunit.v3 4.x，跑在 Microsoft Testing Platform（MTP）v2 上，不用 VSTest。理由是 .NET 8 和 .NET 9 都在 2026-11-10 结束支持，而本仓库从零开始，没有要兼容的旧代码；xunit 和微软也都在往 MTP 走，xunit 4.0 起不再支持 MTP v1。旧仓库留在 .NET 8 直到退役，不因本仓库而升级。

**Status**: accepted（2026-10-14）

**Considered Options**:

- 跟随 0056 用 .NET 8：不选。一个月内就结束支持，等于本仓库一建好就要再升级一次。
- .NET 11：不选。它是 STS（短期支持），而且当时还在 RC 阶段。
- .NET 10（采纳）。

**Consequences**:

- 车载工控机维持 Windows 10 专业版 22H2。它不在 .NET 10 的官方支持列表里（Windows 10 只列了 LTSC 与 IoT 版），产品负责人知情后接受。
- 测试筛选的写法和旧仓库相反：MTP 用 `--filter-class`、`--filter-method`、`--filter-trait`、`--filter-query`，VSTest 的 `--filter` 在这里不能用。`global.json` 里必须有 `"test": { "runner": "Microsoft.Testing.Platform" }`，否则 .NET 10 SDK 上的 `dotnet test` 不走 MTP。
- 0056 的四层机制本仓库都保留：`global.json` 锁 SDK；`Directory.Packages.props` 集中管理版本；`Directory.Build.props` 统一目标框架等属性；`Directory.Build.targets` 的禁用包守卫在构建期报 `SIMPLE0001`，名单换成了 VSTest 的包（`Microsoft.NET.Test.Sdk`、`xunit.runner.visualstudio`）和其他测试框架。0056 记下的限制在这里同样成立：项目文件里内联写的 `Version=` 会被中央版本静默盖掉，构建期拦不住，只能靠审查。
- `global.json` 暂时是 `10.0.302` 加 `rollForward: latestFeature`，不是 0056 的 `disable`，因为 CI 机器还没装 .NET 10 SDK。CI 装好、两边版本对齐后改成 `disable`。到时要记住 0056 的教训：Windows Update 会替换同一 feature band 里的旧补丁版本，所以开发机升级 SDK 时，CI 机器要一起升。
- 旧仓库在 2026-11-10 之后不再有安全补丁。旧线退役拖得越久，这个风险越大。
