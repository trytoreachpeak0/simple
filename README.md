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
| `src/Simple.OnboardTerminal` | 车载终端（WPF） |
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
./simple status      # 每个进程在不在、就绪没有，以及服务端自己的状态
./simple events --since 5m
./simple logs server --tail 50
./simple down        # 正常停止本实例起的进程
./simple down --reset   # 再删掉本实例的库
```

从别的 shell 调用写成 `pwsh -File <clone>/simple.ps1 up`。每个命令都接受 `--json`：标准输出上只有一个 JSON 对象 `{exitCode, error, result}`，进度提示写在标准错误上。退出码是固定的，`./simple --help` 列出全表：0 成功；1 意外错误（或 `down` 之后仍有进程在跑）；2 参数或设置不对；3 编译失败；4 有部件没就绪（已起的会被停掉）；5 实例没在运行；6 实例不健康、只起了一半，或是从另一个 clone 起的；7 另一条 simple 命令正在操作这个实例；8 有进程超时没停下、被强杀；9 连不上 SQL Server 或删库失败。

目前系统里有两个部件：MES 网关（背后接假 MES，先起）和服务端（启动参数里带上网关的地址）。`simple status` 除了列出每个进程，还有一行写服务端最近从网关看到了什么：网关通不通、最近一轮是第几轮、几点读的、多少行。每个部件在 `src/Simple.Cli/Components.cs` 里登记一项：从哪个项目编译、起几个进程、给哪些参数、怎么判就绪；以后加车载终端、IO 模拟器、假 RIoT，都是往这里加一项。

- **实例**：不写 `--instance` 时，实例名取 clone 所在文件夹的名字（例如 `29-simple-cli`），所以两个 worktree 各起一套互不干扰。库名是 `Simple_<实例名>`；端口按实例名在 20000～29999 之间选一段，被占用就往后挪，同一个实例每次拿到的端口一般不变。
- **起**：`up` 先 `dotnet build` 各部件的项目，再把编译输出复制到 `artifacts/simple/<实例名>/` 下运行，所以系统起着的时候照样可以重新编译、跑测试，不会碰到文件被占用。部件是独立的进程，命令行返回后它们继续运行；每个进程的输出写进自己的日志文件。重复执行 `up` 不会起第二套。
- **停**：`down` 只停本实例的状态文件里记下的进程（按进程号和进程启动时间核对，绝不按进程名），用 Ctrl+Break 让它们正常退出，服务端因此会在事件记录里留下 `server.stopped`；30 秒还没退出才强杀，并以退出码 8 报告。
- **库**：默认用本机 LocalDB `(localdb)\MSSQLLocalDB`。要用本机别的命名实例（CI 用 `.\SQLEXPRESS`），把不带库名的连接串放进 `SIMPLE_SQLSERVER`。命令行拒绝别的机器上的 SQL Server、本机的默认实例（`.`、`localhost`、`MSSQLSERVER`）、带端口的写法（带端口时 SqlClient 不看实例名，本机 1433 上通常是默认实例）、故障转移伙伴，以及带 SQL 登录用户名或密码的连接串（连接串会记在状态文件里，所以本机只用 Windows 集成认证）；起服务端和删库之前，还会连上去问服务器自己是谁（`SERVERPROPERTY` 的 `IsLocalDB`、`InstanceName`、`MachineName`），只接受 LocalDB，或本机上名字不是 `MSSQLSERVER` 的命名实例，这样别名和端口转发也绕不过去。`down --reset` 只删 `Simple_<实例名>` 这一个库，删在 `up` 当时用的那个 SQL Server 上。
- **只起本机的假系统**：命令行没有任何指向真实系统的开关。起部件时会去掉环境里以 `Simple__`、`ConnectionStrings__`、`ASPNETCORE_` 开头的变量和 `DOTNET_ENVIRONMENT`，免得外面设好的真实地址、连接串或运行环境被带进本机系统；部件总是以 Production 运行。
- **文件放在哪**：状态文件和日志在 `%LOCALAPPDATA%\simple\instances\<实例名>\`（可用环境变量 `SIMPLE_HOME` 换位置）；日志在 `down` 之后保留，下次 `up` 时覆盖。

`Simple.Cli.Tests` 里的冒烟测试经编译好的命令行真起真停两套随机名字的实例，`dotnet test` 时随其他测试一起跑，CI 上也跑。

## 把重活送到远端机器上跑

改代码留在本机，完整编译、全量测试、起整套系统这些重活可以送到另一台 Windows 机器上跑。远端是哪台机器只从环境变量 `SIMPLE_REMOTE` 读，它的值是本机 SSH 配置（`~/.ssh/config`）里的一个主机别名，仓库里不写任何机器名或地址。

```
./simple remote build
./simple remote test --filter-class "*CliTests"     # 后面的参数原样交给 dotnet test（MTP 选项）
./simple remote up                                  # 远端起整套系统，返回后系统继续运行
./simple remote status                              # 还有 down、events、logs，转给远端那份 simple
./simple remote exec -- dotnet --info               # 在远端的工作树里跑任意命令
./simple remote forget                              # 停掉、删库、删远端目录；删 worktree 之前用
```

- **跑的恰好是本机这棵树**：每次先把工作树做成一个快照提交，包括没提交的改动和还没 `git add` 的新文件，按 `.gitignore` 排除被忽略的文件；用的是一份临时索引，不动真正的索引和工作区。作者、时间都是固定值，所以同一棵树每次得到同一个提交号；工作树干净时就用 `HEAD` 本身。远端检出这个确切的提交再跑，开头和结尾各打印一行：提交号、基于哪个 `HEAD`、几个未提交的文件、哪台机器。
- **怎么送**：只把 `origin/main` 之后的部分（本地没推送的提交加快照）打成 git bundle，经 scp 送到远端的裸仓库缓存；远端缺前置对象时从 origin 拉一次，还不行就送完整历史。远端已经有这个提交就什么都不送。不往 GitHub 推任何分支。快照里新增了超过 10 MiB 的文件时列出来并拒绝发送。
- **远端的目录**：`C:\simple-remote\` 下有 `repo.git`（所有实例共用的对象缓存）、`wt\<实例名>`（每个 worktree 一个检出目录）、`runs\<实例名>`（每次运行的结果，保留最近 20 次）、`locks`、`slots`。实例名与本机相同，所以远端的 `simple up` 也按本机那套实例规则分开端口和库。检出时清掉残留文件，但保留被忽略的 `bin/`、`obj/`、`artifacts/`，编译是增量的。
- **排队**：同一个实例同一时刻只跑一条远程命令，后来的等着，并打印在等谁；`build`、`test`、`exec`、`up` 还要占用整机 4 个名额中的一个。等 30 分钟还轮不到就以退出码 22 结束。
- **结果**：输出实时回传；退出码是远端命令自己的（例如测试的 2、8 原样返回）。`simple remote` 自己的失败用 20（没跑成：git、ssh、scp 出错，或远端在起命令之前失败）、21（快照含大文件，没送）、22（等不到实例或名额）；`--json` 时标准输出是一个 `{exitCode, error, result}`，`result` 里有提交号、`HEAD`、未提交文件数和远端退出码。`build`、`test`、`exec` 的输出和测试结果（TRX）拷回本机 `artifacts/remote/<实例名>/<时刻>-<提交号前 8 位>/`；`exec` 的命令把文件写进 `%SIMPLE_REMOTE_RUN%` 也会拷回来。
- **远端也只起本机的假系统**：远端的命令把 `SIMPLE_SQLSERVER` 和 `SIMPLE_TEST_SQLSERVER` 指向那台机器上专给它用的命名实例 `.\SIMPLE_DEV`，`simple` 在那台机器上照样只接受本机的 LocalDB 或命名实例；`simple remote` 没有任何指向真实系统的开关。
- **断线**：SSH 一断，远端会结束这次运行的所有进程，不会留下没人管的测试进程。`up` 起的部件是例外：它们脱离 SSH 会话继续运行，用 `simple remote down` 停。

为什么手写这一层：现成的同步工具（Mutagen、Syncthing 等）都是持续同步型的，说不出某一次运行看到的是哪个状态的树，"同步到一半就开跑"正是要避免的；`simple remote` 只是 git、ssh、scp 之上的一层薄胶水，"这次跑的是哪棵树"由提交号保证。

## 只运行服务端

```
dotnet run --project src/Simple.Server
```

默认只监听 `http://127.0.0.1:5180`，连本机 LocalDB，库名是 `Simple_<实例名>`（默认实例名 `dev`，即 `Simple_dev`）；第一次启动时自动建库并执行迁移。

