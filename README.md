# Suran.BiliLive

B站直播分区与在线人气榜查询，移植自工作区的 `bili-live-rank` 技能（Python 脚本版）：四个 LLM 工具覆盖大区列表、按名称搜分区、某大区/子分区的人气前N、各大区人气一览、**单个房间或主播现在在不在播**。全部走 B 站直播**公开 JSON 接口**，无需登录、无需 Cookie，不依赖第三方服务。

## 版本
4.1.0（对应 Alife 客户端 4.2.x）

## 依赖
- Alife.Function.FunctionCaller

## AI 函数
| 函数 | 说明 |
|---|---|
| BiliLiveAreas(keyword?) | 不带参数列出全部大区（含子分区数量与分区页链接）；带关键词则在大区名与子分区名里搜索，直接给出可用于 BiliLiveRank 的 parentArea/area |
| BiliLiveRank(parentArea?, area?, count=10, sort?) | 榜单。参数可填 ID 也可填中文名：`("单机游戏")`、`("网游","英雄联盟")`、`(area:"主机游戏")` 都行；两者都留空 = 全站热门。`sort` 留空/`online`=人气从多到少（**接口自带的排序到尾部会抖，插件拿回来再本地重排一次**），`new`=最新开播（按接口顺序列，不重排） |
| BiliLiveRoom(source, asUid=false) | 单个房间/主播的状态：在不在播、标题、两级分区、人气、关注数、开播时间、房间链接。`source` 给房间号（长号或短号都认）、直播间链接、主播UID（配 `asUid=true`）或主播昵称 |
| BiliLiveOverview(count=5) | 各大区前N一览，用来回答"哪个区人多"；每个区内部同样按人气重排 |

## 配置
| 键 | 默认 | 说明 |
|---|---|---|
| MaxCount | 30 | 单次返回条数上限，钳 1~50。实测 `page_size` 填 100 会被打回 20 条，所以上限锁 50 |
| RequestTimeoutSeconds | 30 | 单次工具调用的总超时，钳 5~120 |

## 分区参数怎么解析
`getList?parent_id=0` 一次就返回 12 个大区 + 每个大区下的全部子分区（实测 454 个），插件把它缓存成两棵树（30 分钟），所以：

- 数字参数（`6` / `236`）直接当 ID 用；
- 文字参数先精确匹配名称，再在本大区内做包含匹配；没指定大区时跨全部子分区搜，命中多个会把候选（前 8 个）报回让你指明，**不会瞎猜**；
- 子分区一旦定位成功，就用它自己的 `parent_id` 去查榜，因此 `BiliLiveRank(area:"英雄联盟")` 不需要再传大区；
- **4.1.0 起**：只给一个数字却把它填在 `parentArea` 上、而那其实是子分区ID（`BiliLiveRank("236")` 这种常见误填），插件认出来并直接按那个子分区查，回报里写明「236 是子分区ID，已按子分区查」；同时给 `area` 时会报错提示「236 是子分区ID，要填到 area 参数里」。

