# 场景

统一格式的测试场景：初始状态、事件、期望结果。CI、人和 AI 跑的是同一批场景文件，只是入口不同：

| 谁 | 怎么跑 |
| --- | --- |
| 人、AI 会话 | `./simple scenario run <场景名>`，或在远端机器上：`./simple remote scenario run <场景名>`（见下面的"在远端机器上跑"） |
| `dotnet test`、CI | `tests/Simple.Cli.Tests/ScenarioTests.cs` 把这里的每个场景当作一个测试用例 |

每个场景是这个目录下的一个 `.yaml` 文件，场景名就是文件名（不带扩展名，子目录用 `/`）。格式定义是 [`scenario.schema.json`](scenario.schema.json)（JSON Schema）；文件第一行写上 `# yaml-language-server: $schema=scenario.schema.json`，支持 YAML 语言服务的编辑器就能按它提示和校验。

## 命令

```
./simple scenario list                     列出场景、标题、预期判定、文件是否有效
./simple scenario validate [场景…]          校验（默认全部）；不对时逐条说出第几行、哪一列、哪里不对，退出码 2
./simple scenario run <场景> [--keep-up]    跑一个场景，退出码就是判定（停不掉系统时是 1）
```

`<场景>` 可以是场景名（`infrastructure-up`），也可以是任意位置的 `.yaml` 文件路径，方便先在别处写一个会失败的场景再放进来。

### 在远端机器上跑

`./simple remote scenario run <场景>`、`list`、`validate` 在 `SIMPLE_REMOTE` 指的远端机器上跑同一个命令，用的是本克隆此刻的工作树（没提交的改动和新文件都在，见仓库根 README 的"把重活送到远端机器上跑"）。和本机的区别：

- `run` 占远端整机的一个名额，服务端钥匙放在那台机器的本机级存储里，证据目录写进这次运行的目录，随输出一起拷回本机 `artifacts/remote/<实例名>/<时刻>-<提交号前 8 位>/`。输出里 `Evidence:` 那一行是远端的路径，本机的副本在最后一行写的目录下。退出码照样是判定。
- 场景写成路径时，文件必须在本克隆里、没被 `.gitignore` 排除，否则远端拿不到它，命令以 2 结束。
- 不收 `--evidence`、`--server-key-store`（由它自己定）和 `--keep-up`（远端留着的系统会随 SSH 会话一起被结束）。
- 按 Ctrl+C 时远端这次运行整个被结束，不写 `result.json`；部件进程随之结束，实例的库留到下一次运行重置。
- `./simple remote forget` 也重置场景用的实例 `<实例名>-scenario`（删库、删服务端钥匙）。

`run` 做的事：

1. 校验文件，不对就以退出码 2 结束，什么都不启动。
2. 用场景自己的实例（默认 `<克隆目录名>-scenario`，和本克隆的 `simple up` 互不干扰；`--instance` 可以另指定）。同一个实例同时只跑一个场景，后来的等前一个跑完（最多 5 分钟，等不到退出码 7）。先 `down --reset`：停掉它、删掉它的数据库，再清掉它上次的程序日志。
3. 按初始状态 `up`：和 `simple up` 完全一样起一套真进程，只是多带上场景要的参数（假 RIoT 倍速、假 MES 起始行、各仓起始货物）；再按初始状态改假 RIoT 的车。
4. 每 250 毫秒看一次系统：服务端的 `GET /api/v1/status` 和事件记录 `GET /api/v1/events`。按顺序做事件，判每个期望，直到所有事件做完、所有期望都有了判定。
5. 导出事件记录和最后的状态，再拿最后这份事件记录把事件类期望复核一遍（见下文），停掉系统（`--keep-up` 时不停，可以接着用 `simple status/events/logs --instance <实例>` 看），收集各部件日志，写 `result.json` 和校验清单。系统停了以后实例的数据库还在，下一次跑这个实例时才重置。

