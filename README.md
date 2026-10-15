# Suran.BiliLive

B站直播分区与在线人气榜查询，移植自工作区的 `bili-live-rank` 技能（Python 脚本版）：三个 LLM 工具覆盖大区列表、按名称搜分区、某大区/子分区的人气前N、各大区人气一览。全部走 B 站直播**公开 JSON 接口**，无需登录、无需 Cookie，不依赖第三方服务。

## 版本
4.0.0（对应 Alife 客户端 4.2.x）

## 依赖
- Alife.Function.FunctionCaller

## AI 函数
| 函数 | 说明 |
|---|---|
| BiliLiveAreas(keyword?) | 不带参数列出全部大区（含子分区数量与分区页链接）；带关键词则在大区名与子分区名里搜索，直接给出可用于 BiliLiveRank 的 parentArea/area |
| BiliLiveRank(parentArea?, area?, count=10) | 在线人气榜。参数可填 ID 也可填中文名：`("单机游戏")`、`("网游","英雄联盟")`、`(area:"主机游戏")` 都行；两者都留空 = 全站热门 |
| BiliLiveOverview(count=5) | 各大区前N一览，用来回答"哪个区人多" |

## 分区参数怎么解析
`getList?parent_id=0` 一次就返回 12 个大区 + 每个大区下的全部子分区（实测 452 个），插件把它缓存成两棵树（30 分钟），所以：

- 数字参数（`6` / `236`）直接当 ID 用；
- 文字参数先精确匹配名称，再在本大区内做包含匹配；没指定大区时跨全部子分区搜，命中多个会把候选（前 8 个）报回让你指明，**不会瞎猜**；
- 子分区一旦定位成功，就用它自己的 `parent_id` 去查榜，因此 `BiliLiveRank(area:"英雄联盟")` 不需要再传大区。

## 接口与实测（2026-10-15 本机 curl 核对）
- `GET https://api.live.bilibili.com/room/v1/Area/getList?parent_id=0` → `data` 是**大区数组**，每个元素 `{id, name, list:[子分区...]}`；大区 `id` 是数字、子分区 `id` 是字符串，取值时两种形态都要兼容
- `GET .../room/v1/Area/getRoomList?parent_area_id={pid}&area_id={aid}&sort_type=online&page=1&page_size={n}` → `data` **直接是房间数组**（不是 `data.list`），字段 `roomid/uid/uname/title/online/area_name`，`online` 为在线人气
- `page_size` 实测 50 给满 50 条，**填 100 会被打回 20 条**，所以配置项上限锁在 50
- `parent_area_id=0&area_id=0` 实测可用 = 全站热门，无需特殊分支
- 实测 12 个大区当下都能返回满额榜单；技能文档提到个别分区（绘画/知识/音乐类）可能给空数组，插件遇到空就如实报"当前没有返回房间"，不编数据

## 汇报格式
- 人气统一写成「万」并注明是**在线人气**（`online` 是人气值，不是观看人数）
- 每条带房间链接 `https://live.bilibili.com/{roomid}`，榜末带分区页 `https://live.bilibili.com/p/eden/area-tags?parentAreaId={pid}&areaId={aid}`
- `live.bilibili.com/all?parentAreaId=...` 这种带参写法手机端可能打不开，一律用上面的 `area-tags` 格式

## 与技能版的差异
- 技能里的大区对照表（娱乐=1、网游=2…）是硬编码的，插件改成实时拉分区树，B 站调整分区不需要改代码
- 技能脚本的 `sub <pid>` 分支按 `data` 是对象来解析（实测该接口 `data` 是数组），移植时按实测形状重写
- 请求失败（网络异常/超时）重试一次，B 站返回业务错误码直接抛出，不吞异常

## 注意事项
- 只连 B 站 HTTP 接口，不连协议端，因此与 NapCat/LLOneBot/SnowLuma 无关，任何协议端都能用
- 榜单是实时的，每次调用都会重新请求；`BiliLiveOverview` 会串行发 12 个请求，条数别设置太大
