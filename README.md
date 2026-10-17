# SIMPLE

**SIMPLE** stands for **S**upply of **I**ndirect **M**aterials, **P**roduction **L**ots & **E**quipment parts.
中文全称：**辅料、产品批次与机台备件配送系统**。逐字母：**S** = Supply（配送）；**I** = Indirect、**M** = Materials（间接物料，即辅料：机台用的耗材与易损件）；**P** = Production、**L** = Lots（产品批次）；**E** = Equipment parts（机台备件）。

SIMPLE 按工厂 MES 的需求，用多仓位的自动搬运车，把产品批次、辅料和机台备件送到半导体工厂的机台旁。车辆经工厂的车队调度平台派遣。服务端是唯一做决策的一方；车载终端只执行命令、上报现场观测到的事实。

各厂区的起始业务配置放在 `sites/<厂区>/` 下（首个厂区是 `sites/d8/`），首次安装时导进服务端，之后以服务端里的配置为准，在控制台上修改；产品代码按配置运行，不按厂区写分支，也不以厂区或客户命名。

## 目录

| 位置 | 内容 |
| --- | --- |
| `src/Simple.Server` | 服务端：领域逻辑、调度、对外 API、事件记录 |
| `src/Simple.Server.Console` | 服务端控制台界面，随服务端进程运行 |
| `src/Simple.Server.Riot` | 车队调度平台 RIoT 的适配 |
| `src/Simple.OnboardTerminal` | 车载终端（WPF 界面外壳） |
| `src/Simple.OnboardTerminal.Core` | 车载终端的核心，不依赖 WPF：连接服务端、收发消息、车旁操作、读 IO 模块 |
| `src/Simple.OnboardTerminal.Headless` | 无界面的车载终端，本机整套系统、测试和 CI 用它 |
| `src/Simple.MesGateway` | MES 网关，独立的小进程 |
| `src/Simple.FakeRiot` | 假 RIoT：只在回环地址上监听，按真实 RIoT 的样子回答 SIMPLE 用到的那部分接口，给本机整套系统、测试和 CI 用；它必须先通过 `Simple.Contracts.Tests` |
| `src/Simple.IoSimulator` | 车上 IO 模块的模拟器（WPF 界面外壳，界面还没做） |
| `src/Simple.IoSimulator.Core` | IO 模拟器的核心，不依赖 WPF：料柜物理模型、IO 模块的 Modbus TCP 服务端（FluentModbus）、通信故障、自动应答 |
| `src/Simple.IoSimulator.Headless` | 无界面的 IO 模拟器，本机整套系统、测试和 CI 用它；Modbus 和控制接口都只听回环地址 |
| `src/Simple.Protocol` | 服务端与车载终端之间的协议契约 |
| `src/Simple.Logging` | 各程序共用的程序日志：写本机文件、限大小、脱敏、记下没接住的异常 |
| `src/Simple.Cli` | `simple` 命令行：在本机一条命令起停整套系统、查看状态、事件和日志 |
| `tests/` | 每个项目一个 `*.Tests` 测试项目，另有调度平台的行为契约测试 |
| `scenarios/` | 统一格式的测试场景 |
| `samples/replay/` | 从真实系统录下的回放样本 |
| `sites/<厂区>/` | 各厂区的起始业务配置 |

以后计划再加一个入口 `Simple.Mcp`：给 AI 用的一层薄包装，只调用命令行和服务端 API，不放业务逻辑。

## 第一次还原之前

RIoT 客户端库 `RIoT.Sdk.*` 来自 `trytoreachpeak0/riot-sdk`，发布在 GitHub Packages（`https://nuget.pkg.github.com/trytoreachpeak0/index.json`），其余包来自 nuget.org，见仓库根的 `nuget.config`。这几个包是公开的，但 GitHub 的 NuGet 源连读取公开包也要令牌，所以每台开发机第一次还原（`dotnet restore`、`dotnet build`、`./simple up` 都会还原）之前，要在本机用户级的 NuGet 配置里存一次令牌：

1. 在 GitHub 的 Settings → Developer settings → Personal access tokens → Tokens (classic) 新建一个令牌，只勾 `read:packages`。GitHub Packages 只认 classic 令牌。每台机器单独建一个，作废时互不影响。
2. 以之后运行还原的那个用户运行脚本，按这台机器的情况选下面一种。脚本会提示粘贴令牌，粘贴的内容显示为星号。

   **开发机，交互登录**（例如笔记本）：在本仓库的克隆里打开 PowerShell 7 运行。令牌按当前用户加密（DPAPI）保存，文件里不是明文。

   ```
   pwsh -File scripts/Set-GitHubPackagesToken.ps1
   ```

   **经 SSH 运行还原的远端机器**（例如 `simple remote` 用的 `simple01`）：要加 `-ClearText` 明文保存。经 SSH 密钥登录的会话拿不到 DPAPI 的主密钥（实测 `ProtectedData.Protect(CurrentUser)` 报 `Access is denied.`），NuGet 解不开加密存的令牌，还原会 401。代价是这台机器上任何管理员身份的进程都读得到这个令牌；它只有 `read:packages` 权限，泄露了也只能读包。明文保存用不到 DPAPI，所以直接在笔记本上经 SSH 运行即可；以 SSH 登录的那个用户（`simple01` 上是 `simpleops`）运行，令牌就存进那个用户自己的配置。必须带 `-t`：脚本用 `Read-Host -MaskInput` 读令牌，要一个终端；不带 `-t` 时脚本会一直等着，读不到输入。把 `<克隆>` 换成那台机器上本仓库克隆的路径：

   ```
   ssh -t simple01 "pwsh -NoProfile -File <克隆>/scripts/Set-GitHubPackagesToken.ps1 -ClearText"
   ```

   换令牌（过期或作废）时再运行一次同一条命令，新令牌会替换旧的。

脚本只往用户级的 `%APPDATA%\NuGet\NuGet.Config` 里写名为 `github` 的凭据（`packageSourceCredentials`），不在那里登记源。源只由本仓库的 `nuget.config` 定义，NuGet 按源的名字把用户级的凭据配上去。这样同一台机器上别的仓库不会因此去查 GitHub Packages。如果改成用 `dotnet nuget add source` 存令牌，源会登记到用户级，所有没写 `<clear />` 的仓库还原时都会去查它；令牌一过期或 GitHub 连不上，它们的还原就会因 `NU1301` 失败。笔记本上 v2 线的 `8005-agv-onboard-hmi`、`slots-simulator`、`8005-mes-ingest` 和 riot-sdk 都没有自己的 `nuget.config`，会受影响。用户级配置里已经登记了名为 `github` 的源（例如以前照旧写法存过）时，脚本会删掉那一行并说明。

令牌从头到尾不出现在任何命令行上：不进 PowerShell 的历史记录，也不会作为 `dotnet.exe` 的参数被开了命令行审计的机器记下来。仓库里不放任何令牌。

没做这一步时，还原 `RIoT.Sdk.*` 会因为 401 失败。CI 不用这一步：它用每次运行自带的 `GITHUB_TOKEN`。

## 构建与测试

需要 .NET 10 SDK（版本见 `global.json`）和 SQL Server 2022 LocalDB（实例 `MSSQLLocalDB`）。还原之前先做完上一节那一步。

```
dotnet build simple.slnx
dotnet test --solution simple.slnx
```

测试跑在 Microsoft Testing Platform 上，筛选测试用 `--filter-class`、`--filter-method`、`--filter-trait` 等选项，不是 VSTest 的 `--filter`。

需要数据库的测试每次建一个随机名字的库（`Simple_test_<随机串>`），测完删掉，几个 worktree 同时跑也互不干扰。默认建在本机 LocalDB 上；要用别的 SQL Server 实例，把不带库名的连接串放进环境变量 `SIMPLE_TEST_SQLSERVER`，例如 `Server=.\SQLEXPRESS;Integrated Security=true;TrustServerCertificate=true`。

测试里起的服务端，其证书钥匙默认放在当前用户的密钥存储里。环境变量 `SIMPLE_TEST_SERVER_KEY_STORE` 可以改成 `Machine`（本机级存储，建和删都要管理员）。`simple remote` 在远端运行时会设成 `Machine`，因为经 SSH 密钥登录的会话用不了用户级存储。取 `User`、`Machine` 以外的值时测试直接失败，不会悄悄改用用户级存储。

## 在本机起停整套系统

仓库根的 `simple.ps1` 先编译命令行本身（安静地编译，失败才显示输出），再把参数原样交给它。在 PowerShell 7 里：

```
./simple up          # 编译一次，起齐所有部件，全部就绪才返回
./simple up --terminals 3   # 再起 3 个无界面车载终端
./simple status      # 每个进程在不在、就绪没有，以及服务端自己的状态
./simple events --since 5m
./simple logs server --tail 50   # 服务端自己写的程序日志
./simple logs server --output    # 服务端打到控制台上的输出（启动被拒的原因、崩溃时运行时打印的内容）
./simple down        # 正常停止本实例起的进程
./simple down --reset   # 再删掉本实例的库
```

