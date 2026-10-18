# sample：虚构的小厂区

给场景（`scenarios/`）和测试用的虚构厂区：两个项目、三台车、三个区域号、两种任务类型。它不对应任何真实厂区，改它不会影响现场；场景默认都用它，这样 D8 的真实配置改动不会让一批无关的场景变红。少数"D8 冒烟"场景以后会直接用 `sites/d8/`。格式见 `sites/README.md`。

`site.yaml` 里的值和两个假部件的起始数据对齐：

| 内容 | 值 | 对齐的是 |
| --- | --- | --- |
| 区域号 | `S1-1`、`S1-2`（地图 1 的 1、3 号站），`S2-1`（地图 2 的 2 号站） | 假 MES 的种子行 `src/Simple.MesGateway/Fake/seed-rows.json`；假 RIoT 的站名 `src/Simple.FakeRiot/seed.json` |
| 任务类型 | `SAMPLE_TO_OVEN`、`SAMPLE_TO_GATE`，都是机台送固定站点 | 假 MES 的种子行 |
| 车辆 | `vehicle-1`～`vehicle-3`，对应假 RIoT 的 `fake-vehicle-1`～`3`（设备号 `…fa01`～`…fa03`） | 假 RIoT 的车；本地车载终端报的车号也是 `vehicle-1`、`vehicle-2`…… |
| 站点 | 地图 1：2 号关卡、4 号烘箱、5 号充电桩、7、8 号等待点；地图 2：1 号等待点、3 号充电桩、4 号烘箱、5 号关卡 | 假 RIoT 的地图 |
| IO 点位对应表 | 第 n 仓锁反馈 DI n、光幕 DI n+8、开锁 DO n | IO 模拟器的接线 `src/Simple.IoSimulator.Core/Cabinet.cs` |

两个项目：`SAMPLE_A` 有一区（地图 1，`S1-1`、`S1-2`），`vehicle-1` 负责送烘箱、`vehicle-2` 负责送关卡；`SAMPLE_B` 有二区（地图 2，`S2-1`），`vehicle-3` 两类都负责。参数都用默认值。

改了这几份起始数据中的任何一份，要同步改这里；`Simple.Server.Tests` 的 `SitesInRepositoryTests` 会对着假 RIoT 的地图校验这份配置，并核对它和两份种子数据对得上。

## 谁读它

- `simple up` 起服务端前，服务端命令 `import-site` 导入它（首次起来时存成第 1 版，之后什么都不做）；场景 `site: sample` 也是这样导入的。
- `simple scenario validate` 和 `simple scenario run` 用它检查场景：
  - 场景起的车载终端数（`initial.terminals`）不能多于厂区的车辆数；
  - 场景里写的假 MES 行，区域号和任务类型必须是厂区里有的；
  - 场景里改假 RIoT 的车（`set-riot-vehicle`），设备号必须是厂区车辆清单里的。

和其他厂区一样，这里不放端点，也不放凭据。
