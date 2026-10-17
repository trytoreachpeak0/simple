# sample：虚构的小厂区

给场景（`scenarios/`）和测试用的虚构厂区：三台车、三个区域号、两种任务类型。它不对应任何真实厂区，改它不会影响现场；场景默认都用它，这样 D8 的真实配置改动不会让一批无关的场景变红。少数"D8 冒烟"场景以后会直接用 `sites/d8/`。

`site.yaml` 里的值和两个假部件的起始数据对齐：

| 内容 | 值 | 对齐的是 |
| --- | --- | --- |
| 区域号 | `S1-1`、`S1-2`、`S2-1` | 假 MES 的种子行 `src/Simple.MesGateway/Fake/seed-rows.json` |
| 任务类型 | `SAMPLE_TO_OVEN`、`SAMPLE_TO_GATE` | 同上 |
| 车辆 | `fake-vehicle-1`～`fake-vehicle-3` 及其 RIoT 设备号 | 假 RIoT 的世界 `src/Simple.FakeRiot/seed.json` |

改了这两份种子数据中的任何一份，要同步改这里。

## 现在谁读它

服务端还不读厂区配置（导入是以后的产品票，见 `docs/adr/0017-site-config-lives-in-server.md`）。目前只有 `simple scenario validate` 和 `simple scenario run` 读它，用来检查场景：

- 场景起的车载终端数（`initial.terminals`）不能多于厂区的车辆数；
- 场景里写的假 MES 行，区域号和任务类型必须是厂区里有的；
- 场景里改假 RIoT 的车（`set-riot-vehicle`），设备号必须是厂区车辆清单里的。

和其他厂区一样，这里不放端点，也不放凭据。