从别的 shell 调用写成 `pwsh -File <clone>/simple.ps1 up`。每个命令都接受 `--json`：标准输出上只有一个 JSON 对象 `{exitCode, error, result}`，进度提示写在标准错误上。退出码是固定的，`./simple --help` 列出全表：0 成功；1 意外错误；`down` 之后仍有进程在跑；或进程停后程序目录里仍有文件被占用（`up`、`down`，state 文件保留，再跑一次即可）；2 参数或设置不对；3 编译失败；4 有部件没就绪（已起的会被停掉）；5 实例没在运行（`logs`：没有这份日志，或还没有程序日志）；6 实例不健康、只起了一半，或是从另一个 clone 起的；7 另一条 simple 命令正在操作这个实例；8 有进程超时没停下、被强杀；9 连不上 SQL Server，或删库、删服务端钥匙失败。

目前系统里有五种部件，按这个顺序起：假 RIoT、MES 网关（背后接假 MES）、服务端（启动参数里带上假 RIoT 和网关的地址）、IO 模拟器（每个车载终端配一个）、无界面车载终端（`--terminals N` 个，默认 0）。`simple status` 除了列出每个进程，还有一行写服务端最近从网关看到了什么（网关通不通、最近一轮是第几轮、几点读的、多少行），一行写服务端连不连得上 RIoT，以及服务端看到的每台车载终端。每个部件在 `src/Simple.Cli/Components.cs` 里登记一项：从哪个项目编译、起几个进程、给哪些参数、怎么判就绪，以及起之前要准备什么；以后加部件，也是往这里加一项。部件按登记的顺序一个一个起，前一个全部就绪才起下一个，因为后面的部件可能要用前面的：服务端第一次去网关取数时网关已经在了，车载终端要先从服务端的状态接口读到证书指纹。