中途按 Ctrl+C，或者运行器自己出了异常，也照样停掉系统、收集证据、写 `result.json`，判定是"判不了"，原因写在 `driveFailure` 里。停系统有自己的 5 分钟时限，不受 Ctrl+C 影响。系统没能停掉时，`result.json` 的 `teardown` 写明原因，命令以退出码 1 结束（不管判定是什么），因为留着一套没停的系统本身就是运行失败；`dotnet test` 里这个用例也算失败。所以看 `result.json` 判断一次运行是否成功，除了 `verdict`（或 `asExpected`），还要看 `teardown.stopped`：`verdict` 只说场景本身，不说系统停没停（带 `--keep-up` 时 `stopped` 为 `false`、`kept` 为 `true`，系统本来就该留着，不算失败）。证据文件（时间线也算）写不进去（例如磁盘满）时，不影响判定和停系统，只列在 `evidenceMissing` 里。

## 四种判定和退出码

| 判定 | 退出码 | 意思 |
| --- | --- | --- |
| `holds` 成立 | 0 | 每个适用的期望都看到成立 |
| `does-not-hold` 不成立 | 10 | 至少一个期望看到了不成立 |
| `cannot-tell` 判不了 | 11 | 有期望看不到（状态里没有那个字段、服务端不应答、事件记录读不到），或者场景没能按写的那样驱动（系统没起来、事件失败、等的条件一直没来）。**判不了永远不算通过** |
| `not-applicable` 不适用 | 12 | 所有期望的 `only-if` 都不成立，没有一个期望适用 |

合起来的规则：有一个"不成立"就是不成立；否则有一个"判不了"或者场景没驱动完，就是判不了；否则至少一个成立就是成立；全部不适用才是不适用。

场景用 `expected-verdict` 写自己预期的判定，默认 `holds`。预期不是 `holds` 的场景只能有一个期望，这样判定一定来自它。`run` 的退出码永远是实际判定；`dotnet test` 比较实际判定和预期判定，一致才通过。所以"故意写反"的场景判"不成立"时，CI 是绿的；如果它竟然成立了，CI 反而变红。

## 格式

```yaml
# yaml-language-server: $schema=scenario.schema.json
title: 一行话说明这个场景证明什么          # 必填
description: >                             # 可选，多写几句
  ……
site: sample                               # 必填：用 sites/<site>/ 的厂区配置
expected-verdict: holds                    # 可选，默认 holds

initial:                                   # 初始状态，整段可省
  terminals: 1                             # 几台车有车载终端（各带一个 IO 模拟器），不能多于厂区的车辆数；默认 0
  fake-riot:
    speedup: 10                            # 假 RIoT 的倍速（FakeRiot:Timing:Speedup），默认 1
    vehicles:                              # 开始前改假 RIoT 的车
      - { device-key: BROKERX-…fa01, battery: 50, mode: MODE_AUTO }
  fake-mes:
    rows:                                  # 假 MES 的起始行，代替网关自带的种子行；省略就用种子行
      - { task-type: SAMPLE_TO_OVEN, sublot: SMP0009.01, area: S1-1 }
  slots:                                   # 各仓起始有货的仓位（1～8）
    - { terminal: 1, cargo: [1, 3] }

events:                                    # 按顺序做；每个事件恰好一个触发方式、恰好一个动作
  - id: drop-terminal                      # 可选；期望用 after 引用它
    at: 2s                                 # 从场景开始算的时间（不会早于上一个事件做完）
    stop-process: { process: terminal }
  - when: { status: mesGateway.available, equals: true }   # 或者：等条件成立
    within: 30s                            # 最多等多久，默认 30s；等不到就是判不了
                                           # 等待从上一个事件开始做的那一刻算起（第一个事件从场景开始算），
                                           # 所以上一个事件刚做时就记下的事件记录也算数
    fail-mes-reads: { count: 3 }

expectations:                              # 至少一个
  - id: disconnect-recorded                # 可选，结果里用它称呼这个期望
    title: 断线被记下                       # 可选
    after: drop-terminal                   # 可选：窗口从这个事件开始做的那一刻打开；默认从场景开始
    eventually: { event: onboard.disconnected, data: { vehicleId: vehicle-1 } }
    within: 15s
    only-if: { status: onboardTerminals.count, at-least: 1 }   # 可选：窗口打开时不成立就"不适用"
```

时长写成 `500ms`、`5s`、`1.5s`、`2m`。

### 期望的三种写法

