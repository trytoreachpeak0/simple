# 协议探针（#18）

这是一对一次性的小程序，用来在真机上验证 ADR 0025 定下的传输与安全：gRPC 双向流（Kestrel 自承载）、TLS 1.2、服务端自签证书由终端钉住、每台车载电脑自带钥匙并在控制台核对配对码确认。它不是产品的一部分，不引用任何车辆控制、RIoT 或 IO 代码。验证结论写回 ADR 0025 之后，这个目录可以删除。

| 目录 | 程序 | 作用 |
| --- | --- | --- |
| `server/` | `simple-probe-server` | ASP.NET Core（.NET 10）+ Kestrel，提供 `simple.probe.v1` 双向流和 `simple.probe.v2`（只为版本测试）；标准输入是操作台 |
| `client/` | `simple-probe-client` | .NET 10 控制台，扮演车载终端：首次启动生成密钥与自签证书，钉住服务端公钥，每秒上报，回显命令编号，断线按 1 秒起、30 秒封顶、带抖动的指数退避重连 |
| `linkcut/` | `simple-probe-linkcut` | 本机 TCP 中继，只监听回环地址，模拟"丢包不回 RST"的静默断链。开发机上代替回环上用不了的防火墙阻断；车载电脑防火墙配置文件关着时，也在车载电脑上用（加 `--allow-remote-target`） |
| `field/` | `Run-Field.ps1` | 第二段：经 remote-ops 的 SSH 通道在服务端机器和一台车载电脑上跑同样的步骤 |
| `loopback/` | `Run-Loopback.ps1` | 在开发机上按工单第 1～9 步自动跑一遍并写出每步的结果文件 |

## 发布

两端都按自包含方式发布，目标机上不用装 .NET 运行时：

```
dotnet publish tools/protocol-probe/server -c Release -r win-x64 --self-contained -o artifacts/probe/server
dotnet publish tools/protocol-probe/client -c Release -r win-x64 --self-contained -o artifacts/probe/client
dotnet publish tools/protocol-probe/linkcut -c Release -r win-x64 --self-contained -o artifacts/probe/linkcut
```

## 服务端

```
simple-probe-server --bind <服务端厂区网卡的 IP> --port 5443 --data D:/simple-probe/data
```

- 默认只监听 `127.0.0.1:5443`，数据目录默认在当前用户的 `%LOCALAPPDATA%\simple-probe\server`。要让车载电脑连进来，必须显式给 `--bind`，而且绑具体网卡的 IP、不绑 `0.0.0.0`：factory01 上绑 `0.0.0.0` 会把探针一并暴露到 CI runner 所在的另一个网段。
- 第一次启动在数据目录里生成 `server.pfx`（服务端证书，无口令，不进 Windows 证书存储）和 `server-pin.txt`（证书公钥的 SHA-256，终端要钉的就是它，属公开信息）。`terminals.json` 记录每把终端钥匙是待确认、已确认、已吊销还是疑似克隆。
- `--key-load user|machine`：服务端证书私钥导入到哪里。默认 `user`（当前用户密钥存储）。**经 SSH 公钥登录运行时必须用 `machine`**：这种登录没有用户的 DPAPI 主密钥，导入当前用户存储会报 `拒绝访问`。只放内存（EphemeralKeySet）试过，不能用：SChannel 报 `the platform does not support ephemeral keys`，所以没有这个取值。两种取值下 PFX 都导入到一个临时密钥容器里：在操作台输入 `quit`、服务端正常完成关停时删除（各条双向流在服务端开始关停时立即结束，即使有终端连着也约一秒退出）；进程被强行结束、或没能完成关停时会留下，启动日志 `SERVER-CERT ... keyContainer=` 记下了它的名字，按名字删除（`Run-Field.ps1` 的 `Cleanup` 步骤就是这样做的）。其他取值直接报错退出。
- 其他选项：`--tls 12`（默认，只开 TLS 1.2；`os` 表示用系统默认）、`--key-alg ecdsa|rsa`、`--command-interval 10`（秒）、`--stale-after 10`（秒，"上报过期"计时器）、`--log <文件>`、`--console-log false`。
- `--ping-delay <秒>`、`--ping-timeout <秒>` 打开服务端的 HTTP/2 保活 ping（Kestrel 默认不发 ping）。启动时日志里有一行 `KESTREL ...` 记下生效的值。注意：`--Kestrel:Limits:...` 这种配置写法对 Limits 不生效（实测），所以单独做了这两个选项。

操作台命令（在服务端窗口里输入）：

| 命令 | 作用 |
| --- | --- |
| `list` | 列出全部终端：钥匙指纹前 12 位、状态、车号、配对码、是否在线 |
| `confirm <配对码> <车号>` | 核对配对码后确认"这是哪台车" |
| `revoke <车号或钥匙指纹前缀>` | 吊销，在线的流立即以 `PERMISSION_DENIED` 结束 |
| `forget <车号或钥匙指纹前缀>` | 删掉登记，这把钥匙下次连上来重新待确认（疑似克隆后由工程师用它重新确认） |
| `quiet on/off` | 暂停／恢复每 10 秒下发的编号命令（第 3 步用） |
| `hint on/off` | 下发消息里多带一个 1.0 客户端不认识的字段 50（第 9 步 c） |
| `stats` | 每把钥匙的命令下发／回显数、上报间隔分位数 |
| `quit` | 退出 |