- **实例**：不写 `--instance` 时，实例名取 clone 所在文件夹的名字（例如 `29-simple-cli`），所以两个 worktree 各起一套互不干扰。库名是 `Simple_<实例名>`；端口按实例名在 20000～29999 之间选一段，被占用就往后挪，同一个实例每次拿到的端口一般不变。
- **起**：`up` 先 `dotnet build` 各部件的项目，再把编译输出复制到 `artifacts/simple/<实例名>/` 下运行，所以系统起着的时候照样可以重新编译、跑测试，不会碰到文件被占用。部件是独立的进程，命令行返回后它们继续运行；每个进程都被告知自己的程序日志目录（`--Simple:Logs:Directory`，见"程序日志"一节），打到控制台上的输出另外写进一个文件。重复执行 `up` 不会起第二套。
- **停**：`down` 只停本实例的状态文件里记下的进程（按进程号和进程启动时间核对，绝不按进程名），用 Ctrl+Break 让它们正常退出，服务端因此会在事件记录里留下 `server.stopped`；30 秒还没退出才强杀，并以退出码 8 报告。
- **库**：默认用本机 LocalDB `(localdb)\MSSQLLocalDB`。要用本机别的命名实例（CI 用 `.\SQLEXPRESS`），把不带库名的连接串放进 `SIMPLE_SQLSERVER`。命令行拒绝别的机器上的 SQL Server、本机的默认实例（`.`、`localhost`、`MSSQLSERVER`）、带端口的写法（带端口时 SqlClient 不看实例名，本机 1433 上通常是默认实例）、故障转移伙伴，以及带 SQL 登录用户名或密码的连接串（连接串会记在状态文件里，所以本机只用 Windows 集成认证）；起服务端和删库之前，还会连上去问服务器自己是谁（`SERVERPROPERTY` 的 `IsLocalDB`、`InstanceName`、`MachineName`），只接受 LocalDB，或本机上名字不是 `MSSQLSERVER` 的命名实例，这样别名和端口转发也绕不过去。`down --reset` 只删 `Simple_<实例名>` 这一个库，删在 `up` 当时用的那个 SQL Server 上。
- **只起本机的假系统**：命令行没有任何指向真实系统的开关。起部件时会去掉环境里以 `Simple__`、`ConnectionStrings__`、`ASPNETCORE_`、`Kestrel__` 开头的变量和 `DOTNET_ENVIRONMENT`，免得外面设好的真实地址、连接串、监听地址或运行环境被带进本机系统；部件总是以 Production 运行。
- **管理员密码**：服务端的接口要登录（见"只运行服务端"一节的"登录"）。第一次 `up` 给这个实例随机生成一个管理员密码，存在 clone 之外的 `%LOCALAPPDATA%\simple\instances\<实例名>\admin-password.txt`；每次起服务端之前，`up` 先用服务端命令 `set-admin-password` 把它设进库里。`status`、`events`、判服务端就绪、给车载终端写配置时，命令行都用它经控制台入口登录，用完退出。`down --reset` 删库时一起删掉它，下次 `up` 再生成新的。自己调接口时也用这个密码登录（账号名 `admin`）。
- **文件放在哪**：状态文件和日志在 `%LOCALAPPDATA%\simple\instances\<实例名>\`（可用环境变量 `SIMPLE_HOME` 换位置）。每个进程的程序日志在 `logs\<进程名>\`，由程序自己限制大小，跨多次 `up` 保留，`simple logs <进程名>` 显示它（还没有程序日志或日志是空的，例如进程在日志起来之前就拒绝启动了，命令以退出码 5 说明这一点，并提示改用 `--output`）；控制台输出在 `logs\<进程名>.log`，`down` 之后保留，下次 `up` 时覆盖，`simple logs <进程名> --output` 显示它（`simple logs build --output` 是最近一次编译的输出）。`simple status` 每个进程一行的末尾两样都写：`log` 后面是程序日志目录，`output` 后面是控制台输出文件。每个车载终端的本机配置在同一目录的 `processes\terminal-<n>\terminal.json`，`up` 时按服务端的状态重新写。
- **车载终端**：`terminal-1`、`terminal-2`…分别自报车辆 `vehicle-1`、`vehicle-2`…，连本实例服务端的终端端点。`./simple status` 末尾按服务端看到的样子列出每台车载终端：连着没有、上报新不新鲜、最新鲜的那份上报最多是几秒前写的、版本、屏上文字。每个终端在测试模式下开一个只听回环地址的测试输入（地址就是 `status` 里该进程的 URL），用来代替车旁的人：
  - `POST /test/keyboard`，正文 `{"text": "LOT123\r"}`：像扫码枪那样敲键盘，每个回车结束一次扫码；
  - `POST /test/press`，正文 `{"button": "<按钮名>"}`：按屏上的一个按钮；
  - `GET /test/status`：连接状态和屏上文字；`GET /test/ready`：连着时 200。
  - 终端没连上服务端时，扫码和按键都返回 409 并被丢掉，和车上真屏一样。
- **IO 模拟器**：`io-simulator-1`、`io-simulator-2`…和车载终端一一配对，`terminal-n` 经 Modbus TCP 读 `io-simulator-n`（`up` 把模拟器的 Modbus 地址写进终端的 `terminal.json`，字段 `ioModule`）。模拟的是车上那块 C2000 IO 模块加 8 仓料柜：只认 Unit ID 255，16 路输出（线圈地址 100 起）、16 路输入（离散量地址 200 起）；第 n 仓的开锁是输出 n，锁反馈是输入 n（锁上为 1），光幕是输入 n+8（有货为 0）。开锁输出按脉冲模式 500 ms 后自己回到 0，门随即弹开，100 ms 后锁反馈变开；关门 100 ms 后锁反馈变锁上；放货、取货 50 ms 后光幕翻转。车载终端每 200 ms 读一次全部 32 个点，把原始值、本次启动以来变了几次、距上次变化多久、能不能读到模块填进上报；`./simple status` 在每台终端下面多一行，列出它报的输入和输出。模拟器的控制接口就是 `status` 里该进程的 URL，只听回环地址，用来代替仓前的人和故障：
  - `GET /api/v1/state`：16 路输出、16 路输入的原始值（从通道 1 起），每仓的门、货和被固定的传感器，当前通信故障，自动应答的进度；
  - `POST /api/v1/slots/{n}/load`、`/unload`、`/close`：放货、取货、关门；门关着时放货取货返回 409 `door-not-open`。没有开门、开锁的接口：门只有车载终端写开锁输出才会开；
  - `PUT /api/v1/slots/{n}/lock-feedback`、`/light-curtain`，正文 `{"value": 0}`、`{"value": 1}` 或 `{"value": null}`：把锁反馈或光幕固定成某个读数，不管门和货实际怎样，`null` 取消；
  - `PUT /api/v1/modbus-fault`，正文 `{"mode": "no-response"}`、`{"mode": "disconnect"}`、`{"mode": "delay", "delayMs": 300}` 或 `{"mode": "none"}`：通信故障，分别是连着但不回应、断开并拒绝新连接、每个请求晚回、恢复；
  - `POST /api/v1/auto-responder`，正文例如 `{"action": "load", "doors": 3, "actSeconds": 4, "closeSeconds": 4, "withinSeconds": 300}`：接下来开的 3 扇门，每扇开后 4 秒放一件货（`unload` 则取一件），再过 4 秒关门。门在动作前被人关了、往有货的仓放、从空仓取、到期没处理完，都算失败并停下；`POST /api/v1/auto-responder/wait?timeoutSeconds=60` 一直等到做完才返回 200，失败、被取消或等到超时都返回 409，并带上原因，脚本和场景据此判成败。`GET` 看进度，`DELETE` 停下。
- **服务端钥匙**：本实例服务端的证书钥匙放在当前 Windows 用户的密钥存储里，名字是 `Simple-Server-<实例名>`。`down` 不动它，下次 `up` 指纹不变；`down --reset` 连库一起删掉它。`up` 把钥匙放在哪个存储记在实例目录的 `serverkey.txt` 里，`down --reset` 只按这份记录去删；没有记录时两种存储都查，删掉存在的那一把；删不掉就以退出码 9 报错并保留记录，下次还能再删。

`Simple.Cli.Tests` 里的冒烟测试经编译好的命令行真起真停随机名字的实例：两套只有服务端的实例互不干扰；一套带 3 个车载终端的实例里，终端上报新鲜、经测试输入扫的码进了事件记录、被杀掉的终端 10 秒内记成上报过期、`down` 后不留进程；一套带 2 个车载终端的实例里，服务端看到的每台终端的 16 路输入与它配对的模拟器一致，经控制接口固定一个光幕 2 秒内传到服务端、变化次数加 1，让模拟器不回应后 10 秒内终端报读不到 IO 模块、服务端记下 `onboard.io-unreadable`。`dotnet test` 时随其他测试一起跑，CI 上也跑。

## 把重活送到远端机器上跑

改代码留在本机，完整编译、全量测试、起整套系统这些重活可以送到另一台 Windows 机器上跑。远端是哪台机器只从环境变量 `SIMPLE_REMOTE` 读，它的值是本机 SSH 配置（`~/.ssh/config`）里的一个主机别名，仓库里不写任何机器名或地址。

```
./simple remote build
./simple remote test                                # 全量测试；后面的参数原样交给 dotnet test（MTP 选项）
./simple remote test --project tests/Simple.Cli.Tests --filter-class "*CommandLineTests"   # 只跑一个类时要带 --project
./simple remote up                                  # 远端起整套系统，返回后系统继续运行
./simple remote status                              # 还有 down、events、logs，转给远端那份 simple
./simple remote exec -- dotnet --info               # 在远端的工作树里跑任意命令
./simple remote forget                              # 停掉、删库、删远端目录；删 worktree 之前用
```

- **只跑一个类时用 `--project`**：`simple remote test` 默认对整个解决方案跑 `dotnet test --solution simple.slnx`。这时按类过滤（`--filter-class`），其余测试项目一个测试都匹配不到，各自以 8（一个测试都没跑到）结束，整次运行也就以 8 结束。带上 `--project <测试项目路径>`（必须在本仓库里），就只跑那一个项目，跑通了以 0 结束。
- **跑的恰好是本机这棵树**：每次先把工作树做成一个快照提交，包括没提交的改动和还没 `git add` 的新文件，按 `.gitignore` 排除被忽略的文件；用的是一份临时索引，不动真正的索引和工作区。作者、时间都是固定值，所以同一棵树每次得到同一个提交号；工作树干净时就用 `HEAD` 本身。远端检出这个确切的提交再跑，开头和结尾各打印一行：提交号、基于哪个 `HEAD`、几个未提交的文件、哪台机器。
- **怎么送**：只把 `origin/main` 之后的部分（本地没推送的提交加快照）打成 git bundle，经 scp 送到远端的裸仓库缓存；远端缺前置对象时从 origin 拉一次（拉进这个实例自己的 `refs/simple-remote-origin/<实例名>`，不和别的实例共用，被断线打断留下的锁下次运行自己清掉；`forget` 时删掉），还不行就送完整历史。远端已经有这个提交就什么都不送。不往 GitHub 推任何分支。快照里新增了超过 10 MiB 的文件时列出来并拒绝发送。
- **远端的目录**：`C:\simple-remote\` 下有 `repo.git`（所有实例共用的对象缓存）、`wt\<实例名>`（每个 worktree 一个检出目录）、`runs\<实例名>`（每次运行的结果，保留最近 20 次）、`locks`、`slots`。实例名与本机相同，所以远端的 `simple up` 也按本机那套实例规则分开端口和库。**注意**：本机上两个文件夹同名的 clone（例如两台机器上都叫 `repo`）到了远端是同一个实例、同一个目录，本机"实例是从另一个 clone 起的"那道保护在远端不起作用，后跑的会改掉先跑的检出；这种情况下用 `--instance` 给其中一个起别的名字。检出时清掉残留文件，但保留被忽略的 `bin/`、`obj/`、`artifacts/`，编译是增量的。
- **排队**：同一个实例同一时刻只跑一条远程命令，后来的等着，并打印在等谁；`build`、`test`、`exec`、`up` 还要占用整机 4 个名额中的一个。等 30 分钟还轮不到就以退出码 22 结束。
- **结果**：输出实时回传；退出码是远端命令自己的（例如测试的 2、8 原样返回）。`simple remote` 自己的失败用 20（没跑成：git、ssh、scp 出错，或远端在起命令之前失败）、21（快照含大文件，没送）、22（等不到实例或名额）；`--json` 时标准输出是一个 `{exitCode, error, result}`，`result` 里有提交号、`HEAD`、未提交文件数和远端退出码。`build`、`test`、`exec` 的输出和测试结果（TRX）拷回本机 `artifacts/remote/<实例名>/<时刻>-<提交号前 8 位>/`；`exec` 的命令把文件写进 `%SIMPLE_REMOTE_RUN%` 也会拷回来。
- **远端也只起本机的假系统**：远端的命令把 `SIMPLE_SQLSERVER` 和 `SIMPLE_TEST_SQLSERVER` 指向那台机器上专给它用的命名实例 `.\SIMPLE_DEV`，`simple` 在那台机器上照样只接受本机的 LocalDB 或命名实例；`simple remote` 没有任何指向真实系统的开关。
- **断线**：远端那台机器的 sshd 在笔记本这头断开后，既不结束会话，也不结束会话里的进程（simple01 上 2026-10-17 实测）。所以本机每 5 秒往 ssh 写一行心跳，远端每 0.5 秒检查一次：察觉断开后，结束这次运行的整棵进程树（包括它起的子进程、`up` 已经起来的部件），然后退出，实例锁和名额随之放掉。命令开跑之前从 origin 拉取、导入 bundle 这些可能要几分钟的 git 操作也一样在检查之列，断开后连 git 一起结束。察觉要多久：
  - 强杀本机的 `simple` 或 ssh：约 1–2 秒（远端读到输入结束）；
  - Ctrl+C：同上，约 1–2 秒；
  - 断网：远端在最后一次心跳之后约 30 秒结束运行；本机 ssh 也在约 30 秒内放弃（`ServerAliveInterval=10` × 3），以 20 结束。

  `up` 已经成功返回之后再断开不影响它起的系统：部件脱离 SSH 会话继续运行，用 `simple remote down` 停。
- **残留自动清理**：被断线结束的运行来不及让测试收尾，会在远端留下测试库（`.\SIMPLE_DEV` 上的 `Simple_test_<12 位十六进制>`）和本机级存储里的测试钥匙（`Simple-Server-test_<12 位十六进制>`、`Simple-Test-<32 位十六进制>`）。`build`、`test`、`exec`、`up` 开跑之前，远端先看整机 4 个名额是不是都空着：都空着说明没有别的测试在跑，就占住全部名额，删掉名字恰好是上面这几种格式的库和钥匙，逐个打印删了什么，再放开名额照常排队；有名额被占就这次不清，留给下一次。只认这几种测试命名；worktree 实例自己的库 `Simple_<实例名>` 和钥匙 `Simple-Server-<实例名>` 一般不是这种格式，即使实例名恰好像测试实例（例如 `--instance test_0123456789ab`），只要远端有这个实例的检出目录 `wt\<实例名>`，它的库和钥匙也不删。清理出错只打印一行，不影响这次运行。所以那台机器的 `.\SIMPLE_DEV` 和本机级钥匙存储只给 `simple remote` 用：不经 `simple remote`、直接 SSH 上去跑测试，它的库和钥匙可能被当成残留删掉。另外，每次运行（任何命令）取得实例锁后，还删掉 7 天没有被任何运行用过的旧版 agent 脚本（`bin\agent-*.ps1`，每个版本的命令行装一份自己的；还在用旧版的 clone 下次会自动再拷一份）和一天以前没导入完的 bundle（`incoming\*.bundle`）。
- **远端的服务端钥匙在本机级存储里**：经 SSH 密钥登录的会话用不了按用户加密的 Windows 存储（DPAPI），所以 `simple remote up` 让服务端把钥匙放在那台机器的本机级密钥存储里。**本机级的钥匙只有管理员能建、能删**，所以远端的 SSH 账户必须是那台机器的本机管理员。`simple remote down --reset` 和 `forget` 按 `up` 时的记录把它删掉。

为什么手写这一层：现成的同步工具（Mutagen、Syncthing 等）都是持续同步型的，说不出某一次运行看到的是哪个状态的树，"同步到一半就开跑"正是要避免的；`simple remote` 只是 git、ssh、scp 之上的一层薄胶水，"这次跑的是哪棵树"由提交号保证。

## 程序日志

服务端、MES 网关、车载终端（WPF 外壳和无界面版）、IO 模拟器、假 RIoT 都用 Serilog 写程序日志（ADR 0034），接在 `Microsoft.Extensions.Logging` 后面，代码里照常用 `ILogger`。程序日志是程序自己的调试记录，不是事件记录：统计和"谁按了什么"仍然只查事件记录（ADR 0019）。

- **写在哪**：本机的 `<目录>\<程序>.log`，满了另起 `<程序>_001.log`、`<程序>_002.log`……，超过保留个数就删最旧的。目录默认是 `%ProgramData%\Simple\<程序>\logs`（程序是 `Server`、`MesGateway`、`OnboardTerminal`、`IoSimulator`），每个文件默认 10 MB、保留 10 个，所以一个程序最多约占 100 MB。三项都能改：`Simple:Logs:Directory`、`Simple:Logs:FileSizeLimitBytes`、`Simple:Logs:RetainedFileCount`。服务端和 MES 网关和其他设置一样读（命令行、环境变量、`Simple:SettingsFile`）；车载终端和 IO 模拟器的界面版只从命令行读，例如 `--Simple:Logs:Directory=D:\logs`；无界面的 IO 模拟器和假 RIoT 一样读（命令行、环境变量、`appsettings.json`）。一个文件写到上限时，正在写的那一条仍然完整写完。同一台机器上同一个程序跑两份时（例如上面"再起一套"服务端），给它们不同的目录，否则两份日志会交错在同一组文件里；`simple up` 已经给每个进程单独一个目录。
- **每次启动的第一行**：`<程序> started (version …, process …)`，即使程序别的什么都没说，也能从日志里看出它起过几次、是哪个版本。
- **凭据不进日志**：脱敏在日志这一层统一做，控制台输出和文件都经过它。属性名表明是凭据的值（名字里含 `Password`、`Token`、`Secret`、`ApiKey`、`PairingCode` 等，或以 `Key` 结尾，例如 `SigningKey`；`ServerKeyName` 这种中间带 Key 的不算）整个换成 `***`。文字里的凭据只去掉值、保留名字：连接串的 `Password=`/`Pwd=`（带引号 `"a""b"`、ODBC 大括号 `{a;b}` 的整体去掉）、`--Simple:...:Password=x` 和 `--Simple:...:Password x`、查询串的 `api_key=`、JSON 的 `"password": "…"`、对象里的 `Password = …`、网址里的 `user:密码@`（密码里有没转义的 `@` 也一样）、`Authorization:` 后面的凭据（不论 Bearer、Basic、Token、ApiKey 哪种方案）、`Cookie:`/`Set-Cookie:` 的整个值、JWT、PEM 私钥（缺了 END 行的从 BEGIN 去到结尾）、GitHub 令牌、"配对码 123456"。异常的消息和内层异常也一样。值在连接串里取到下一个 `;`（可以含空格），在别处取到空格或 `&`、`,`、`;`、`)` 这类分隔符为止，所以 `password=x&user=y` 只去掉 `x`。配对码只在写明是配对码时去掉，不按"像不像 6 位数"去猜：批次号、机台号、站点号常常就是一串数字，要在日志里照常看得到；单独的 `Basic`、`Bearer` 后面跟的是普通单词时也不去掉。宁可多去掉一点：被去掉一个无害的值，这行日志照样能用；漏一个凭据就不行。
- **不拖慢写日志的线程**：脱敏在写日志的那个线程上做（WPF 外壳里就是界面线程），所以每条规则都只从词首起匹配，耗时随文字长度线性增长，不会因为一个长值卡上几分钟。常见的 1 MB 长串在 1 秒左右做完；最费时的形状（例如 1 MB 的 `a=a=a=…`，每处都要查一次名字）在 1.5 秒左右。每条规则各有 1 秒的时限，所以一条日志最坏要等"规则条数 × 1 秒"；超时的那条日志，正文整段换成 `*** (not shown: redacting it took too long)`，宁可看不到也不漏。脱敏本身出了别的错时，正文换成 `*** (not shown: the log line could not be redacted)`，这一行照样写出、不和下一行粘在一起。
- **没接住的异常**：进程级的未处理异常记成 `FTL` 后进程照常结束；没人等的失败任务记成 `ERR`，不改变它原来的处理；WPF 界面线程上没接住的异常记成 `FTL`（带完整堆栈）后照常交给 WPF，程序随之结束时再记一句 `The exception above, which nothing in the UI thread caught, ends the process`，不重复堆栈。
- **拒绝启动时**：服务端、MES 网关、无界面车载终端因设置不对拒绝启动时（退出码 2），原因除了写在标准错误上，也写进程序日志（`FTL`），因为作为 Windows 服务运行时标准错误没人看。只有日志设置本身不对时（上限不是正整数）写不进日志，只在标准错误上。IO 模拟器遇到这种情况弹框说明后退出；车载终端的 WPF 外壳不因日志设置起不来，改用默认上限并在日志里写一行警告。

## 只运行服务端

```
dotnet run --project src/Simple.Server
```

默认接口只监听 `http://127.0.0.1:5180`，车载终端端点监听 `https://127.0.0.1:5181`，连本机 LocalDB，库名是 `Simple_<实例名>`（默认实例名 `dev`，即 `Simple_dev`）；第一次启动时自动建库并执行迁移。

