# riot-sdk 发布到 GitHub Packages，SIMPLE 按普通包引用

`Simple.Server.Riot` 要用 `riot-sdk`（RIoT 客户端库，`trytoreachpeak0/riot-sdk`）。产品负责人定了 `riot-sdk` 保持独立仓库、独立的包，别的程序也能用，不并进本仓库。剩下的问题是包怎么到 SIMPLE 手里。

上一代的做法是手工打包，再把 nupkg 连同哈希拷进服务端仓库的 `vendor/`，本地包源指向具体版本的目录。每升一次版本，要改 SDK、手工打包、核哈希、改三处钉版本的地方、重生成锁文件，约半天手工活；还出过同一个版本号打了两次、第二次被本机缓存遮住的事故。

产品负责人定了发布到 `trytoreachpeak0` 账号下的 GitHub Packages，不发 nuget.org（2026-10-17，工单 #34）。原话是不想让这个包通过 nuget 共享出去，通过 GitHub 就行。

**Status**: accepted（2026-10-17）

**Considered Options**:

- 发到 GitHub Packages（采纳）：包在这个 GitHub 账号下，不进 nuget.org 的公共目录。代价是 GitHub 的 NuGet 源连读取也要令牌，公开包也一样：每台开发机要存一个 `read:packages` 令牌。
- 发到 nuget.org：不选。任何机器不用凭据就能还原，但发上去就是对全世界公开、永久的，只能下架不能删除，产品负责人不想这样共享。
- 像上一代那样把 nupkg 放进本仓库的 `vendor/`：不选。就是上面那半天手工活；仓库里多出二进制文件，“同一个版本号不重发”全靠自觉。

**Consequences**:

- 本仓库照常用中央包管理：`Directory.Packages.props` 钉 `RIoT.Sdk.Facade` 的版本，它带进同版本的 `RIoT.Sdk.Core` 和 `RIoT.Sdk.Generated`；锁文件里有每个包的 `contentHash`，CI 的 `--locked-mode` 还原会核对。升级 SDK 就是改这一个版本号、重生成锁文件，在一个 PR 里完成。
- 仓库根目录的 `nuget.config` 先 `<clear />`，再列 nuget.org 和 GitHub Packages 两个源，用包来源映射规定 `RIoT.Sdk.*` 只从 GitHub Packages 拿，其余只从 nuget.org 拿。机器上另外配置的包源（例如 vm01 上 v2 线的本地源）不会混进来，两个源也不能顶替对方的包。
- 仓库里不放任何凭据。**开发机第一次还原之前**，要在用户级 NuGet 配置里以源名 `github` 存一个只有 `read:packages` 权限的 classic 令牌（`dotnet nuget add source … --configfile "$env:APPDATA/NuGet/NuGet.Config"`），步骤见 README 的“第一次还原之前”。没做这一步，还原会因 401 失败。存法分两种：
  - 交互登录的开发机：按当前用户加密（DPAPI）保存，文件里不是明文。
  - 经 SSH 运行还原的远端机器（例如 `simple remote` 用的 `simple01`）：必须加 `--store-password-in-clear-text` 明文保存。经 SSH 密钥登录的会话拿不到 DPAPI 主密钥（#40 实测 `ProtectedData.Protect(CurrentUser)` 报 `Access is denied.`），NuGet 解不开加密的令牌。代价是那台机器上的管理员进程都读得到这个令牌；它只有 `read:packages` 权限，泄露了也只能读包。
  - 两种存法都靠同一个机制：NuGet 按源的名字匹配凭据，本仓库 `nuget.config` 里的 `<clear />` 只清源、不清用户级配置里的凭据（2026-10-17 用本机回环上的假源实测）。
- CI 用每次运行自带的 `GITHUB_TOKEN`（`packages: read`），在两个还原步骤上经环境变量 `NuGetPackageSourceCredentials_github` 交给 NuGet，令牌随运行结束失效。包是公开的，所以不用在包设置的 “Manage Actions access” 里给本仓库开 Read：GitHub 文档写明 “If the package is public, any workflow running in any repository can download the package.”（《Publishing and installing a package with GitHub Actions》，2026-10-17 读到）。以后要是换成私有包，就得补这一步。
- 发布在 `riot-sdk` 仓库完成：`.github/workflows/publish.yml` 只能手动触发，只从 `main` 发布，默认只打包和核对、不推送，勾选推送才发布；凭据只有那次运行的 `GITHUB_TOKEN`，没有长期密钥：检查作业只能读，空跑时不签发写令牌；推送作业才有 `packages: write`，推的是检查作业检查过的那一份包。三个包逐个推送，不是原子的：中途失败时不删已推上去的半套，把补丁号加一、经批准重新发布。推送前 `scripts/check-packages.ps1` 核对包里只有预期的文件、没有像凭据的内容和本机路径、许可证（MIT）与仓库地址齐全、GitHub Packages 上还没有这个版本。包的 `RepositoryUrl` 指向 riot-sdk，发布后自动关联到那个仓库。
- riot-sdk 0.3.0 依赖 `Microsoft.Kiota.Bundle` 1.17.4，其中 `Microsoft.Kiota.Abstractions` 1.22.0 以下有高危漏洞 GHSA-7j59-v9qr-6fq9（跨站重定向时泄露 Cookie 与 Proxy-Authorization 请求头），本仓库还原时报 `NU1903`。按中央包管理的做法，`Directory.Packages.props` 把 `Microsoft.Kiota.Bundle` 钉到 1.22.2，所有 Kiota 包一起升到 1.22.2；等 riot-sdk 出了依赖 1.22 以上的新版本，就去掉这一行。
- 包是公开的（产品负责人 2026-10-17）：任何有 GitHub 令牌的人都能还原，但不进 nuget.org。新发布的包默认私有，第一次发布后由产品负责人在每个包的设置页把可见性改为 Public。公开不可撤回：GitHub 上的包一旦公开，就不能再改回私有。公开包不收费，也不占私有包的存储额度：GitHub 文档写明 “GitHub Packages usage is free for public packages”，存储与流量额度只针对私有仓库的包（《GitHub Packages billing》，2026-10-17 读到）。
- 公开包还原时仍然要令牌：GitHub 文档写明，除容器仓库外，拉包都要用个人令牌或 `GITHUB_TOKEN` 认证，“regardless of whether the package is public or private”（《About permissions for GitHub Packages》，2026-10-17 读到）。所以上面开发机存令牌那一步不能省。
- 版本号不重用。v2 线用过的号（`0.1.0-controlserver.1`、`0.1.0-controlserver.2`、`0.2.0-fp.1` 到 `0.2.0-fp.7`）没上过 GitHub Packages，但还留在一些机器的本机缓存里，同号的新包会被缓存遮住，所以这些号和 `-controlserver`、`-fp` 后缀都不用，`check-packages.ps1` 会拒绝。第一个版本是 `0.3.0`，打包前先把 `fp/v2-facade` 合回了 riot-sdk 的 `main`，所以它带着 v2 线这两个月的现场修复。
- v2 线不受影响：它继续从自己仓库的 `vendor/` 拿 `0.2.0-fp.7`，它的 `NuGet.Config` 里没有 GitHub Packages 源；`fp/v2-facade` 分支不动。
- 每次发布都是对外、撤不回的操作：发布前把包名、版本号、包里的文件清单和核对结果交产品负责人批准。
