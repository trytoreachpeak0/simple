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
- 仓库里不放任何凭据。**开发机第一次还原之前**，要用 `scripts/Set-GitHubPackagesToken.ps1` 在用户级 NuGet 配置里存一个只有 `read:packages` 权限的 classic 令牌，步骤见 README 的“第一次还原之前”。没做这一步，还原会因 401 失败。
  - 只存凭据，不登记源：脚本只写 `packageSourceCredentials` 下名为 `github` 的一节，源只由本仓库的 `nuget.config` 定义。NuGet 按源的名字匹配凭据；本仓库 `nuget.config` 里的 `<clear />` 只清源，不清用户级的凭据。
  - 不用 `dotnet nuget add source` 存令牌：它会把源登记到用户级，同一台机器上所有没写 `<clear />` 的仓库还原时都会去查 GitHub Packages，令牌过期、被吊销或连不上时就整体失败（`NU1301`）。笔记本上 v2 线的 `8005-agv-onboard-hmi`、`slots-simulator`、`8005-mes-ingest` 和 riot-sdk 都没有自己的 `nuget.config`，正属于这种情况。
  - 2026-10-17 用本机 127.0.0.1 上真正提供这三个包的假源实测：
    - 对照：照旧写法登记了源、令牌失效时，一个没有 `nuget.config` 的项目还原报 `NU1301 … 401 (Unauthorized)`；
    - 改用脚本后，仿本仓库的项目用这份凭据还原成功，假源收到的是脚本存的凭据；没有 `nuget.config` 的项目在全新缓存下还原成功，假源收到的请求是 0；
    - 明文模式和再跑一次换令牌也都通过。
  - 存法分两种：交互登录的开发机按当前用户加密（DPAPI）保存，用的是 NuGet 自己的加密方式，文件里不是明文；经 SSH 运行还原的远端机器（例如 `simple remote` 用的 `simple01`）要加 `-ClearText` 明文保存。原因是经 SSH 密钥登录的会话拿不到 DPAPI 主密钥（#40 实测 `ProtectedData.Protect(CurrentUser)` 报 `Access is denied.`），NuGet 解不开加密的令牌。代价是那台机器上的管理员进程都读得到这个令牌；它只有 `read:packages` 权限，泄露了也只能读包。
  - 令牌由脚本用 `Read-Host -MaskInput` 读入，不出现在任何命令行上：不进 PowerShell 历史，也不会作为 `dotnet.exe` 的参数被命令行审计记下来。换令牌就是再运行一次脚本。
