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
| `src/Simple.OnboardTerminal.Core` | 车载终端的核心，不依赖 WPF：连接服务端、收发消息、车旁操作 |
| `src/Simple.OnboardTerminal.Headless` | 无界面的车载终端，本机整套系统、测试和 CI 用它 |
| `src/Simple.MesGateway` | MES 网关，独立的小进程 |
| `src/Simple.IoSimulator` | 车上 IO 模块的模拟器 |
| `src/Simple.Protocol` | 服务端与车载终端之间的协议契约 |
| `src/Simple.Cli` | `simple` 命令行：在本机一条命令起停整套系统、查看状态、事件和日志 |
| `tests/` | 每个项目一个 `*.Tests` 测试项目，另有调度平台的行为契约测试 |
| `scenarios/` | 统一格式的测试场景 |
| `samples/replay/` | 从真实系统录下的回放样本 |
| `sites/<厂区>/` | 各厂区的起始业务配置 |

以后计划再加一个入口 `Simple.Mcp`：给 AI 用的一层薄包装，只调用命令行和服务端 API，不放业务逻辑。

## 构建与测试

需要 .NET 10 SDK（版本见 `global.json`）和 SQL Server 2022 LocalDB（实例 `MSSQLLocalDB`）。

```
dotnet build simple.slnx
dotnet test --solution simple.slnx
```

测试跑在 Microsoft Testing Platform 上，筛选测试用 `--filter-class`、`--filter-method`、`--filter-trait` 等选项，不是 VSTest 的 `--filter`。

需要数据库的测试每次建一个随机名字的库（`Simple_test_<随机串>`），测完删掉，几个 worktree 同时跑也互不干扰。默认建在本机 LocalDB 上；要用别的 SQL Server 实例，把不带库名的连接串放进环境变量 `SIMPLE_TEST_SQLSERVER`，例如 `Server=.\SQLEXPRESS;Integrated Security=true;TrustServerCertificate=true`。

## 在本机起停整套系统

仓库根的 `simple.ps1` 先编译命令行本身（安静地编译，失败才显示输出），再把参数原样交给它。在 PowerShell 7 里：

```
./simple up          # 编译一次，起齐所有部件，全部就绪才返回
./simple up --terminals 3   # 再起 3 个无界面车载终端
./simple status      # 每个进程在不在、就绪没有，以及服务端自己的状态
./simple events --since 5m
./simple logs server --tail 50
./simple down        # 正常停止本实例起的进程
./simple down --reset   # 再删掉本实例的库
```

从别的 shell 调用写成 `pwsh -File <clone>/simple.ps1 up`。每个命令都接受 `--json`：标准输出上只有一个 JSON 对象 `{exitCode, error, result}`，进度提示写在标准错误上。退出码是固定的，`./simple --help` 列出全表：0 成功；1 意外错误（或 `down` 之后仍有进程在跑）；2 参数或设置不对；3 编译失败；4 有部件没就绪（已起的会被停掉）；5 实例没在运行；6 实例不健康、只起了一半，或是从另一个 clone 起的；7 另一条 simple 命令正在操作这个实例；8 有进程超时没停下、被强杀；9 连不上 SQL Server 或删库失败。

目前系统里有服务端和无界面车载终端（`--terminals N` 个，默认 0）。每个部件在 `src/Simple.Cli/Components.cs` 里登记一项：从哪个项目编译、起几个进程、给哪些参数、怎么判就绪，以及起之前要准备什么；以后加 IO 模拟器、假 RIoT、MES 网关，都是往这里加一项。部件按登记的顺序一个一个起，前一个全部就绪才起下一个，因为后面的部件可能要用前面的：车载终端要先从服务端的状态接口读到证书指纹。

