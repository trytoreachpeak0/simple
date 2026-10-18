# 厂区的起始配置

每个厂区一个目录 `sites/<厂区>/`，里面的 `site.yaml` 是这个厂区的起始参数配置（参数配置就是厂区配置，见 `docs/adr/0017-site-config-lives-in-server.md` 和 `docs/rules-overview.md` 第 13 节）。它只在首次安装时导进服务端一次，之后以服务端里的配置为准；测试和场景也用它。

| 目录 | 是什么 |
| --- | --- |
| `sample/` | 虚构的小厂区，场景和测试默认用它，和假 MES、假 RIoT 的起始数据对得上 |
| `d8/` | 首个厂区 D8，照 v2 线的生产配置整理，没有的值标"待定" |

这里只放业务配置，不放 SIMPLE 要连接的端点，也不放口令、密码、密钥、令牌。车辆启用、仓位停用、充电桩停用是人在控制台按的开关，不是配置，也不在这里（ADR 0011、0012、0020）。

## 怎么导入

导入是服务端的一条命令，用服务端自己的设置（实例名、数据库、车队调度系统的地址和调用密钥都从仓库外来）：

```
Simple.Server import-site sites/d8/site.yaml
```

它做这几件事：

1. 读文件，格式不对就逐条说出哪里不对（文件位置或"哪一项"），什么都不导入。
2. 服务端里已经有配置时什么都不导入：同一个厂区的，退出码 0；别的厂区的，退出码 2。
3. 从车队调度系统读全部地图和站点（只读），读不到时什么都不导入，退出码 3。
4. 对整份配置做第 13 节的校验（下面"校验什么"），有一条不通过就什么都不导入，逐条列出，退出码 2。
5. 存成第 1 版，**每个项目的每个任务类型一律关着**（ADR 0017），记一条事件 `site-configuration.imported`，退出码 0。

`simple up` 在每次起服务端之前都跑这条命令，默认导入 `sites/sample/`，`--site d8` 之类可以换；场景导入自己 `site:` 写的厂区。库里已经有配置时它什么都不做，所以可以反复跑。

导入以后，当前生效的整份配置经 `GET /api/v1/site-configuration` 读（所有登录的账号都能看），状态接口 `GET /api/v1/status` 的 `siteConfiguration` 写着当前是第几版、哪个厂区。改配置（对比、提交、退回）是以后的票。

## 文件格式

YAML，键名用连字符。没写的可选项取括号里的默认值；参数没写的取汇总表二的默认值。不认识的键算错，免得拼错的键被悄悄当成默认值。

```yaml
name: d8                         # 厂区，必填
description: ……                  # 可选

projects:                        # 项目（ADR 0015）
  - code: D8                     # 编号，账号和车辆一览页的地址用它
    name: D8                     # 中文名，可选
    zones:                       # 这个项目的派车区（ADR 0015、0023、0036、0038）
      - code: WIRE_BONDING
        name: 焊线区             # 中文名，必填（车载屏上显示）
        demand-order: nearest-first   # 需求先后策略，可选：first-come-first-served、
                                      # nearest-first（默认）、nearest-first-with-wait-cap、shortest-overall
        wait-cap: 15m            # 等待封顶的 T，可选，默认 15m；只有 nearest-first-with-wait-cap 用到
    fixed-stations:              # 这个项目的固定站点（ADR 0015、0035）
      - { task-type: STAGING_TO_WIRE, map: 26, station: 217, all-loaded: 10m }
                                 # all-loaded：车在这里装货时"全部装完"等多久，可选，不写用全厂区的值

task-types:                      # 任务类型（ADR 0006、0038）
  - { code: STAGING_TO_WIRE, name: 待送区送焊线, pickup: fixed-station, unload: machine }
                                 # pickup、unload：machine（机台）或 fixed-station（固定站点）

areas:                           # 区域号（ADR 0002、0006、0015）
  - { area: N1-1, zone: WIRE_BONDING, door-side: front }
  - { area: T8-8, zone: DIE_ATTACH, door-side: front, not-served: true }
                                 # zone、door-side 可以不写，这个区域号就不派车、运行时告警；
                                 # door-side 是车辆清单里仓位分组的名字；not-served 默认 false

vehicles:                        # 车辆清单（ADR 0006、0007、0015、0020、0021、0023、0024）
  - id: agv01                    # 车辆标识，SIMPLE 分配，永不改变
    riot-device-key: BROKERX-…   # 对应车队调度系统里的哪台车
    project: D8
    zone: WIRE_BONDING           # 只属于一个派车区
    task-types: [WIRE_TO_GATE]   # 负责的任务类型
    slot-groups:                 # 仓位分组，按先后；组内仓号按先后
      - { name: front, slots: [1, 2, 3, 4] }
      - { name: rear, slots: [5, 6, 7, 8] }
    chargers:                    # 能去的充电桩，按先后
      - { map: 26, station: 211 }
    waiting-point: { map: 26, station: 214 }
    io-points:                   # IO 点位对应表：每个仓的锁反馈、光幕（DI）和开锁输出（DO）
      - { slot: 1, lock: DI1, light-curtain: DI9, unlock: DO1 }

parameters:                      # 只写要改的；名字见下表
  charge-needed-level: 40
```

站点一律写成"地图 + 站号"（`map`、`station`），因为站号跨地图不唯一（ADR 0015）。时长写成 `500ms`、`30s`、`10m`、`2h`。几台车一样的部分可以用 YAML 的锚点（`&io-points`、`*io-points`）只写一遍。