除了"进程活着"的健康检查和登录接口，每个接口都要先登录（ADR 0045），"谁能用"一列见下面的"登录"。

| 接口 | 谁能用 | 用途 |
| --- | --- | --- |
| `GET /health/live` | 不用登录 | 进程活着 |
| `GET /health/ready` | 看 | 能连上自己的数据库 |
| `GET /api/v1/status` | 看 | 版本、启动时间、实例名、库名；车载终端端点的地址和证书指纹；每台车载终端的版本、连着没有、最新鲜的上报最多是几秒前写的、是否已过期、最近下发的屏上文字；RIoT 一栏（连不连得上：`not-configured`、`connecting`、`connected` 或 `unreachable`，地址、最近一次问和最近一次连上的时刻、出错原因、地图数）；MES 网关一栏（见下一节） |
| `GET /api/v1/events` | 看 | 事件记录，按提交顺序（顺着游标一直往后读不会漏事件）；参数 `after`（游标，即上一页的 `nextCursor`）、`since`、`until`、`limit`（1～1000，默认 100） |
| `GET /openapi/v1.json` | 看 | 接口描述 |
| `POST /api/v1/login`、`POST /api/v1/logout`、`GET /api/v1/session`、`PUT /api/v1/session/password` | 见"登录" | 登录、退出、我是谁、改自己的密码 |
| `GET`、`POST /api/v1/accounts`，`DELETE /api/v1/accounts/{name}`，`PUT /api/v1/accounts/{name}/password`，`PUT /api/v1/accounts/{name}/paused` | 管账号 | 账号列表（每个账号最后一次来的时间、此刻在几台电脑上登录着）、建账号、删账号、给别的账号设密码、值守账号的"暂停值守" |

在同一台机器上再起一套，换实例名和端口：

```
dotnet run --project src/Simple.Server -- --Simple:Instance=wt25 --urls=http://127.0.0.1:5182 --Simple:OnboardTerminals:Url=https://127.0.0.1:5183
```

服务端默认连 `http://127.0.0.1:5200` 上的假 RIoT（`Simple:Riot:Url`、`Simple:Riot:CallApiKey`，仓库里的密钥是假 RIoT 认的占位串）。每 10 秒读一次 RIoT 的地图列表，看连不连得上；连不上时按逐步拉长、最长 30 秒的间隔一直重试（ADR 0008），不会因为 RIoT 掉线而退出。`Simple:Riot:Url` 留空时不连 RIoT，状态里写 `not-configured`。这里只读，不建单、不发命令。

仓库里的配置只指向回环地址和 LocalDB。真实的连接串和地址放在仓库外：用 `--Simple:SettingsFile=<路径>` 指定一个 JSON 配置文件，或用环境变量（例如 `ConnectionStrings__Simple`、`Simple__Instance`）。连接串里不写库名，库名由实例名决定。配置不合法时服务端拒绝启动，退出码为 2，并逐项说明是哪一项不对；控制台的 HTTPS 地址（ADR 0046）做好之前，接口只听回环，所以接口的监听地址（`urls`）不是回环地址、或不是 `http://` 时也拒绝启动。