| 接口 | 用途 |
| --- | --- |
| `GET /health/live` | 进程活着 |
| `GET /health/ready` | 能连上自己的数据库 |
| `GET /api/v1/status` | 版本、启动时间、实例名、库名，以及车载终端、RIoT、MES 网关三栏（车载终端和 RIoT 目前为空；MES 网关一栏见下一节） |
| `GET /api/v1/events` | 事件记录，按提交顺序（顺着游标一直往后读不会漏事件）；参数 `after`（游标，即上一页的 `nextCursor`）、`since`、`until`、`limit`（1～1000，默认 100） |
| `GET /openapi/v1.json` | 接口描述 |

在同一台机器上再起一套，换实例名和端口：

```
dotnet run --project src/Simple.Server -- --Simple:Instance=wt25 --urls=http://127.0.0.1:5181
```

仓库里的配置只指向回环地址和 LocalDB。真实的连接串和地址放在仓库外：用 `--Simple:SettingsFile=<路径>` 指定一个 JSON 配置文件，或用环境变量（例如 `ConnectionStrings__Simple`、`Simple__Instance`）。连接串里不写库名，库名由实例名决定。配置不合法时服务端拒绝启动，退出码为 2，并逐项说明是哪一项不对；接口还没有鉴权，所以监听地址不是回环地址时也拒绝启动。

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