| 写法 | 窗口 | 成立 | 不成立 |
| --- | --- | --- | --- |
| `eventually: <条件>` + `within: T` | T 秒内终将成立 | 窗口内某一次看到成立 | 窗口内每一次都看到不成立 |
| `always: <条件>` + `for: T` | T 秒内一直成立 | 窗口内每一次都看到成立 | 某一次看到不成立 |
| `never: <条件>` + `for: T` | T 秒内从不出现 | 窗口内每一次都看到不成立 | 某一次看到成立 |

窗口里只要有一次"看不到"，又没有决定性的一次，就判"判不了"。

**状态条件是抽样看的**：每 250 毫秒看一次，比这更短的变化可能两次之间就过去了，看不到。要证明某件事发生过（哪怕只有一瞬间），用事件记录的条件，不要用状态条件。另外，做一个耗时的动作（例如停一个进程）时运行器顾不上看；窗口里有超过 1 秒没看过的空档时，状态条件不能靠窗口结束来下结论（`always`、`never` 判成立，`eventually` 判不成立），而是判"判不了"，理由里写明哪一段没看到。窗口内已经看到的决定性结果（`eventually` 看到成立、`always` 看到不成立、`never` 看到出现）不受空档影响。

事件记录的条件是累积的（记下了就一直在），所以窗口结束 1.5 秒后再看一次事件记录，用这一次给整个窗口下结论：服务端写事件记录比事情发生晚一点点。跑完以后还会拿最后一份完整的事件记录复核一遍；某条记录发生在窗口内、却在下结论之后才写进记录，复核结果和原判定不一致时，这个期望改判"判不了"。

### 条件

两种，一个条件只能是其中一种：

- **状态**：`status: <路径>` 加恰好一个比较：`equals`、`not-equals`、`at-least`、`at-most`（后两个只比数字）。路径按 `GET /api/v1/status` 的 JSON 写，用 `.` 连接：
  - 字段：`riot.state`、`mesGateway.available`；
  - 下标（从 0 开始）：`onboardTerminals[0].connected`；
  - 按字段值挑第一个：`onboardTerminals[vehicleId=vehicle-1].connected`、`io.inputs[number=9].value`；
  - `count`：数组的长度，`onboardTerminals.count`。

  路径在状态里不存在（例如还没报到的终端）算"看不到"，不算"不成立"。`only-if` 只能用状态条件（它只在窗口打开那一刻判一次）。
- **事件记录**：`event: <类型>`，可加 `source`（`server`、`onboard-terminal`）和 `data`（事件数据里必须有这些字段、值相等，其他字段不管）。在窗口内记下过这样一条就算成立。事件条件只配 `eventually` 和 `never`，不配 `always`（记下了就一直在，"一直成立"没有意义）。事件类型见 `src/Simple.Server/Events/EventRecord.cs`。

本地起的车载终端报告的车号是 `vehicle-1`、`vehicle-2`……，和终端编号一致。

### 动作

经各个假部件的控制接口去做，和人或 AI 手动调接口效果一样：

| 动作 | 参数 | 做什么 |
| --- | --- | --- |
| `add-mes-row` | `task-type`、`sublot`、`area`、`eqp`、`step`、`dates`、`package` | 假 MES 出现一行 |
| `remove-mes-row` | `task-type`、`sublot` | 假 MES 里这个业务键的行消失 |
| `fail-mes-reads` | `count`，可选 `message` | 假 MES 接下来 count 次读取失败；0 取消 |
| `malform-mes-reads` | `count` | 接下来 count 次读取多返回一行缺值的行；0 取消 |
| `set-riot-vehicle` | `device-key`，可选 `battery`、`brake-released`、`mode`、`manual-charger`、`emergency-button-pressed` | 车旁的人改车的状态（电量、刹车、手柄模式、充电器、急停按钮） |
| `fail-riot-creates` | `times`，可选 `order-made` | 假 RIoT 接下来 times 次建单回 00002（注入错误码） |
| `scan` | `terminal`、`text` | 车旁的人在第 n 台终端扫码（像扫码枪一样以回车结尾） |
| `press` | `terminal`、`button` | 车旁的人在第 n 台终端按键 |
| `fault-io-module` | `terminal`、`mode`（`none`、`no-response`、`disconnect`、`delay`），可选 `delay-ms` | 第 n 台终端的 IO 模块不应答、断连接或变慢；`none` 解除 |
| `auto-respond` | `terminal`、`action`（`load`、`unload`）、`doors`，可选 `act-seconds`、`close-seconds`、`within-seconds` | 仓前的人：接下来打开的 doors 扇门，各放进或取出货物后关门（IO 模拟器的自动应答） |
| `stop-process` | `process` | 停掉系统里的一个进程，名字和 `simple status` 里的一样（一台终端时叫 `terminal`，多台时叫 `terminal-1`……）；例如车载终端掉线 |