- CI 用每次运行自带的 `GITHUB_TOKEN`（`packages: read`），在两个还原步骤上经环境变量 `NuGetPackageSourceCredentials_github` 交给 NuGet，令牌随运行结束失效。包是公开的，所以不用在包设置的 “Manage Actions access” 里给本仓库开 Read：GitHub 文档写明 “If the package is public, any workflow running in any repository can download the package.”（《Publishing and installing a package with GitHub Actions》，2026-10-17 读到）。以后要是换成私有包，就得补这一步。
- 发布在 `riot-sdk` 仓库完成：`.github/workflows/publish.yml` 只能手动触发，只从 `main` 发布，默认只打包和核对、不推送，勾选推送才发布；凭据只有那次运行的 `GITHUB_TOKEN`，没有长期密钥：检查作业只能读，空跑时不签发写令牌；推送作业才有 `packages: write`，推的是检查作业检查过的那一份包。三个包逐个推送，不是原子的：中途失败时不删已推上去的半套，把补丁号加一、经批准重新发布。推送前 `scripts/check-packages.ps1` 核对包里只有预期的文件、没有像凭据的内容和本机路径、许可证（MIT）与仓库地址齐全、GitHub Packages 上还没有这个版本。包的 `RepositoryUrl` 指向 riot-sdk，发布后自动关联到那个仓库。
- riot-sdk 依赖 `Microsoft.Kiota.Bundle`，本仓库经它间接用到 Kiota。0.3.0 依赖的是 1.17.4，其中 `Microsoft.Kiota.Abstractions` 1.22.0 以下有高危漏洞 GHSA-7j59-v9qr-6fq9（跨站重定向时泄露 Cookie 与 Proxy-Authorization 请求头），本仓库还原时报 `NU1903`。当时在 `Directory.Packages.props` 里把 `Microsoft.Kiota.Bundle` 钉到 1.22.2 先过了这道门，代价是跑的是“按 1.17.4 编译、在 1.22.2 上运行”、没测过的组合。0.3.1（riot-sdk#12，本仓库 #48，2026-10-17）改为基于 Kiota 1.22.2 构建，那一行钉版本已经去掉，Kiota 的版本由 riot-sdk 决定。
  - 0.3.1 在 Kiota 1.22.2 下跑过 riot-sdk 自己的全部测试（271 个）。其中新加的 15 个逐字节核对 Facade 发出的每一个 POST：急停、解除急停、取消单、暂停、继续、挂起继续、上线、下线、优先执行、两个建单重载、路径代价、最近终点、最近起点。核对的是方法、路径、HTTP 版本、全部请求头和请求体。同样的请求在 1.17.4 与 1.22.2 下各录一遍，除 HTTP 版本外逐字节相同。Kiota 1.21.2 改过写请求体的序列化；补这 15 个测试之前，只有单段建单的请求体被这样核对过。
  - 唯一的差别是请求的 HTTP 版本：Kiota 1.20.0 起版本从 `HttpClient` 读取，不再写死成 2.0，`HttpClient` 默认是 1.1。所以 Kiota 1.17.4 下（即 0.3.0 自带的组合）请求是 2.0，1.22.2 下是 1.1，版本策略两边都是 `RequestVersionOrLower`。本仓库自 #47 把 Kiota 钉到 1.22.2 起已经是 1.1，改用 0.3.1 没有再改变它。RIoT 的地址是 `http://`，.NET 在这个策略下对明文 http 不尝试 HTTP/2，所以 2.0 和 1.1 在线上发的都是 HTTP/1.1。若将来 RIoT 改用 `https://`，2.0 会协商到 HTTP/2，1.1 只用 1.1。riot-sdk 的测试把 1.1 钉住了。
  - 这个漏洞在 riot-sdk 的调用路径上走不到：`RiotSession` 和 `RiotAuthClient` 总是把 `HttpClient` 交给 Kiota，调用方没给时自己建一个，并设了 `AllowAutoRedirect=false`（riot-sdk `main`：`RiotSession.cs:26,147`，`RiotAuthClient.cs:18,55`）。整个仓库都不经 `KiotaClientFactory`，也就不会装上有问题的 `RedirectHandler`。所以升级 Kiota 换来的是还原不再报 `NU1903`、本仓库不用再钉 Kiota，不是实际的安全收益。以后本仓库给 riot-sdk 传入自己的 `HttpClient` 时，也要关掉自动重定向，和 riot-sdk 自建的客户端保持一致。
- 包是公开的（产品负责人 2026-10-17）：任何有 GitHub 令牌的人都能还原，但不进 nuget.org。新发布的包默认私有，第一次发布后由产品负责人在每个包的设置页把可见性改为 Public。公开不可撤回：GitHub 上的包一旦公开，就不能再改回私有。公开包不收费，也不占私有包的存储额度：GitHub 文档写明 “GitHub Packages usage is free for public packages”，存储与流量额度只针对私有仓库的包（《GitHub Packages billing》，2026-10-17 读到）。
- 公开包还原时仍然要令牌：GitHub 文档写明，除容器仓库外，拉包都要用个人令牌或 `GITHUB_TOKEN` 认证，“regardless of whether the package is public or private”（《About permissions for GitHub Packages》，2026-10-17 读到）。所以上面开发机存令牌那一步不能省。
- 版本号不重用。v2 线用过的号（`0.1.0-controlserver.1`、`0.1.0-controlserver.2`、`0.2.0-fp.1` 到 `0.2.0-fp.7`）没上过 GitHub Packages，但还留在一些机器的本机缓存里，同号的新包会被缓存遮住，所以这些号和 `-controlserver`、`-fp` 后缀都不用，`check-packages.ps1` 会拒绝。第一个版本是 `0.3.0`，打包前先把 `fp/v2-facade` 合回了 riot-sdk 的 `main`，所以它带着 v2 线这两个月的现场修复。
- 对 v2 线：`8005-agv-control-server` 继续从自己仓库的 `vendor/` 拿 `0.2.0-fp.7`，它的 `NuGet.Config` 先 `<clear />`、里面没有 GitHub Packages 源，这次发布不改变它的解析；`fp/v2-facade` 分支不动。v2 线其他几个仓库没有自己的 `nuget.config`，会读到机器的用户级配置，所以开发机上只存凭据、不登记源（见上），它们才不会被牵连。
- 每次发布都是对外、撤不回的操作：发布前把包名、版本号、包里的文件清单和核对结果交产品负责人批准。
