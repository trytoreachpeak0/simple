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

需要 .NET 10 SDK（版本见 `global.json`）。

```
dotnet build simple.slnx
dotnet test --solution simple.slnx
```

测试跑在 Microsoft Testing Platform 上，筛选测试用 `--filter-class`、`--filter-method`、`--filter-trait` 等选项，不是 VSTest 的 `--filter`。