- **实例**：不写 `--instance` 时，实例名取 clone 所在文件夹的名字（例如 `29-simple-cli`），所以两个 worktree 各起一套互不干扰。库名是 `Simple_<实例名>`；端口按实例名在 20000～29999 之间选一段，被占用就往后挪，同一个实例每次拿到的端口一般不变。
- **起**：`up` 先 `dotnet build` 各部件的项目，再把编译输出复制到 `artifacts/simple/<实例名>/` 下运行，所以系统起着的时候照样可以重新编译、跑测试，不会碰到文件被占用。部件是独立的进程，命令行返回后它们继续运行；每个进程的输出写进自己的日志文件。重复执行 `up` 不会起第二套。
- **停**：`down` 只停本实例的状态文件里记下的进程（按进程号和进程启动时间核对，绝不按进程名），用 Ctrl+Break 让它们正常退出，服务端因此会在事件记录里留下 `server.stopped`；30 秒还没退出才强杀，并以退出码 8 报告。
- **库**：默认用本机 LocalDB `(localdb)\MSSQLLocalDB`。要用本机别的命名实例（CI 用 `.\SQLEXPRESS`），把不带库名的连接串放进 `SIMPLE_SQLSERVER`。命令行拒绝别的机器上的 SQL Server、本机的默认实例（`.`、`localhost`、`MSSQLSERVER`）、带端口的写法（带端口时 SqlClient 不看实例名，本机 1433 上通常是默认实例）、故障转移伙伴，以及带 SQL 登录用户名或密码的连接串（连接串会记在状态文件里，所以本机只用 Windows 集成认证）；起服务端和删库之前，还会连上去问服务器自己是谁（`SERVERPROPERTY` 的 `IsLocalDB`、`InstanceName`、`MachineName`），只接受 LocalDB，或本机上名字不是 `MSSQLSERVER` 的命名实例，这样别名和端口转发也绕不过去。`down --reset` 只删 `Simple_<实例名>` 这一个库，删在 `up` 当时用的那个 SQL Server 上。
- **只起本机的假系统**：命令行没有任何指向真实系统的开关。起部件时会去掉环境里以 `Simple__`、`ConnectionStrings__`、`ASPNETCORE_`、`Kestrel__` 开头的变量和 `DOTNET_ENVIRONMENT`，免得外面设好的真实地址、连接串、监听地址或运行环境被带进本机系统；部件总是以 Production 运行。
- **文件放在哪**：状态文件和日志在 `%LOCALAPPDATA%\simple\instances\<实例名>\`（可用环境变量 `SIMPLE_HOME` 换位置）；日志在 `down` 之后保留，下次 `up` 时覆盖。每个车载终端的本机配置在同一目录的 `processes\terminal-<n>\terminal.json`，`up` 时按服务端的状态重新写。
- **车载终端**：`terminal-1`、`terminal-2`…分别自报车辆 `vehicle-1`、`vehicle-2`…，连本实例服务端的终端端点。`./simple status` 末尾按服务端看到的样子列出每台车载终端：连着没有、上报新不新鲜、最新鲜的那份上报最多是几秒前写的、版本、屏上文字。每个终端在测试模式下开一个只听回环地址的测试输入（地址就是 `status` 里该进程的 URL），用来代替车旁的人：
  - `POST /test/keyboard`，正文 `{"text": "LOT123\r"}`：像扫码枪那样敲键盘，每个回车结束一次扫码；
  - `POST /test/press`，正文 `{"button": "<按钮名>"}`：按屏上的一个按钮；
  - `GET /test/status`：连接状态和屏上文字；`GET /test/ready`：连着时 200。
  - 终端没连上服务端时，扫码和按键都返回 409 并被丢掉，和车上真屏一样。
- **服务端钥匙**：本实例服务端的证书钥匙放在当前 Windows 用户的密钥存储里，名字是 `Simple-Server-<实例名>`。`down` 不动它，下次 `up` 指纹不变；`down --reset` 连库一起删掉它。

`Simple.Cli.Tests` 里的冒烟测试经编译好的命令行真起真停随机名字的实例：两套只有服务端的实例互不干扰；一套带 3 个车载终端的实例里，终端上报新鲜、经测试输入扫的码进了事件记录、被杀掉的终端 10 秒内记成上报过期、`down` 后不留进程。`dotnet test` 时随其他测试一起跑，CI 上也跑。

## 只运行服务端

```
dotnet run --project src/Simple.Server
```

默认接口只监听 `http://127.0.0.1:5180`，车载终端端点监听 `https://127.0.0.1:5181`，连本机 LocalDB，库名是 `Simple_<实例名>`（默认实例名 `dev`，即 `Simple_dev`）；第一次启动时自动建库并执行迁移。

| 接口 | 用途 |
| --- | --- |
| `GET /health/live` | 进程活着 |
| `GET /health/ready` | 能连上自己的数据库 |
| `GET /api/v1/status` | 版本、启动时间、实例名、库名；车载终端端点的地址和证书指纹；每台车载终端的版本、连着没有、最新鲜的上报最多是几秒前写的、是否已过期、最近下发的屏上文字；RIoT、MES 网关两栏（目前为空） |
| `GET /api/v1/events` | 事件记录，按提交顺序（顺着游标一直往后读不会漏事件）；参数 `after`（游标，即上一页的 `nextCursor`）、`since`、`until`、`limit`（1～1000，默认 100） |
| `GET /openapi/v1.json` | 接口描述 |

在同一台机器上再起一套，换实例名和端口：

```
dotnet run --project src/Simple.Server -- --Simple:Instance=wt25 --urls=http://127.0.0.1:5182 --Simple:OnboardTerminals:Url=https://127.0.0.1:5183
```

仓库里的配置只指向回环地址和 LocalDB。真实的连接串和地址放在仓库外：用 `--Simple:SettingsFile=<路径>` 指定一个 JSON 配置文件，或用环境变量（例如 `ConnectionStrings__Simple`、`Simple__Instance`）。连接串里不写库名，库名由实例名决定。配置不合法时服务端拒绝启动，退出码为 2，并逐项说明是哪一项不对；接口还没有鉴权，所以接口的监听地址（`urls`）不是回环地址、或不是 `http://` 时也拒绝启动。

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
