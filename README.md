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
| `tests/` | 每个项目一个 `*.Tests` 测试项目，另有调度平台的行为契约测试 |
| `scenarios/` | 统一格式的测试场景 |
| `samples/replay/` | 从真实系统录下的回放样本 |
| `sites/<厂区>/` | 各厂区的起始业务配置 |

以后计划加两个入口：`Simple.Cli`，用一条命令起停整套系统、跑场景；`Simple.Mcp`，给 AI 用的一层薄包装，只调用命令行和服务端 API，不放业务逻辑。

## 构建与测试

需要 .NET 10 SDK（版本见 `global.json`）和 SQL Server 2022 LocalDB（实例 `MSSQLLocalDB`）。

```
dotnet build simple.slnx
dotnet test --solution simple.slnx
```

测试跑在 Microsoft Testing Platform 上，筛选测试用 `--filter-class`、`--filter-method`、`--filter-trait` 等选项，不是 VSTest 的 `--filter`。

需要数据库的测试每次建一个随机名字的库（`Simple_test_<随机串>`），测完删掉，几个 worktree 同时跑也互不干扰。默认建在本机 LocalDB 上；要用别的 SQL Server 实例，把不带库名的连接串放进环境变量 `SIMPLE_TEST_SQLSERVER`，例如 `Server=.\SQLEXPRESS;Integrated Security=true;TrustServerCertificate=true`。

## 运行服务端

```
dotnet run --project src/Simple.Server
```

默认只监听 `http://127.0.0.1:5180`，连本机 LocalDB，库名是 `Simple_<实例名>`（默认实例名 `dev`，即 `Simple_dev`）；第一次启动时自动建库并执行迁移。

| 接口 | 用途 |
| --- | --- |
| `GET /health/live` | 进程活着 |
| `GET /health/ready` | 能连上自己的数据库 |
| `GET /api/v1/status` | 版本、启动时间、实例名、库名，以及车载终端、RIoT、MES 网关三栏（目前为空） |
| `GET /api/v1/events` | 事件记录，按写入顺序；参数 `after`（游标，即上一页的 `nextCursor`）、`since`、`until`、`limit`（1～1000，默认 100） |
| `GET /openapi/v1.json` | 接口描述 |

在同一台机器上再起一套，换实例名和端口：

```
dotnet run --project src/Simple.Server -- --Simple:Instance=wt25 --urls=http://127.0.0.1:5181
```

仓库里的配置只指向回环地址和 LocalDB。真实的连接串和地址放在仓库外：用 `--Simple:SettingsFile=<路径>` 指定一个 JSON 配置文件，或用环境变量（例如 `ConnectionStrings__Simple`、`Simple__Instance`）。连接串里不写库名，库名由实例名决定。配置不合法时服务端拒绝启动，退出码为 2，并逐项说明是哪一项不对；接口还没有鉴权，所以监听地址不是回环地址时也拒绝启动。

事件记录只能追加：数据库里的触发器拒绝一切修改和删除。删掉 `Simple_<实例名>` 这个库，事件记录就清空，下次启动重新建库。

改了数据模型后，用本仓库钉住版本的 `dotnet-ef` 生成迁移：

```
dotnet tool restore
dotnet ef migrations add <迁移名> --project src/Simple.Server --output-dir Persistence/Migrations
```