### 登录

做法见 ADR 0045，要点如下。

- **管理员密码只有一个设法**：在服务端那台机器上运行服务端命令，密码从标准输入读（不写在命令行上，命令行别人看得到），用和服务端相同的设置（实例名、连接串、`Simple:SettingsFile`）：

  ```
  dotnet run --project src/Simple.Server -- set-admin-password
  ```

  在控制台窗口里直接运行时，输入的密码不显示在屏幕上。第一次运行时建管理员账号 `admin`，以后运行是重设它的密码；先执行迁移，再设密码，管理员在所有电脑上的登录随之作废（正在运行的服务端最多 10 秒后发现）。网页和接口上没有这个功能。管理员记得旧密码时，用 `PUT /api/v1/session/password` 改自己的密码。
- **登录**：`POST /api/v1/login`，内容 `{"name": "...", "password": "...", "entry": "console" 或 "mcp"}`。成功时回账号、角色、项目和入口，并设一个登录 Cookie `__Host-simple`（`Secure`、`HttpOnly`、`SameSite=Strict`，7 天有效）；账号名或密码不对一律回 401，不说是哪一样不对。Cookie 里只有一把加密的随机钥匙，谁登录着、从哪台电脑、经哪个入口、什么时候来过，都记在服务端库里的登录记录上，每个请求按服务端内存里那一份核对，库短暂连不上也不把人踢出去。库里的登录记录只存这把钥匙的 SHA-256，拿到库的副本也拼不出一个能用的登录。
- **同一台电脑再登录**：浏览器还登录着时再登录（例如班组长没退出、工程师来登录），旧的那次登录作废，换成新的一次；新的登录失败时，旧的照旧。
- **登录接口收什么**：请求体最多 4 KB（改自己密码的接口也一样）；账号名不像账号名的（空、超过 100 个字符、带换行之类的控制字符）不去查库，直接按"不对"回 401。程序日志只写存在的账号名，不存在的一律写成"unknown account"，免得有人往账号框里敲进密码或伪造日志行。
- **多久失效**：每天第一次使用时，Cookie 续成新的 7 天；7 天没用过的登录作废。退出（`POST /api/v1/logout`）删掉这一次登录，复制走的 Cookie 也不能再用；改密码、删账号让这个账号在所有电脑上的登录立刻作废，正好赶在改密码那一刻用旧密码登录的也一样（账号上记着凭据版本，改密码就加一，旧版本的登录一律不认）。退出时库正好连不上，这次登录在内存里立刻作废，库里那一行等下一次写库时再删；在那之前服务端若重启，这次登录会从库里读回来、重新有效。服务端重启不掉登录：加密 Cookie 的钥匙存在库里（`DataProtectionKeys` 表），每把钥匙再用本机级 DPAPI 加密，所以库搬到别的机器上恢复后，所有人要重新登录一次。本机级的意思是：factory01 上任何能运行程序的 Windows 账号都解得开这些钥匙，用它们能解开截获的 Cookie；能在生产服务器上运行程序的人本来就什么都能做，这一层防的是库被拷走。
- **改东西的请求要带请求头** `X-Simple-Request: 1`（POST、PUT、PATCH、DELETE，登录也算），没带回 400。服务端不开跨域访问（CORS），别的网站的网页发不出带这个头的请求，借已登录的浏览器偷偷发请求就做不到。服务端也不发 HSTS（ADR 0046）。
- **防猜密码**：同一台电脑（按来源 IP）每分钟最多 5 次登录或改自己密码的尝试，多了回 429 并在 `Retry-After` 里说多少秒后再试；不锁账号。登录成功也算一次（框架的限速器按请求数），一次登录管 7 天，正常使用碰不到。上限可以改：`Simple:Login:AttemptsPerWindow`、`Simple:Login:AttemptWindow`；`simple up` 起的本机系统把它调大，因为命令行每次查状态都从回环登录。
- **谁能用**：每个接口在代码里标明一个"谁能用"（`src/Simple.Server/Accounts/Access.cs`），没标的接口谁都用不了，并有测试逐个检查。免登录的只有登录接口和"进程活着"；"车载终端"这一级只能标在车载终端协议上，标在别的接口上服务端起不来。gRPC 对不认识的服务和方法不另设兜底接口，那类请求和其他找不到的路径一样处理：在回环上的接口地址，改东西的请求没带 `X-Simple-Request` 先回 400，带了而没登录回 401；在车载终端端点上一律 404。

  | 谁能用 | 班组长 | 工程师 | 管理员 | 值守账号 | 经 MCP 的登录 |
  | --- | --- | --- | --- | --- | --- |
  | 看 | 能（只看本项目，按业务核对） | 能 | 能 | 能 | 能 |
  | 看全部项目 | | | 能 | 能 | 管理员、值守能 |
  | 班组长的动作 | 能 | 能 | 能 | | |
  | 工程师的动作 | | 能 | 能 | | |
  | 改参数配置 | | | 能 | | 管理员能（事件记录保存天数除外） |
  | 管账号 | | | 能 | | |
  | 改自己的密码 | 能 | 能 | 能 | | |

  "暂停值守"开着时，值守账号的每个请求（包括登录）都回 403，问题类型 `watch-paused`。
- **账号**：班组长、工程师账号必须写所属项目，值守账号不写；值守账号整个系统只有一个；管理员账号只能由服务端命令建，不能经接口建、删或设密码。服务端里还没有项目表，项目先按名字存，以后有了项目再改成引用（ADR 0015、0017）。密码规则只有一条：至少 8 个字符；另有一个上限 128 个字符，不是规则而是登录接口的请求体只收 4 KB，设得更长就永远登录不上。只存哈希（PBKDF2-HMAC-SHA512，22 万次），旧的弱哈希在下次登录时自动升级；升级只在密码没被同时改掉时写回，否则这次登录按旧密码处理、回 401。
- **记在哪**：建账号、删账号、设密码、开关暂停值守进事件记录，写账号、入口和来源 IP，不写密码。登录、退出、登录失败只进程序日志（写来源 IP），不进事件记录。

### 车载终端端点

车载终端经一条 gRPC 双向流连服务端（ADR 0024、0025），只有"车载上报"和"下发状态"两种整份消息，定义在 `src/Simple.Protocol/Protos/onboard_v1.proto`。协议大版本写在连接的请求头 `simple-protocol-major` 上（ADR 0026），服务端不支持的大版本以 `FAILED_PRECONDITION` 拒绝，并在 trailer `simple-supported-majors` 里列出它支持的大版本。

- **地址**：`Simple:OnboardTerminals:Url`，只能是 `https://`，要写明一个具体的 IP 地址（或 `localhost`），不接受 `0.0.0.0`、`[::]`、`*` 这类所有网卡一起监听的写法，也不接受主机名。它和接口不同，可以是别的机器连得到的地址：这个端点上只有车载终端协议，走 TLS 1.2，其他请求（包括状态接口）一律 404；反过来，接口的回环地址上也找不到车载终端协议。
- **证书**：服务端第一次启动时在运行它的 Windows 账号的密钥存储（CNG）里建一把有名字的 ECDSA P-256 钥匙，默认名字 `Simple-Server-<实例名>`（`Simple:OnboardTerminals:ServerKeyName`；`Simple:OnboardTerminals:ServerKeyStore=Machine` 改放本机级存储），以后每次启动都用这把钥匙现做一张自签证书。车载终端只认证书公钥的 SHA-256（指纹），所以钥匙不变指纹就不变；指纹在状态接口的 `onboardTerminalEndpoint.certificatePin` 里。钥匙可以加密导出，留给部署备份：丢了它，每台车载终端都要重新配置。
- **新鲜度**：每份消息带自己的序号和最近收到的对方序号。服务端按自己的钟算一份上报最多是多久以前写的（它确认收到的那份下发状态是什么时候发出的），10 秒内才算新鲜；最新鲜的那份超过 10 秒，记 `onboard.stale`，重新收到新鲜上报时记 `onboard.fresh`。连接是否还开着不参与判断。
- **事件**：`onboard.connected`（新的流上收到第一份上报）、`onboard.disconnected`（流结束及原因）、`onboard.stale`、`onboard.fresh`、`onboard.refused`（大版本不支持、上报里没有车辆标识）、`onboard.input-received`（车旁的人扫了码或按了按钮；这一版只记录，不处理）。

## 只运行无界面车载终端

```
dotnet run --project src/Simple.OnboardTerminal.Headless -- --config=<配置文件> [--test-input=http://127.0.0.1:<端口>]
```

配置文件放在仓库外，不写 `--config` 时是 `%ProgramData%\Simple\OnboardTerminal\terminal.json`，内容是本车的车辆标识、服务端地址（不写时是本机 `https://127.0.0.1:5181`）和服务端证书指纹：

```json
{ "vehicleId": "vehicle-1", "server": "https://127.0.0.1:5181", "serverCertificatePin": "<状态接口里的 certificatePin>" }
```

