# MES 网关

MES 网关是一个单独的小进程，是 SIMPLE 里唯一和客户 MES 打交道的地方。它一轮一轮地读 MES，把最近一轮的整份结果放在 `GET /api/v1/snapshot` 上，服务端定时来拉（ADR 0027）。网关的接口只在本机回环地址上监听（默认 `http://127.0.0.1:5190`），没有登录。

网关有两种数据来源，由 `Simple:Mes:Source` 选：

| 来源 | 是什么 | 什么时候用 |
| --- | --- | --- |
| `Fake`（默认） | 假 MES：从 `Fake/seed-rows.json` 起步，测试可以经 `/fake-mes/*` 加减行、让下一轮失败或格式不对 | 本机、测试、CI、`simple up`；仓库里的配置只用它 |
| `Oracle` | 客户的 Oracle MES，只读 | 由人为某一次运行明确选用，地址和账号从仓库外来 |

## Oracle 来源

### 用什么连

用 Oracle 官方的托管驱动 `Oracle.ManagedDataAccess.Core`（23.9.0，Thin 模式），机器上不用装 Oracle 客户端。这是上一代 MesIngest 在生产上读同一个客户 MES 用的驱动和版本。

- 最多 2 个连接（`OracleMesSource.MaxConnections`）：每轮只读一次、一轮接一轮；第二个连接留给"上一轮超时被放弃、驱动还没放开连接"的情况。
- 打开连接最多 15 秒（`Simple:Mes:Oracle:ConnectTimeout`），查询最多 30 秒（`Simple:Mes:Oracle:QueryTimeout`，和上一代一样）。整次读取另有一个总时限，等于两者之和再加 5 秒；超过就放弃这次读取，这一轮按读失败算。
- 两轮之间最长可能隔多久：两轮之间的等待（成功后是 `Simple:Mes:RoundInterval`，失败后最长是 `Simple:Mes:RetryMaxDelay`）取较长的那个，再加上总时限。这个值必须不超过 80 秒，否则网关拒绝启动。原因是服务端超过 90 秒（它的 `StaleAfter` 默认值）没看到新轮次，就判"轮次停住"；留出 10 秒给服务端 2 秒一次的拉取和 5 秒的请求时限，MES 卡住时服务端看到的才是"读不到 MES"。网关不知道服务端实际配的值，按默认的 90 秒算；服务端如果把 `StaleAfter` 调得更短，网关这边也要跟着缩短。默认值是 30 + 50 = 80 秒，正好在线上。

### 只跑客户批准的那一条查询

查询原文在 [`Oracle/mes-task-union.sql`](Oracle/mes-task-union.sql)，编译进网关程序。它是上一代 MesIngest 的 `MES_TASK_UNION`，取自 `8005-mes-ingest` 仓库 `bb397e7`（2026-10-16 合并，七类任务，第七类 `WIRE_TO_FULL_INSPECTION` 是那天加的）的 `queries/mes-task-union/query.sql`，一个字节都没改，MES 的业务语义也没动。

- 查询的 SHA-256 写死在程序里（`MesTaskUnionQuery.ExpectedSha256`）：`793452f171ce70520b187fc6411c771e731b3307906215422909d1814687847c`，和上一代 `CanonicalMesTaskUnionQuery.ExpectedSha256` 是同一个值。摘要按 CRLF 换行的字节计算；`Oracle/.gitattributes` 让每次检出都是 CRLF。
- 编进程序的查询和这个摘要差一个字节，选了 `Oracle` 的网关就拒绝启动；读取用的也只是核对过的这份原文。
- 要换查询，只能先由客户 IT 批准新的原文，再在同一个 PR 里换文件和摘要。

查询输出 7 列 `TASK_TYPE, SUBLOT, AREA, EQP, STEP, DATES, PACKAGE`。网关只检查结构（ADR 0027），不判断值；SQL 注释里说的"应用层按 2026-08-01 过滤"不在网关里做，是服务端把快照变成需求时的事。

### 只读怎么保证

- **网关这一侧**：它发给 MES 的只有上面这一条 `SELECT`，作为返回行的查询执行；不开事务、不带参数，代码里没有别的语句（`Oracle/OracleMesSource.cs`）。
- **账号这一侧**：上一代的需求文档写过，客户给的 MES 账号可能有写权限，只读只靠我们的代码。第一次读真 MES 之前，应请客户 IT 给一个只有 `SELECT` 权限的专用账号，或者由他们确认现有账号的权限；这件事要客户 IT 来查，网关不对 MES 发任何别的语句去探权限。