## 接口与实测（2026-10-15 建立，2026-10-22 随 4.1.0 复跑，反射探针 106 项）
- `GET https://api.live.bilibili.com/room/v1/Area/getList?parent_id=0` → `data` 是**大区数组**，每个元素 `{id, name, list:[子分区...]}`；大区 `id` 是数字、子分区 `id` 是字符串，取值时两种形态都要兼容
- `GET .../room/v1/Area/getRoomList?parent_area_id={pid}&area_id={aid}&sort_type=online&page=1&page_size={n}` → `data` **直接是房间数组**（不是 `data.list`），字段 `roomid/uid/title/uname/online/area_name/parent_name/cover/link/face`，`online` 为在线人气；条目里**没有 `live_time`**
- **`sort_type=online` 不是严格降序**：一次取 50 条实测有 33 个逆序对（`…2795, 3033, 2785…` 这样从第 25 条开始抖）。所以 4.1.0 拿回本地按 `online` 重排后才叫「人气前N」
- `page_size` 实测 50 给满 50 条，**填 100 会被打回 20 条**（配置上限锁 50 的依据）
- `sort_type=live_time`（最新开播）接口认，实测回 5 条、这些房间 `online` 多为 0；因为条目里没有 `live_time` 字段，插件不重排、只按接口顺序列
- `parent_area_id=0&area_id=0` 实测可用 = 全站热门，无需特殊分支
- 单个房间：`GET .../room/v1/Room/get_info?room_id={房间号|短号}` 免登录可用，给 `live_status`(0未开播/1直播中/2轮播回放) `title/online/attention/uid/area_name/parent_area_name/live_time/short_id/keyframe`；实测短号 `6` 能解析到 `room_id=7734200`，房间不存在时回 `code=1 未找到该房间`。**它不给主播昵称**
- 主播昵称与UID反查：`GET .../live_user/v1/Master/info?uid={uid}` 免登录可用，给 `info.uname/room_id/follower_num/room_news`；实测 `attention` 与 `follower_num` 同值（1126269），所以「关注数」取自哪一个都一致
- 按主播名找房间没有免登录直达接口，走 `GET .../room/v3/area/getRoomList?...&keyWord={词}`：这个接口的 `data` 是**对象**，房间在 `data.list` 里（与 v1 榜单直接给数组不同）；`data.count` 是整个分区的热度计数、**不是命中数**（实测换个词 count 都是 11804），所以插件搜一页 50 条再按昵称自己筛（实测「青铜树海」「迷瞪」都能在 50 条里筛到那一条）。筛不到就如实说没筛到、让给房间号，命中多条就把候选报回，不瞎猜
- **已下架**：`room/v1/Room/getStatusInfoByRoom` 与 `getStatusInfoByid` 都回 `code=1000003 方法...未在控制器中找到`，别再照旧文档去调
- 分区树里 `new_tags` 给出的两种排序就是 `online`（最热直播）与 `live_time`（最新开播）

## 汇报格式
- 人气统一写成「万」并注明是**在线人气**（`online` 是人气值，不是观看人数）
- 每条带房间链接 `https://live.bilibili.com/{roomid}`，榜末带分区页 `https://live.bilibili.com/p/eden/area-tags?parentAreaId={pid}&areaId={aid}`
- `live.bilibili.com/all?parentAreaId=...` 这种带参写法手机端可能打不开，一律用上面的 `area-tags` 格式
- `BiliLiveRoom` 的状态写成「直播中 / 轮播或回放 / 没在播」三种；轮播那种实测 `online=0`、`live_time` 是 `0000-00-00` 占位，所以占位时间不显示；昵称取不到时直说「昵称没取到」而不是拿 UID 顶替

## 与技能版的差异
- 技能里的大区对照表（娱乐=1、网游=2…）是硬编码的，插件改成实时拉分区树，B 站调整分区不需要改代码
- 技能脚本的 `sub <pid>` 分支按 `data` 是对象来解析（实测 v1 榜单接口 `data` 是数组），移植时按实测形状重写；4.1.0 加的按名字搜那条路走的是 v3 接口，那个**才是** `data.list` 套一层
- 请求失败（网络异常/超时）重试一次，B 站返回业务错误码直接抛出，不吞异常

## 注意事项
- 只连 B 站 HTTP 接口，不连协议端，因此与 NapCat/LLOneBot/SnowLuma 无关，任何协议端都能用
- 榜单是实时的，每次调用都会重新请求；`BiliLiveOverview` 会串行发 12 个请求，条数别设置太大
- 按主播名查只能查到**正在播**的（v3 关键词接口只回在播房间），下播了就查不到；要认人可以给房间号或 UID
- B 站这个关键词搜索是按标题/标签模糊匹配的，所以插件一定再按昵称筛一遍，不会把「人气最高的相似标题房间」当成你要的主播