日志每行一个事件，带毫秒和时区。主要关键字：`CALL`（协商出的 HTTP 版本、TLS 版本、密码套件、ALPN）、`PENDING`、`ADMIT`、`SUPERSEDE`（同一把钥匙的新连接顶替旧连接）、`RX`／`TX`（每条消息的收发时刻）、`ECHO`、`STALE`／`FRESH-AGAIN`（上报过期计时器）、`ALERT`（疑似克隆）、`REFUSE`、`REFUSE-VERSION`、`SESSION-END`、`RESOURCE`（每分钟一次工作集与 CPU）。

## 客户端

```
simple-probe-client --data C:/simple-probe/client
```

- 服务端地址和要钉的指纹写在数据目录的 `client.json` 里（`{"server": "https://<服务端地址>:5443", "pin": "<server-pin.txt 的内容>"}`），也可以用 `--server`、`--pin` 给。仓库里不放任何真实地址。
- 第一次启动在当前用户的 CNG 密钥存储里生成不可导出的 ECDSA P-256 私钥（`--key-provider tpm` 时放进 TPM，经 Microsoft Platform Crypto Provider），自签证书放进 `CurrentUser\My`。`identity.json` 只记密钥容器名、证书指纹和服务端确认过的车号，不含任何秘密。
- `--key-store user|machine`：默认 `user`（当前用户密钥存储与 `CurrentUser\My`）。经 SSH 公钥登录运行时用 `machine`（机器密钥存储与 `LocalMachine\My`，仍不可导出，要管理员），原因同服务端的 `--key-load`。其他取值直接报错退出。钥匙和证书是持久的，用完要 `--reset`。
- `--reset` 删除证书、密钥和 `identity.json`，并打印删除后的核对结果（按 `identity.json` 里记的存储位置删）。
- 测试用选项：`--major <n>`（报给服务端的主版本）、`--minor-extra`（上报里多带服务端不认识的字段 50）、`--interval <秒>`、`--claim-vehicle <车号>`（冒充某台车，第 8 步 c）、`--keepalive`／`--keepalive-timeout`（HTTP/2 保活，默认 10 秒／10 秒）。
- 运行中可在窗口里输入 `interval <秒>`、`extra on/off`、`status`、`quit`。
- 屏上（日志 `STATE` 行）显示：待确认及配对码、已连接、与服务端断开、版本不匹配（需要更新／不一致两种说法）、被服务端拒绝、服务端证书不是钉住的那一张。

## 在开发机上跑一遍（工单第 0 步）

```
pwsh -File tools/protocol-probe/loopback/Run-Loopback.ps1 -Run <仓库外的目录> -Bin artifacts/probe -Steps 1,3,4,4k,5,6,7,8,9
pwsh -File tools/protocol-probe/loopback/Run-Loopback.ps1 -Run <同上> -Bin artifacts/probe -Steps 2
pwsh -File tools/protocol-probe/loopback/Run-Loopback.ps1 -Run <同上> -Bin artifacts/probe -Steps 8f,Cleanup
```

每步的结果写在 `<Run>/results/stepN.md`，原始日志在 `<Run>/logs/stepN/`。第 1 步会清掉 `terminals.json` 并给 client-a 换新钥匙，后面各步依赖它确认过的 agv-a，所以要先跑第 1 步。`Cleanup` 删除全部测试用的终端钥匙和证书，并核对证书存储里没有残留。

## 在真机上跑（第二段）

```
pwsh -File tools/protocol-probe/field/Run-Field.ps1 -Run <仓库外的目录> -ServerAlias <服务端别名> -ClientAlias <车载电脑别名> -ServerBind <服务端厂区网卡 IP> -Steps 1,3,4,6,7,8,9,2
pwsh -File tools/protocol-probe/field/Run-Field.ps1 -Run <同上> -ServerAlias <同上> -ClientAlias <同上> -ServerBind <同上> -Steps Collect,Cleanup
```

- 先把三个发布目录解压到服务端机器的 `<ServerRoot>/server` 和车载电脑的 `<ClientRoot>/client`、`<ClientRoot>/linkcut`（默认 `D:/simple-probe`、`C:/simple-probe`）。
- 脚本经 `ssh <别名>` 在远端起探针进程，经标准输入下发操作台命令，把远端控制台输出镜像到 `<Run>/mirror/`，每步结果写到 `<Run>/results/`。各步共用同一组进程，所以要在一次调用里按上面的顺序跑。
- 服务端用 `--key-load machine`、客户端用 `--key-store machine`，原因见上。
- 第 4 步和第 8 步 (d)① 经车载电脑本机的 linkcut 中转；不加任何防火墙规则。
- 停下所有进程时先停终端和 linkcut、最后停服务端；停服务端先在操作台输入 `quit` 等它正常退出（这样临时密钥容器会被删除），40 秒没退出才强行结束；第 6 步要模拟服务端崩溃，是有意强行结束的。
- 每一次远端调用（ssh、scp）的退出码都检查，非零就报错停下；`Cleanup` 的核对结果不符合预期（容器删除后仍在、目录仍在、还有探针进程或证书）也报错退出。
- 别名写 `.` 时不经 ssh，直接在本机起进程（只用于在开发机上验证脚本本身），再加 `-KeyStore user` 让两端把钥匙放当前用户存储（本机不是管理员时）。
- `Collect` 把两端的日志用 scp 拷回 `<Run>/logs/`；`Cleanup` 对各个测试终端执行 `--reset`，从服务端机器上的日志（以及本机镜像）里读出全部 `keyContainer=`，逐个删除并核对服务端留下的临时密钥容器，删除两台机器上的探针目录，并把核对结果写进 `<Run>/results/cleanup.md`。
- `Run-Field.ps1` 的 `Collect`、`Cleanup` 是第二段真机跑完之后才写的，真机那次清理是手工按同样的办法做的；它们只在开发机上用本机模式验证过。