动作失败（控制接口报错、进程不存在）时，后面的事件都不再做，等这些事件的期望判"判不了"，整个场景至少是"判不了"。

## 时间

业务计时不靠给服务端加"测试倍速"：时长本来就是参数配置（`docs/adr/0017-site-config-lives-in-server.md`），场景要短就在自己的配置里写短。服务端现在还不读厂区配置，所以目前场景只能调假部件的节奏：假 RIoT 用自己的倍速（`initial.fake-riot.speedup`）。

## 厂区

场景默认用虚构的小厂区 `sites/sample/`（三台车、区域 `S1-1`、`S1-2`、`S2-1`，任务类型 `SAMPLE_TO_OVEN`、`SAMPLE_TO_GATE`，和假 MES、假 RIoT 的起始数据对齐，见那里的 README）。校验时会检查：终端数不多于厂区车辆数，假 MES 行的区域号和任务类型是厂区里有的，改的车是厂区车辆清单里的。以后少数"D8 冒烟"场景会用 `site: d8`，等 `sites/d8/site.yaml` 写出来再加。

## 证据

每跑一次，在仓库外建一个证据目录，默认在 `%LOCALAPPDATA%\simple\scenario-runs\`（设了 `SIMPLE_HOME` 就在它下面；`--evidence <目录>` 可以另指定），目录名是 `<年月日-时分秒>-<场景名>-<运行号>`：

| 文件 | 内容 |
| --- | --- |
| `timeline.jsonl` | 边跑边写的时间线，一行一条：重置、起系统、每条新的事件记录、每个事件做了什么、每个期望何时判成什么、观察失败与恢复 |
| `events.jsonl` | 服务端事件记录的导出，一行一条 |
| `status.json` | 结束前最后一次的服务端状态 |
| `result.json` | 判定、预期判定、每个期望的判定和理由（含它在文件里的行号）、每个事件的结果、证据是否齐全 |
| `logs/` | 各部件的程序日志（`logs/<进程>/`）和控制台输出（`logs/<进程>.log`），还有这次编译的输出 |
| `scenario.yaml` | 跑的那份场景文件的副本 |
| `fake-mes-rows.json` | 场景写了假 MES 起始行时，交给网关的那份 |
| `MANIFEST.sha256` | 最后写：其余每个文件的 SHA-256 |

收集某个文件失败不改变判定，只在 `result.json` 的 `evidenceMissing` 里列出来。

`dotnet test` 里某个场景用例失败时，测试会把它的证据目录复制出来，免得随测试的临时目录一起删掉：CI 上复制到测试结果目录（`SIMPLE_SCENARIO_EVIDENCE`，随测试结果一起上传），经 `simple remote test` 时复制到带回本机的运行目录，否则复制到克隆里的 `artifacts/scenario-evidence/`。

## 现有的场景

第一批只覆盖基础设施本身；业务场景由各张产品票自带。

| 场景 | 证明什么 | 预期判定 |
| --- | --- | --- |
| `infrastructure-up` | 空系统起来后两台车载终端在线、RIoT 已连上、MES 可达 | holds |
| `terminal-disconnect` | 车载终端掉线，服务端记下断线、状态变成未连接、报告过期 | holds |
| `mes-source-fails-and-recovers` | MES 来源读取失败，服务端记下不可用，网关重试成功后记下恢复 | holds |
| `io-module-unreadable` | IO 模块不应答，服务端记下读不到；解除后记下又读得到；起始有货的仓位光幕读 0 | holds |
| `deliberately-wrong` | 期望故意写反，证明运行器真的会判"不成立" | does-not-hold |
| `cannot-tell-on-purpose` | 期望看的车不存在，证明观察不到时判"判不了"（退出码 11，不同于"不成立"的 10） | cannot-tell |