### 失败怎么显示

每一种失败都是失败的一轮，网关继续按退避重试（1 秒起，最长 30 秒），快照里 `succeeded` 为 `false`、`rows` 为 `null`：

| 情况 | `failure.kind` | `failure.message` 举例 |
| --- | --- | --- |
| 驱动起不来、连不上、账号被拒 | `read` | 驱动的原话，如 `ORA-12541: Cannot connect. No listener at host … port …` |
| 查询超过查询时限 | `read` | `The MES query did not finish within 30 s: ORA-01013: …` |
| 整次读取超过总时限 | `read` | `The MES read did not finish within 50 s (…); it was abandoned.` |
| 列不是约定的 7 列，或值不是文字、日期、空 | `format` | `The result has no column PACKAGE.` |

### 凭据

地址和账号只从仓库外来，由人为那一次运行选定；仓库里的 `appsettings*.json` 不带任何 `Simple:Mes:Oracle` 设置（`GatewaySettingsTests` 检查）。快照里的失败原因和程序日志都经过 `Simple.Logging` 的脱敏：连接串里的 `Password=…`、`Simple__Mes__Oracle__Password=…`、Oracle 工具常用的 `user/password@库` 写法（库写成 `host:port/service`、`//host:port/service`、TNS 别名或 `(DESCRIPTION=…)` 都算，也包括 `jdbc:oracle:thin:user/password@…` 和 Oracle 工具的 `userid=`、`target=`、`auxiliary=`、`catalog=`、`conn=`、`connect=` 参数，密码里带 `/` 或 `@` 也一样），密码都只留下 `***`。遮不住的几种：登录紧跟在别的 `=` 或 `,` 后面，密码里有引号、括号或超过 4 个 `@`，库名写在引号里（`@"db"`）。网关自己不会写出这些写法。

## 对真 MES 跑一次只读读取

这一步要产品负责人点头、选定时间，由协调会话安排；本节只说做法。`simple up` 做不了这件事：它只起本机的假系统，还会去掉调用者环境里的 `Simple__*` 变量。

1. **准备设置文件，放在仓库外**，例如 `C:\simple-local\mes-gateway.json`。里面不写密码：

   ```json
   {
     "Simple": {
       "Mes": {
         "Source": "Oracle",
         "RoundInterval": "00:00:30",
         "Oracle": {
           "DataSource": "<host>:<port>/<service>",
           "User": "<只读账号>"
         }
       },
       "Logs": { "Directory": "C:\\simple-local\\mes-gateway-logs" }
     }
   }
   ```

   `DataSource` 只写库的地址（`host:port/service` 或 TNS 描述符），不要写成 `user/password@…`，网关会拒绝。`RoundInterval` 是两轮成功读取之间的等待；只是试读的话，放长一些对 MES 更客气，但最长 30 秒（默认时限下；见上文"两轮之间最长可能隔多久"）。

2. **先构建**（在仓库根目录，这时环境里还没有密码）。构建会留下常驻的 MSBuild 和编译服务进程，先构建、再输密码，它们就不会带着密码变量：

   ```powershell
   dotnet build src/Simple.MesGateway -c Release
   ```

3. **在同一个 PowerShell 7 窗口里输入密码**，只放进这个窗口的环境变量，不落盘：

   ```powershell
   $env:Simple__Mes__Oracle__Password = Read-Host -MaskInput 'MES password'
   ```

4. **启动网关，不再构建**：

   ```powershell
   dotnet run --project src/Simple.MesGateway -c Release --no-build -- --Simple:SettingsFile=C:/simple-local/mes-gateway.json
   ```

   配置有误时网关拒绝启动，退出码 2，并说出是哪一项（不会显示密码）。

5. **读快照**：

   ```powershell
   Invoke-RestMethod http://127.0.0.1:5190/api/v1/snapshot
   ```

   `round` 从 1 起每读一次加一；`succeeded: true` 时 `rows` 是这一轮的全部行，失败时看 `failure`。

6. **读到想要的结果就停**：在网关窗口按 Ctrl+C，然后 `Remove-Item Env:Simple__Mes__Oracle__Password`。程序日志在第 1 步设的目录里，可以拿来对照；里面不应出现密码，出现了就是缺陷，请报告。

## 不在这里做的

- 箱数查询（上一代的 `sublot-box-count`）和每个子批的篮数：要先问客户 IT。
- 把快照变成需求、判断需求出现和消失：服务端的事（ADR 0003、ADR 0027）。
- 写 MES：以后如果要写，也只经网关，另行评审。