文件里没有"每个项目每个任务类型的开关"：首次导入一律关着，由管理员逐个打开（ADR 0017）。

### 参数

| 名字 | 默认 | 汇总表二里的那一行 |
| --- | --- | --- |
| `departure-nothing-loaded` | 5m | 离站倒计时：还没装上货 |
| `departure-loading-halfway` | 2m | 离站倒计时：一批装到一半 |
| `departure-between-sublots` | 5m | 离站倒计时：两批之间 |
| `departure-all-loaded` | 1m | 离站倒计时：全部装完（全厂区的值） |
| `no-progress-alarm` | 10m | 等人没动静告警 |
| `lock-reading-stale-after` | 10s | 门锁读数多久算不新鲜 |
| `terminal-fault-after` | 2m | 断联多久算车载终端故障 |
| `class-three-retrigger-window` | 10s | 三类急停解除后多久内又被触发算别人的急停 |
| `auto-recovery-wait` | 30s | 自动恢复前等待 |
| `hang-fault-after` | 10m | 挂起多久没继续成功算故障 |
| `resume-fault-after` | 30s | 发继续命令后多久仍挂起算故障 |
| `foreign-order-fault-after` | 10s | 外来单取消后多久没结束算故障 |
| `fault-cancel-read-back-alarm` | 10s | 进入故障时取消的单，多久读回不了或没结束就告警 |
| `brake-window`、`brake-count` | 10m、3 | 刹车：10 分钟内第 3 次 |
| `retry-max-delay` | 30s | 重试间隔上限 |
| `unreachable-alarm-after` | 2m | 读 MES、连车队调度系统失败多久告警 |
| `mes-stale-after` | 90s | MES 网关多久没有新的一轮算 MES 故障 |
| `io-module-fault-after` | 10s | 读不到 IO 模块多久算 IO 模块故障 |
| `vehicle-offline-fault-after` | 2m | 车离线多久算车辆本体故障 |
| `traffic-blocked-fault-after` | 10m | 被交通管制挡住多久算车队调度系统故障 |
| `emergency-stop-resend-max-delay` | 10s | 急停重发的间隔上限 |
| `alarm-escalate-after` | 30m | 不是故障的告警多久升级给班组长 |
| `urgent-charge-level`、`charge-needed-level`、`ready-level`、`full-level` | 20、35、60、99 | 四条电量线（百分比） |
| `charging-stall-window`、`charging-stall-rise` | 10m、3 | 涨不动的窗口和涨幅（百分点） |
| `event-retention-days` | 180 | 事件记录保存天数 |
| `unlock-request-max-age` | 3s | 开锁请求最长有效 |
| `onboard-input-max-age` | 5s | 车旁操作最长有效 |
| `buttons-grey-after` | 2s | 多久没收到新鲜的下发状态，按钮变灰 |
| `disconnected-after` | 3s | 多久没收到新鲜的下发状态，显示与服务端断开 |

车载终端重连服务端的间隔上限（25 秒）不在这里：断着线的车载终端拿不到配置，它是车载终端程序内置的值。

## 校验什么

导入时对整份配置一次校验完，列出全部问题，不是只报第一条。来自 ADR 的规则（`docs/rules-overview.md` 第 13 节）：

- 任务类型一端是机台、一端是固定站点（ADR 0006）；
- 区域号在整套 SIMPLE 里只归一个派车区，比较前只去掉首尾空白（ADR 0002、0015）；
- 同一项目里一个任务类型只归一台车；开着的任务类型必须配给本项目的一台车，停用的车也算（ADR 0023）；
- 车的派车区属于车所属的项目（ADR 0015、0023）；
- 固定站点、充电桩、等待点在车队调度系统的地图上存在（ADR 0015、0020、0021）；
- 一个站点只当充电桩、等待点、固定站点、机台站点四者之一，机台站点从地图上读（ADR 0021）；
- 每个项目、每张地图、每个任务类型最多一个固定站点；一个站点只做一个项目、一个任务类型的固定站点（ADR 0015）；
- 每台车至少一个充电桩；同一个桩不出现在两个项目的车的列表里（ADR 0020）；
- 每台车有等待点，两台车不共用（ADR 0021）；
- 紧急充电线 < 去充电线 < 可接活线 ≤ 充满线（ADR 0020）。

另外还查这些，否则配置读不成它本来的意思（实现这张票的会话补的，不是 ADR 定的）：编号不重复（项目、派车区、任务类型、车辆标识、车队调度系统里的车），引用的项目、派车区、任务类型都存在，开门侧是车辆清单里出现过的仓位分组名，仓号不重复，IO 点位对应表每个仓恰好一行、锁和光幕接输入、开锁接输出、一个点不接两个信号，时长大于 0，次数至少 1，电量线在 0～100 之间，编号和名字不超过数据表的列宽（编号 100 个字符、名字 200 个、说明 1000 个）。编号一律区分大小写：`agv01` 和 `AGV01` 是两台车，和区域号一样（ADR 0002）。

### 地图上的区域号

区域号对应哪个站点只看地图（ADR 0002）。D8 的地图把区域号写在站名里，SIMPLE 沿用 v2 线的读法：站名去掉首尾空白后按 `_` 拆开，每一段都是区域号（大写字母开头，接字母或数字、一个连字符、数字，例如 `N2-2`），而且互不相同，这个站点就是这几个区域号的机台站点，例如 `N2-2_N3-1`；别的站名（`关卡`、`充电点1`）不是机台站点。v2 线限制最多三段，SIMPLE 不限。