缺车辆标识或指纹时拒绝启动，退出码 2。终端只认指纹对得上的服务端，对不上时在日志里写明钉住的和对方出示的指纹，不连。断线后自己重连，间隔从约 1 秒开始逐步拉长、带随机抖动，最长 25 秒。`--test-input` 打开测试输入（见上一节），只接受回环地址。

事件记录只能追加：数据库里的触发器拒绝一切修改和删除，也拒绝没有经过 `EventStore.AppendAsync` 的插入。后者在事务里先拿一把排他的应用锁再插入，提交时才放开，所以事件编号的顺序就是提交顺序。删掉 `Simple_<实例名>` 这个库，事件记录就清空，下次启动重新建库。

改了数据模型后，用本仓库钉住版本的 `dotnet-ef` 生成迁移：

```
dotnet tool restore
dotnet ef migrations add <迁移名> --project src/Simple.Server --output-dir Persistence/Migrations
```

## MES 网关与假 MES

MES 网关（`src/Simple.MesGateway`）是一个单独的小进程，是整个系统里唯一和 MES 打交道的地方。它按轮读 MES，每一轮的结果整份留在内存里；服务端每隔一段时间来取一次这整份快照。连线方向只有一个：服务端连网关，网关不需要知道服务端在哪。

目前网关只有一个来源：假 MES（`Simple:Mes:Source=Fake`，也是唯一允许的值）。读真 Oracle 是以后单独的票，要先拿到客户批准的查询。假 MES 从 `Fake/seed-rows.json` 读起始行，那是一个虚构的小厂区（区域号 `S1-1` 这类，任务类型 `SAMPLE_*`），不是 D8 的真实数据。每一行就是上一代主查询的 7 列：`TASK_TYPE, SUBLOT, AREA, EQP, STEP, DATES, PACKAGE`。

单独运行：

```
dotnet run --project src/Simple.MesGateway
```

默认只监听 `http://127.0.0.1:5190`，每 5 秒一轮。

| 接口 | 用途 |
| --- | --- |
| `GET /health/ready` | 第一轮读完就绪（读成功或失败都算） |
| `GET /api/v1/snapshot` | 最近一轮：轮次（启动以来第几次读，失败的也算）、读取时刻、成败、失败原因、全部行。失败的一轮 `rows` 是 `null`，不是空数组，免得"没读到"被当成"MES 里没有需求" |
| `GET /fake-mes/` | 假 MES 现在的行，以及还剩几次失败、几次格式错误没用掉 |
| `POST /fake-mes/rows` | 加一行，正文就是快照里一行的样子，例如 `{"taskType":"SAMPLE_TO_OVEN","sublot":"SMP0500.01","area":"S1-5",...}`；不检查重复，MES 本来就可能给出重复行 |
| `DELETE /fake-mes/rows?taskType=…&sublot=…` | 删掉这个业务键（`TASK_TYPE` 加 `SUBLOT`，大小写和空格都算数）的所有行 |
| `POST /fake-mes/failures` | `{"count":5,"message":"ORA-03113 …"}`：接下来 5 次读取失败，失败原因是这段文字 |
| `POST /fake-mes/malformed` | `{"count":1}`：接下来 1 次读取多返回一行缺了 `PACKAGE` 的值，整轮按"格式不对"算失败 |
| `POST /fake-mes/reset` | 回到种子里的行，清掉没用完的失败和格式错误 |

改动从下一轮起生效。网关只检查结果的结构（恰好这 7 列、每行 7 个值、值是文字、日期或空），不检查值本身对不对（例如区域号的写法），那是服务端以后的业务规则。

**读失败时**：网关一直重试，不放弃。间隔从 1 秒起逐步拉长，带随机抖动，最长 30 秒（ADR 0008），用的是 Polly，不是手写的循环。每次重试都是新的一轮，所以重试期间快照一直显示"失败"和失败原因。

**服务端这一边**：服务端默认每 2 秒取一次快照（`Simple:MesGateway:Url`、`Simple:MesGateway:PollInterval`）。网关连不上时同样按逐步拉长、最长 30 秒的间隔重试。状态接口的 `mesGateway` 一栏写网关能不能连上、最近一轮（轮次、读取时刻、成败）、行数和全部行；网关连不上时，最近一轮照旧显示，并带着它的读取时刻。

"MES 可用"是指网关能连上、它最近一轮读成功了，而且轮次还在往前走：网关进程还在、但读取卡住时，轮次停在原地。服务端按自己的钟记下最后一次看到轮次变化的时刻，超过 `Simple:MesGateway:StaleAfter`（默认 90 秒，高于网关重试上限 30 秒和上一代查询超时 30 秒，免得慢但正常的 MES 被误判）没有新轮次，就算不可用。服务端只在这个状态变化时记事件：

- `mes.unreachable`：MES 变得不可用。`data.cause` 是 `gateway`（网关停了或连不上，即术语表里的"MES 网关故障"）或 `read`（网关在，但读不到 MES、读到的格式不对或轮次停住，即"MES 故障"）。不可用期间原因变了，再记一条。
- `mes.recovered`：又可用了，带上第一轮好数据的轮次、行数和从何时起不可用。

服务端刚启动、第一次就取到好数据时不记事件；第一次就不可用（网关没起来或第一轮就失败）要记一条 `mes.unreachable`。网关刚重启、还没读完第一轮时，服务端当作"没有新消息"，不改变状态。

和服务端一样，网关的接口没有鉴权，监听地址不是回环地址时拒绝启动（退出码 2，并说明是哪一项）；真实端点只能从仓库外读入（`--Simple:SettingsFile=<路径>` 或环境变量）。`Simple.Server.Tests` 的守卫测试会检查网关的 `appsettings*.json` 里有没有非回环地址。

## 假 RIoT 与 RIoT 契约测试

RIoT 是首个厂区的车队调度系统，SIMPLE 只通过它让车移动。它的行为有不少反直觉的地方，例如按 upperId 查一张不存在的单，真 RIoT 回 HTTP 200、业务码 `0`、不带 `result`，而不是 404。上一代的假 RIoT 在这里回 404，"RIoT 说没这张单"的处理逻辑因此在测试里全绿、到现场从来不触发（上一代服务端的 `cs#404`、`cs#432`）。所以这次反过来做：在真 RIoT 上观测到的行为先写成契约测试（`tests/Simple.Contracts.Tests`），假 RIoT（`src/Simple.FakeRiot`）必须通过同一批测试，别的测试才能拿它当 RIoT 用。

### 假 RIoT

一个只在回环地址上监听的小服务（默认 `http://127.0.0.1:5200`，监听地址不是回环时拒绝启动），按 riot-sdk 0.3.1 实际发出的请求和读取方式回答：登录、建单（含带充电动作的）、按 upperId 或 orderId 查单、订单列表、取消、暂停、从暂停继续、从挂起继续、优先执行、急停与解除、上下线、路线代价与就近站点、车辆状态、地图、站点和边表。`./simple up` 把它作为第一个部件起来，服务端默认连它。

- **种子**：`src/Simple.FakeRiot/seed.json`，两张虚构的小地图和三台假车（`fake-vehicle-1` 在 1 号图 1 号站）。1 号图的路网是有向的，6 号站没有路通进去，5 号站是充电桩；两张图都有 1 号站，演示站号跨图不唯一。没有用 D8 的真实地图和车。
- **会自己动**：订单排队 → 执行 → 车到站 → 约 1 秒后成功；行驶中车的站号读 0；执行中 `controlState` 一直是 `CONTROL_STATE_ERR`；急停约 1 秒锁上、解除后 4～5 秒才回 `OK`，读到 `OK` 后故障码还残留约 3.5 秒；暂停期间车被急停过的，解除后再继续，订单当即回到执行中、读 `MT_RUNNING`，车约 16 秒后才起步；在桩上充电时电量每 36 秒涨 1%，涨到 98% 停（`FakeRiot:ChargingStopsAtBattery`），之后仍报 `CHARGING`；电量 ≤10%（`FakeRiot:NoOrdersAtOrBelowBattery`）的车不派单。默认时长接近真 RIoT，都在 `FakeRiot:Timing` 下，契约测试用几十到几百毫秒。
- **时间压缩**：`FakeRiot:Timing:Speedup` 是倍速，上面所有时长连同行驶时间都除以它。默认 1 是真实速度；设成 100，一张从 1 号站到 3 号站（20 米）的单从建单到成功不到 1 秒，不用真等十几秒的起步延迟。`Tick`（多久推进一次）和接口上的时间戳不压缩。
- **负载看得见**：`FakeRiot:ReadDelay` 让每个读请求（`/api` 下的 GET，和 `getRouteCostsBy`、`queryNearEnd`、`queryNearestStart` 三个只读的路线查询）先等这么久再回答，按假 RIoT 的时钟算。真 RIoT 读请求的 p95 约 168 ms，上一代现场约每秒 10 次读，轮询过密会拖慢全厂（`research/riot-review.md` R39）。默认 0；运行中可以用控制面改。每个 `/api` 调用都按"方法 + 路由模板"计数，没鉴权被拒的也算，用 `GET /fake-riot/calls` 读。
- **没观测过的就不编**：真 RIoT 在某种情况下怎么回答没有记录时（例如对一张已成功的单发取消），假 RIoT 回业务码 `FAKE-UNOBSERVED`，说明这是知识的空白，任何代码都不能依赖它。只有产品负责人的说法、还没实测的，假 RIoT 照说法做，对应的契约标"未实测"（见下面的 #11 对照表）。
- **控制面**（只有假 RIoT 有，代替车旁的人、网页前的人和别的产线）：

  | 接口 | 作用 |
  | --- | --- |
  | `POST /fake-riot/orders/{orderId}/hang?resultCode=` | 把执行中或暂停的单打成挂起（9），就像有人在车载屏上取消 |
  | `POST /fake-riot/orders/{orderId}/fail` | 让排队中的单变成失败（4）。假 RIoT 自己从不让单失败；按产品负责人的说法，真 RIoT 也只会让刚创建、还没排队的单失败（ADR 0010）。假 RIoT 没有"还没排队"这一态，用排队中的单代替，执行中的单回 404 |
  | `POST /fake-riot/orders/web` | 正文 `{"vehicleKey", "mapId", "station", "orderName"}`：像有人在 RIoT 网页上手动派单，`upperId` 是空串（Q-043、`cs#617`），回 `{"orderId"}` |
  | `POST /fake-riot/faults/create-00002` | 正文 `{"orderMade": true, "times": 1}`：接下来几次建单回 `00002`；`orderMade` 为 `true` 时单其实建成了，为 `false` 时没建。真 RIoT 的 `00002` 两种都可能，要按"结果未知"对待（R6） |
  | `PUT /fake-riot/order-list/churn` | 正文 `{"ordersPerRead": 1}`：每读一次订单列表之前，别的产线先建这么多单，翻页途中总数会变（`cs#573`）；0 停止 |
  | `PUT /fake-riot/vehicles/{deviceKey}` | 正文里给哪项改哪项：`battery`（0～100）、`brakeReleased`（松抱闸：执行中的单挂起，`breakSwitchState` 读 `UNMOVABLE`；抱闸还松着时从挂起继续回 `14013` 车辆状态异常）、`mode`（`MODE_AUTO` 或 `MODE_MANUAL`）、`manualCharger`（插手动充电器：`batteryState` 读 `CHARGING`）、`emergencyButtonPressed`（按下车上的急停按钮：`CAN_NOT_RECOVER`、执行中的单保持执行中、车停住，在 RIoT 里解除无效；松开后变 `CAN_RECOVER`，要在 RIoT 里解除）。松抱闸、不是 `MODE_AUTO`、插着手动充电器时，车不接新单、手上的单也不走 |
  | `POST /fake-riot/vehicles/{deviceKey}/drive` | 正文 `{"station": 3}`：人不经订单把车开过去，像用手控器（先把 `mode` 切成 `MODE_MANUAL`）或在 Matrix 上操作（`mode` 仍是 `MODE_AUTO`）；车在动、读 `MT_RUNNING`、`procState` 是 `IDLE`，RIoT 里没有订单；这时建的单排着，车到站后才执行（第 38 轮）。车有订单（包括挂起的）时不让开 |
  | `PUT /fake-riot/maps/{mapId}/chargers/{stationId}` | 正文 `{"connected": false}`：拔掉充电器，之后去这个桩的充电单挂起、结果码 `407802` |
  | `POST /fake-riot/maps/{mapId}/stations`、`DELETE /fake-riot/maps/{mapId}/stations/{stationId}` | 像在 RIoT 的地图编辑里新建、删除站点。新站点的站号是这张图有过的最大站号加一，删掉的站号不再分配（ADR 0015）；车站着、出发或要去的站点删不掉（409） |
  | `GET /fake-riot/calls`、`DELETE /fake-riot/calls` | 读、清零调用计数 |
  | `PUT /fake-riot/read-delay` | 正文 `{"milliseconds": 168}`：运行中改读延迟 |

- **已知和真 RIoT 不一样的地方**，后续票用到哪一条再补，补之前先加契约：
  - 不自动给没指定车的单派车（BC-ORDER-008）：SDK 也不许不指定车，SIMPLE 不会发这种单；
  - 充电单不展开进出点：进出点是现场地图的配置，种子里没有；充电失败直接挂起，不先重试两三轮：第 24 轮只看到"重试了几次"，次数和间隔没记下来；
  - 充电中的车照样派单：产品负责人说充电中不接单，可同一次现场读数里车恰恰是在充电中被派了单（BC-ORDER-023、Q-044），矛盾没解释之前不照哪一边改；
  - upperId 超过 52 个字符一律回 `00002`：真实上限在 52 到 68 之间，没测出来（BC-ORDER-022），假 RIoT 取最严的一头；
  - 从不出现状态 8：它的含义没查明（U1），实验室一次都没见过，假 RIoT 编不出它会怎样出现；
  - 列表里网页手派单的 `upperId` 是什么形状没观测过（Q-043 第 2 问），假 RIoT 和按 orderId 查一样给空串；
  - 从挂起继续后有没有起步延迟没观测过，假 RIoT 不加；
  - 充电速度没量过（假 RIoT 用每 1% 36 秒）；行驶不耗电；
  - 没被急停过的暂停，继续后不加起步延迟：约 16 秒只在"暂停 + 急停 + 解除"之后见过（BC-ORDER-020 第 3 条），没有不急停的对照；第 10 轮暂停后继续，约 9 秒已经到站，不支持每次都有 16 秒；
  - 暂停中松抱闸会怎样没观测过（第 27 轮 F 节只看了执行中的单），假 RIoT 让暂停的单保持暂停；
  - 挂起期间人又在车上让车走、这时发从挂起继续，真 RIoT 回成功、订单仍挂起（BC-ORDER-018 第 2 条），假 RIoT 没做：有订单（包括挂起的）的车不让人开；
  - 删掉的站点会不会出现在 `removedStation` 列表里没观测过，假 RIoT 的这个列表一直是空的；
  - 被优先执行的单如果刚建、还不满排队时长，真 RIoT 是等它还是先跑后面较早的单，没观测过；假 RIoT 等它（队首不让人插队）；
  - 执行中一直读 `CONTROL_STATE_ERR`（BC-VEH-005 第 8 条）只在生产 RIoT 上见过（第 44 轮，共 2 单）；第 38 轮在测试环境（地图 30），订单执行期间 36 次读数全是 `CONTROL_STATE_OK`，其中 8 次车在动。这一条可能只在生产 RIoT 上成立，假 RIoT 照生产的样子做。

### 契约测试

每条契约是一个经 riot-sdk 的 Facade 调 RIoT 的测试，用 `[Contract("BC-ORDER-004")]` 写明出处：行为实验室的契约编号、实验轮次（`round 24`）或问题编号（`Q-033`），或上一代的现场记录（`cs#404`）。出处会变成测试的 trait，可以按它挑：

```
dotnet test --project tests/Simple.Contracts.Tests --filter-trait "Contract=BC-ORDER-004"
```

产品负责人说过、但还没在真 RIoT 上测过的行为（例如"对挂起的单发取消，单子变成已取消"，ADR 0010），另外标 `[NotVerifiedOnRealRiot("…")]`（trait `RealRiot=NotVerified`）。假 RIoT 照这些说法做，好让建在上面的代码能测；它们是否成立另由 #11 在真 RIoT 上核对，测出来不成立要回头改 ADR 和假 RIoT。

#11 里的每一条都有契约，出处里写 `#11` 和依赖它的 ADR，`--filter-trait "Contract=#11"` 一次挑出来；`ContractCatalogueTests` 检查每条契约都写了出处、每条 `#11` 的契约都写了 ADR。对照如下（"未实测"即标了 `[NotVerifiedOnRealRiot]`）：

| #11 的条目 | 契约 | ADR | 实测情况 |
| --- | --- | --- | --- |
| 站号永不改变，新站点只递增、删掉的不再分配；站号跨图不唯一 | `StationContracts.ANewStationGetsANumberNoStationHadBefore`、`MapContracts.TwoMapsHaveAStationWithTheSameId` | 0015 | 前者未实测；后者实验室见过（BC-MAP-002） |
| 订单只在刚创建、还没排队时失败，车在原地 | `FailedOrderContracts.AJustMadeOrderFailsWithTheVehicleWhereItIs`（假 RIoT 拒绝让执行中的单失败，由 `Simple.FakeRiot.Tests` 测） | 0010 | 未实测 |
| 按急停按钮、服务端下的急停会不会让订单挂起 | `EmergencyButtonContracts.PressingTheButtonDoesNotHangTheExecutingOrder`、`EmergencyStopContracts.AnEmergencyStopKeepsTheExecutingOrderExecuting` | 0010 | 都不会，实验室见过：按住按钮，第 28 轮约 14 分钟（520 个采样）、第 27 轮 A 节短监视，订单一直执行中（Q-035），服务端下的急停见 BC-ORDER-015。ADR 0010 原先写"按急停按钮会进挂起"，产品负责人 2026-10-18 按实测改正 |
| 对挂起的单发取消，变成已取消 | `OrderCommandContracts.CancellingAHungOrderCancelsIt` | 0010 | 未实测 |
| 一类急停软件解不开；一类触发同时触发二类，一类消除后二类还在；按钮按着时在 RIoT 里解除会立刻再触发 | `EmergencyButtonContracts.WhileTheButtonIsPressedAReleaseInRiotDoesNotReleaseTheStop`、`OnceTheButtonIsLetGoTheStopStaysUntilReleasedInRiot` | 0009 | 部分实测：第 27 轮 A 节见过硬件急停要物理松开后 `cancelEmergency` 才回 `OK`；"立刻再触发""一类带出二类"是产品负责人的说法 |
| 三类急停解除后车接着走原来的单 | `EmergencyStopContracts.AfterTheStopIsReleasedTheVehicleGoesOnWithItsOrder` | 0009 | 未实测（BC-ORDER-020 第 5 条只是推断） |
| 下线后不开始排队的单，已开始的照常做完，暂停、挂起的仍能恢复 | `IntegrationLevelContracts` 三条 | 0011 | 未实测（第 13 轮只看到开头） |
| `MODE_MANUAL` 时人能直接开车 | `ManualUseContracts.InManualModeAPersonDrivesTheVehicleWithoutAnOrder` | 0022 | 未实测 |
| 取消行驶中的单车会猛停 | `StationContracts.CancellingAMovingOrderStopsTheVehicleWhereItIs` | 0021 | 未实测；假 RIoT 只表现"停在原地"，猛不猛表现不出来 |
| 插着手动充电器车动不了 | `ManualUseContracts.WithTheManualChargerPluggedInTheVehicleReadsChargingAndDoesNotMove` | 0022 | 未实测；读 `CHARGING` 第 42 轮见过 |
| 判断站点上或开往站点的车只能遍历车辆和订单 | `StationContracts.EveryVehicleOnTheListHasACardThatSaysWhereItStands`（只读，真 RIoT 上也跑）、`TheOrderListShowsWhereAnExecutingVehicleIsHeading` | 0020 | 未实测 |
| 电池可能 97%～99% 停充、报不出 100%；充满后还报不报 `CHARGING` | `ChargingContracts.ChargingStopsShortOfAHundredPercentAndTheVehicleStillReadsCharging` | 0020 | 未实测（推测）；假 RIoT 取仍报 `CHARGING` |
| 在 Matrix 上让车走时 `mode` 仍是 `MODE_AUTO`、车在动没订单 | `ManualUseContracts.DrivenFromMatrixTheVehicleMovesWithoutAnOrderInAutoMode`、`AnOrderMadeWhileAPersonDrivesTheVehicleWaitsUntilItArrives` | 0022 | `mode` 未实测（推测，1 个样本）；`MT_RUNNING`、`procState` `IDLE`、途中建的单到站后才执行，第 38 轮单机去站点见过（把实验室的"单机"当成 ADR 里的 Matrix，是推断） |
| 充电单 = move + `act(78,1,0)`，开始充电即成功；下一单队首自动插 `act(78,2,0)`；充不上电 `407802` 挂起 | `ChargingContracts` 前三条 | 0020 | 实验室见过（第 24、25 轮） |
| 松抱闸：`mode` 仍 `MODE_AUTO`、`breakSwitchState` 读 `UNMOVABLE`；松着时新建的单一直排队 | `ManualUseContracts.AReleasedBrakeReadsUnmovableAndTheModeStaysAuto`、`AnOrderMadeWhileTheBrakeIsReleasedStaysQueued`、`ReleasingTheBrakeHangsTheExecutingOrder`、`ContinuingWhileTheBrakeIsStillReleasedIsRefusedWith14013` | 0022 | 实验室见过（第 21、27-F、29-F、30 轮，BC-ORDER-015） |
| 在桩上充电、插手动充电器时都报 `CHARGING` | `ChargingContracts.AChargeOrderSucceedsOnceTheVehicleCharges`、`ManualUseContracts.WithTheManualChargerPluggedInTheVehicleReadsChargingAndDoesNotMove` | 0020、0022 | 实验室见过（第 24、25、42 轮） |
| 手动用车（松抱闸、`MODE_MANUAL`、手动充电器）时取消排队、暂停、挂起的单能不能生效 | `ManualUseContracts.AQueuedOrderCanBeCancelledDuringManualUse`、`AHeldOrderCanBeCancelledDuringManualUse`、`AHungOrderCanBeCancelledDuringManualUse`，各三种手动用车 | 0022 | 未实测；假 RIoT 取都能生效 |
| 恢复以后排队、暂停、挂起的单会不会自己开始 | `ManualUseContracts.AQueuedOrderStartsByItselfOnceManualUseEnds`、`AHeldOrderStaysHeldOnceManualUseEnds`、`AHungOrderDoesNotGoOnByItselfOnceManualUseEnds`，各三种手动用车 | 0022 | 未实测；假 RIoT 取排队的自己开始、暂停和挂起的要发继续（合上抱闸后从挂起继续回 0，第 27 轮 F 见过） |
| 车离开桩时读哪个字段 | `ChargingContracts.TheChargerIsFreeOnceTheVehicleHasLeftIt` | 0020 | 未实测；假 RIoT 一离站 `currentStation` 就读 0 |
| C2000 脉冲开锁、关机断电、开锁输出常 1 时能否锁上（#16 第六场） | 不是 RIoT 的行为，不在这里 | 0024、0012 | 归车上 IO 模块与 IO 模拟器，等台架或真车窗口核对 |

**默认、CI 上永远只对假 RIoT 跑**：每条测试在测试进程里起一个新的假 RIoT，不开端口。

**对真 RIoT 跑**要人明确选定，并且只跑只读的契约（ADR 0047）：

1. 在任何 git 克隆之外写一个 JSON 文件，例如：

   ```json
   {
     "url": "http://<RIoT 地址>:<端口>",
     "callApiKey": "<RIoT 网页上的调用密钥>",
     "vehicleKey": "BROKERX-…",
     "mapId": 26,
     "vehicleStation": 151,
     "reachableStation": 155,
     "secondReachableStation": null,
     "unreachableStation": null,
     "chargingStation": null,
     "mapsSharingAStationId": [25, 26],
     "sharedStationId": 1,
     "username": null,
     "password": null,
     "runUntil": "2026-10-18T18:00:00+08:00"
   }
   ```

   `runUntil` 是这一次运行到几点为止：过了这个时刻，或者写得比现在晚 12 小时以上，都拒绝。这样环境变量留在终端里，之后的运行（包括同一个终端里的 AI 会话）也不会悄悄连到真 RIoT。

2. 设环境变量 `SIMPLE_CONTRACTS_RIOT_SETTINGS` 指向它，再跑测试。测试在第一次调用前把要调的地址打到标准错误上。

拒绝的情形：`CI=true`；文件在某个 git 克隆里面（不只是本仓库，任何上层目录有 `.git` 都算，经符号链接或目录联接指进去的也算）；`runUntil` 过期或太远；缺 `url`、`callApiKey`、`vehicleKey`、`mapId`、`vehicleStation`、`reachableStation`、`runUntil`。会建单、发订单命令、急停或上下线的契约在真 RIoT 上一律跳过，理由写在跳过信息里：这些操作要产品负责人逐次授权、现场有人。需要控制面的契约在真 RIoT 上也跳过，故意用错口令登录的那一条也跳过（可能锁账号）。跳过不算通过。

跳过只是第一道。对真 RIoT 发出的每个请求还要经过一层白名单（`tests/Simple.Contracts.Tests/Targets/ReadOnlyGuard.cs`）：只放行契约用到的读接口，逐个列名，即读地图、站点、边表、车辆信息、订单详情和订单列表的 GET，以及登录和三个路线查询这四个只读的 POST。不是"GET 都放行"：RIoT 有会动作的 GET，例如发动作指令的 `execActionCommand`、清车并取消订单的 `clearVehicleAndCancelOrderTask`。不在名单上的请求，包括同步发送的，都在发出前被拦下，测试报错。所以哪条契约忘了写跳过，也只会失败，不会让车动；要读一个新接口，先把它加进名单。

**加一条契约**：先找到出处，即行为实验室（`8005-agv-program` 仓库的 `rcs/riot-behavior-lab/`，`knowledge/behavioral-contracts.md` 里的 `BC-…` 编号、`hypotheses/open-questions.md` 里的 `Q-…` 编号、`evidence/` 下的轮次）或上一代的现场记录（`cs#…`）；写成测试，写明出处，只是有人这样说、没测过的另标 `[NotVerifiedOnRealRiot]`；对假 RIoT 跑，红了就改假 RIoT，直到绿。只读的契约在真 RIoT 上也能跑，写操作的开头调 `Target.RequireWrites()`。
