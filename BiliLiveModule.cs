using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.FunctionCaller;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace Suran.BiliLive;

public class BiliLiveConfig
{
    [DisplayName("基础·榜单最大条数")]
    [Description("BiliLiveRank/BiliLiveOverview 单次返回条数的上限，B站接口超过50会被打回20，范围1-50")]
    public int MaxCount { get; set; } = 30;

    [DisplayName("基础·请求超时(秒)")]
    [Description("单次工具调用的总超时")]
    public int RequestTimeoutSeconds { get; set; } = 30;
}

[Module("B站直播查询",
    "B站直播分区与在线人气榜：大区与子分区列表、按名称查分区、某大区/子分区的在线人气前N、各大区人气一览、某个房间或主播现在在不在播",
    defaultCategory: "苏染的工具")]
public class BiliLiveModule(
    XmlFunctionCaller functionCaller,
    ILogger<BiliLiveModule> logger,
    Interactor<BiliLiveModule> interactor
) : ChatBehaviour, IConfigurable<BiliLiveConfig>
{
    public BiliLiveConfig Configuration { get; set; } = null!;

    static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    const string LiveApiBase = "https://api.live.bilibili.com";
    const string AreaListPath = "/room/v1/Area/getList?parent_id=0";
    const string RoomListPath = "/room/v1/Area/getRoomList";
    const string RoomListV3Path = "/room/v3/area/getRoomList";
    const string RoomInfoPath = "/room/v1/Room/get_info?room_id=";
    const string MasterInfoPath = "/live_user/v1/Master/info?uid=";
    const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    const int AreaCacheSeconds = 1800;
    const int KeywordScanSize = 50;

    List<LiveParentArea> parentAreas = new();
    List<LiveSubArea> subAreas = new();
    DateTime areaTreeFetchedTime = DateTime.MinValue;

    protected override Task OnAwake()
    {
        XmlHandler handler = new(this)
        {
            Description = "B站直播分区与人气榜查询：大区/子分区列表、指定分区在线人气前N、各大区人气一览、单个房间/主播在不在播。",
            Explanation = "全部走B站直播公开接口，无需登录、无需Cookie。人气是实时的在线人气，每次查都会变。"
                + "分区既可以用ID也可以用中文名（如 BiliLiveRank(\"单机游戏\") 或 BiliLiveRank(area:\"英雄联盟\")），"
                + "记不住分区时先用 BiliLiveAreas 带关键词查。"
                + "问「XX现在在播吗/这个直播间什么状态」用 BiliLiveRoom，房间号在 https://live.bilibili.com/ 地址栏里能看到。"
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        return Task.CompletedTask;
    }

    // ============================================================
    // AI 函数
    // ============================================================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("列出B站直播的全部大区（带子分区数量与分区页链接）；给了keyword则在大区名与子分区名里搜索，返回可直接传给 BiliLiveRank 的 parentAreaId/areaId")]
    public async Task BiliLiveAreas(
        [Description("可选关键词，如 单机/英雄联盟/虚拟主播，留空表示只列大区")] string? keyword = null)
    {
        await LoadAreaTreeAsync();
        string want = (keyword ?? "").Trim();
        List<string> lines = new();
        if (want.Length == 0)
        {
            lines.Add("B站直播大区（parentAreaId 可用于 BiliLiveRank）：");
            foreach (LiveParentArea parent in parentAreas)
            {
                lines.Add("- " + parent.Name + " parentAreaId=" + parent.Id + "，子分区 " + subAreas.FindAll(s => s.ParentId == parent.Id).Count + " 个"
                    + "\n  " + AreaTagsUrl(parent.Id, "0"));
            }
            lines.Add("全站热门用 BiliLiveRank(parentArea:\"0\")。");
            interactor.Poke(string.Join("\n", lines));
            return;
        }

        List<string> matchedParents = new();
        foreach (LiveParentArea parent in parentAreas)
        {
            if (parent.Name.Contains(want, StringComparison.OrdinalIgnoreCase))
            {
                matchedParents.Add("- 大区 " + parent.Name + " → parentArea=\"" + parent.Id + "\"，链接 " + AreaTagsUrl(parent.Id, "0"));
            }
        }
        List<LiveSubArea> matchedSubs = subAreas.FindAll(s => s.Name.Contains(want, StringComparison.OrdinalIgnoreCase));
        if (matchedParents.Count == 0 && matchedSubs.Count == 0)
        {
            interactor.Poke("直播分区里没有名称含「" + want + "」的大区或子分区。可以用 BiliLiveAreas 不带参数看全部大区。");
            return;
        }
        lines.Add("搜索「" + want + "」命中的分区：");
        lines.AddRange(matchedParents);
        int shownSubs = Math.Min(20, matchedSubs.Count);
        foreach (LiveSubArea sub in matchedSubs.GetRange(0, shownSubs))
        {
            lines.Add("- " + sub.ParentName + " / " + sub.Name + " → parentArea=\"" + sub.ParentId + "\", area=\"" + sub.Id + "\""
                + "\n  " + AreaTagsUrl(sub.ParentId, sub.Id));
        }
        if (shownSubs < matchedSubs.Count)
        {
            lines.Add("… 命中 " + matchedSubs.Count + " 个子分区，只列前 20 个，可以更具体地再搜一次");
        }
        interactor.Poke(string.Join("\n", lines));
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查某大区或子分区的在线人气榜前N。参数可填ID也可填中文名；只给子分区名时会自动定位它所属的大区；两者都留空表示全站热门。sort留空/online=人气最高优先（接口自带的排序到第25条之后会抖，插件拿回来再按人气本地重排一次），new=最新开播优先（实测这个模式回的房间人气多为0，按接口顺序列、不重排）")]
    public async Task BiliLiveRank(
        [Description("大区ID或大区名，如 6 / 单机游戏；0 或留空表示全站")] string? parentArea = null,
        [Description("子分区ID或名称，如 236 / 主机游戏；0 或留空表示整个大区")] string? area = null,
        [Description("榜单条数，默认10，上限看插件配置")] int count = 10,
        [Description("排序：留空或 online=按人气从多到少，new=最新开播")] string? sort = null)
    {
        (string parentId, string areaId, string label) = await ResolveAreaAsync(parentArea ?? "", area ?? "");
        int limited = Math.Clamp(count, 1, Math.Clamp(Configuration.MaxCount, 1, 50));
        bool newestFirst = IsNewestSort(sort);
        string url = LiveApiBase + RoomListPath
            + "?parent_area_id=" + parentId + "&area_id=" + areaId + "&sort_type=" + (newestFirst ? "live_time" : "online")
            + "&page=1&page_size=" + limited;
        JsonElement data = await LiveGetAsync(url);
        List<string> lines = new();
        lines.Add("【" + label + "】" + (newestFirst ? "最新开播" : "在线人气") + " 前" + limited
            + (newestFirst ? "" : "（已按人气本地重排）") + "（实时数据，随时间变化）");
        List<JsonElement> rooms = newestFirst ? FlattenArray(data) : SortByOnline(FlattenArray(data));
        if (rooms.Count == 0)
        {
            lines.Add("（该分区当前没有返回房间，可能是暂时没人在播或接口限流，可稍后再试或换个大区）");
            lines.Add("分区页：" + AreaTagsUrl(parentId, areaId));
            interactor.Poke(string.Join("\n", lines));
            return;
        }
        int index = 0;
        foreach (JsonElement room in rooms)
        {
            index++;
            lines.Add(index + ". " + S(room, "uname") + " " + FormatOnline(N(room, "online"))
                + " · " + S(room, "area_name") + " · " + OneLine(S(room, "title"))
                + " · https://live.bilibili.com/" + S(room, "roomid"));
        }
        lines.Add("分区页：" + AreaTagsUrl(parentId, areaId));
        interactor.Poke(string.Join("\n", lines));
    }

    static bool IsNewestSort(string? sort)
    {
        string want = (sort ?? "").Trim().ToLowerInvariant();
        return want == "new" || want == "live_time" || want == "最新" || want == "开播";
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("一次给出所有大区各自的在线人气前N，用来看哪个区人多；子分区请看 BiliLiveRank")]
    public async Task BiliLiveOverview(
        [Description("每个大区列几条，默认5")] int count = 5)
    {
        await LoadAreaTreeAsync();
        int limited = Math.Clamp(count, 1, Math.Clamp(Configuration.MaxCount, 1, 50));
        List<string> lines = new();
        lines.Add("B站直播各大区在线人气前" + limited + "（实时数据，每个区内部已按人气本地重排）");
        foreach (LiveParentArea parent in parentAreas)
        {
            lines.Add("");
            string url = LiveApiBase + RoomListPath
                + "?parent_area_id=" + parent.Id + "&area_id=0&sort_type=online&page=1&page_size=" + limited;
            try
            {
                List<JsonElement> rooms = SortByOnline(FlattenArray(await LiveGetAsync(url)));
                if (rooms.Count == 0)
                {
                    lines.Add("【" + parent.Name + "】当前没有返回房间");
                    continue;
                }
                lines.Add("【" + parent.Name + "】 " + AreaTagsUrl(parent.Id, "0"));
                int index = 0;
                foreach (JsonElement room in rooms)
                {
                    index++;
                    lines.Add("  " + index + ". " + S(room, "uname") + " " + FormatOnline(N(room, "online"))
                        + " · " + S(room, "area_name"));
                }
            }
            catch (Exception areaError)
            {
                lines.Add("【" + parent.Name + "】查询失败：" + areaError.Message);
            }
        }
        interactor.Poke(string.Join("\n", lines));
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查某个直播间现在在不在播、标题与人气。source 可给房间号（长号或短号都认，实测短号6能跳到 7734200）、直播间链接、主播UID（配 asUid=true），也可给主播昵称——昵称是拿B站直播关键词搜一页再按昵称筛，只筛得到正在播的")]
    public async Task BiliLiveRoom(
        [Description("房间号、直播间链接、主播UID 或主播昵称")] string source,
        [Description("source 给的是主播UID就填 true，默认按房间号认")] bool asUid = false)
    {
        string trimmed = source.Trim();
        if (trimmed.Length == 0)
        {
            throw new Exception("没给房间号、UID或主播名");
        }
        Match linkMatch = Regex.Match(trimmed, @"live\.bilibili\.com/(?:h/)?(\d+)", RegexOptions.IgnoreCase);
        if (linkMatch.Success)
        {
            trimmed = linkMatch.Groups[1].Value;
        }

        if (IsDigits(trimmed) == false)
        {
            await ReportByNameAsync(trimmed);
            return;
        }
        string roomArg = trimmed;
        List<string> notes = new();
        JsonElement? info;
        string anchor = "";
        if (asUid)
        {
            (info, string uidError, anchor) = await ResolveByUidAsync(roomArg);
            if (info == null)
            {
                throw new Exception("UID " + roomArg + " 没查到直播间：" + uidError);
            }
            notes.Add("（按主播UID " + source.Trim() + " 定位到房间）");
        }
        else
        {
            (info, string roomError) = await FetchAsync(RoomInfoUrl(roomArg));
            if (info == null)
            {
                // 这串数字既可能是房间号也可能是UID，两种都试一遍，并把试的顺序说清楚
                (JsonElement? byUid, string uidError, string uidAnchor) = await ResolveByUidAsync(roomArg);
                if (byUid == null)
                {
                    throw new Exception("房间 " + roomArg + " 查不到（" + roomError + "），当主播UID试也不通（" + uidError + "）；"
                        + "房间号可在 https://live.bilibili.com/ 地址栏里看到");
                }
                notes.Add("（" + roomArg + " 当房间号查不到：" + roomError + "；改按主播UID查到了房间 " + S(byUid.Value, "room_id") + "）");
                info = byUid;
                anchor = uidAnchor;
            }
        }
        if (anchor.Length == 0)
        {
            anchor = await AnchorNameAsync(S(info.Value, "uid"));
        }
        List<string> lines = new();
        lines.AddRange(notes);
        lines.Add(DescribeRoomLine(info.Value, anchor));
        interactor.Poke(string.Join("\n", lines));
    }

    static string RoomInfoUrl(string roomId) => LiveApiBase + RoomInfoPath + roomId;

    static string MasterInfoUrl(string uid) => LiveApiBase + MasterInfoPath + uid;

    // 主播名没有直接查房间的免登录接口：拿 v3 的 keyWord 搜一页，再按昵称筛（实测「青铜树海」「迷瞪」都能在50条里筛到那一条，
    // 而接口回的 count 是整个分区的热度计数、不是命中数，所以不能拿它当「搜到几条」）
    static string KeywordSearchUrl(string keyword, int size) => LiveApiBase + RoomListV3Path
        + "?platform=web&parent_area_id=0&cate_id=0&area_id=0&sort_type=online&page=1&page_size=" + size
        + "&keyWord=" + Uri.EscapeDataString(keyword);

    static List<JsonElement> FilterByUname(List<JsonElement> rooms, string name) =>
        rooms.FindAll(room => S(room, "uname").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);

    // v3 这个接口把房间放在 data.list 里（榜单那个 v1 接口是直接给数组），两种形状都要认
    async Task<List<JsonElement>> KeywordRoomsAsync(string name)
    {
        JsonElement payload = await LiveGetAsync(KeywordSearchUrl(name, KeywordScanSize));
        return payload.TryGetProperty("list", out JsonElement list) ? FlattenArray(list) : FlattenArray(payload);
    }

    async Task ReportByNameAsync(string name)
    {
        List<JsonElement> rooms = await KeywordRoomsAsync(name);
        List<JsonElement> hits = FilterByUname(rooms, name);
        if (hits.Count == 0)
        {
            throw new Exception("直播关键词搜「" + name + "」这一页 " + rooms.Count + " 条里没有昵称含这个词的主播；"
                + "B站这个搜索是按标题/标签模糊匹配的，给房间号最准（或 asUid=true 配UID）");
        }
        if (hits.Count > 1)
        {
            List<string> names = hits.ConvertAll(room => S(room, "uname") + "（房间 " + S(room, "roomid") + "，人气 " + FormatOnline(N(room, "online")) + "）");
            int shown = Math.Min(8, names.Count);
            throw new Exception("昵称含「" + name + "」的在播主播有 " + hits.Count + " 个，请指明其一："
                + string.Join("、", names.GetRange(0, shown)) + (shown < names.Count ? "…" : ""));
        }
        JsonElement? info = (await FetchAsync(RoomInfoUrl(S(hits[0], "roomid")))).Data ?? hits[0];
        interactor.Poke("主播「" + name + "」：" + DescribeRoomLine(info.Value, S(hits[0], "uname"))
            + "\n（按直播关键词搜到这一条，榜上人气 " + FormatOnline(N(hits[0], "online")) + "）");
    }

    async Task<(JsonElement? Data, string Error)> FetchAsync(string url)
    {
        try
        {
            return (await LiveGetAsync(url), "");
        }
        catch (Exception fetchError)
        {
            return (null, fetchError.Message);
        }
    }

    async Task<(JsonElement? RoomInfo, string Error, string AnchorName)> ResolveByUidAsync(string uid)
    {
        (JsonElement? master, string masterError) = await FetchAsync(MasterInfoUrl(uid));
        if (master == null)
        {
            return (null, masterError, "");
        }
        string roomId = S(master.Value, "room_id");
        if (roomId.Length == 0 || roomId == "0")
        {
            return (null, "这个UID没有直播间", "");
        }
        (JsonElement? info, string error) = await FetchAsync(RoomInfoUrl(roomId));
        return (info, error, MasterName(master.Value));
    }

    // 主播昵称在 Master/info 的 data.info.uname 里，get_info 本身不给昵称
    static string MasterName(JsonElement master) =>
        master.TryGetProperty("info", out JsonElement inner) && inner.ValueKind == JsonValueKind.Object ? S(inner, "uname") : "";

    async Task<string> AnchorNameAsync(string uid)
    {
        if (uid.Length == 0)
        {
            return "";
        }
        var fetched = await FetchAsync(MasterInfoUrl(uid));
        return fetched.Data == null ? "" : MasterName(fetched.Data.Value);
    }

    string DescribeRoomLine(JsonElement info, string anchor)
    {
        long status = N(info, "live_status");
        string state = status switch
        {
            1 => "直播中",
            2 => "轮播/回放（实测这种 online 为0、live_time 是 0000-00-00 占位）",
            0 => "没在播",
            _ => "状态码 " + status
        };
        string roomId = S(info, "room_id");
        List<string> parts = new()
        {
            "直播间 " + roomId + (S(info, "short_id") is string shortId && shortId.Length > 0 && shortId != "0" ? "（短号 " + shortId + "）" : "")
                + " https://live.bilibili.com/" + roomId,
            "　" + state + " · 人气 " + FormatOnline(N(info, "online"))
                + " · 关注 " + FormatOnline(N(info, "attention"))
                + "（实测 attention 与主播资料的 follower_num 同值）"
        };
        parts.Add("　主播 " + (anchor.Length > 0 ? anchor : "（昵称没取到）")
            + (S(info, "uid").Length > 0 ? "（uid " + S(info, "uid") + "）" : "")
            + " · 分区 " + S(info, "parent_area_name") + "/" + S(info, "area_name"));
        if (S(info, "title").Length > 0)
        {
            parts.Add("　标题：" + OneLine(S(info, "title")));
        }
        if (S(info, "live_time").Length > 0 && S(info, "live_time").StartsWith("0000") == false)
        {
            parts.Add("　开播时间：" + S(info, "live_time"));
        }
        return string.Join("\n", parts);
    }

    static bool IsDigits(string text) => Regex.IsMatch(text, @"^\d+$");

    // ============================================================
    // 分区树与参数解析
    // ============================================================

    // 一次 parent_id=0 就能拿到全部大区及其子分区，比逐个大区再查少十几次请求
    async Task LoadAreaTreeAsync()
    {
        if (parentAreas.Count > 0 && (DateTime.Now - areaTreeFetchedTime).TotalSeconds < AreaCacheSeconds)
        {
            return;
        }
        JsonElement data = await LiveGetAsync(LiveApiBase + AreaListPath);
        List<LiveParentArea> newParents = new();
        List<LiveSubArea> newSubs = new();
        foreach (JsonElement parent in FlattenArray(data))
        {
            string parentId = S(parent, "id");
            string parentName = S(parent, "name");
            newParents.Add(new LiveParentArea(parentId, parentName));
            if (parent.TryGetProperty("list", out JsonElement list) == false)
            {
                continue;
            }
            foreach (JsonElement sub in FlattenArray(list))
            {
                newSubs.Add(new LiveSubArea(parentId, parentName, S(sub, "id"), S(sub, "name")));
            }
        }
        if (newParents.Count == 0)
        {
            throw new Exception("B站直播分区接口没返回任何大区，接口可能改了结构或正在限流");
        }
        parentAreas = newParents;
        subAreas = newSubs;
        areaTreeFetchedTime = DateTime.Now;
        logger.LogInformation("B站直播分区树已刷新：{Parents} 个大区，{Subs} 个子分区", newParents.Count, newSubs.Count);
    }

    async Task<(string parentId, string areaId, string label)> ResolveAreaAsync(string parentArea, string area)
    {
        await LoadAreaTreeAsync();
        string parentArg = parentArea.Trim();
        string areaArg = area.Trim();
        LiveParentArea? namedParent = null;
        if (parentArg.Length > 0 && IsSiteArg(parentArg) == false)
        {
            namedParent = parentAreas.Find(p => p.Id == parentArg
                || p.Name == parentArg
                || p.Name.Contains(parentArg, StringComparison.OrdinalIgnoreCase));
            if (namedParent == null)
            {
                // 只给一个数字时很容易把子分区ID填进 parentArea（236 这类），认出来就直接按子分区查
                LiveSubArea? mistaken = areaArg.Length == 0 || IsSiteArg(areaArg) ? subAreas.Find(s => s.Id == parentArg) : null;
                if (mistaken != null)
                {
                    return (mistaken.ParentId, mistaken.Id,
                        mistaken.ParentName + "/" + mistaken.Name + "（" + parentArg + " 是子分区ID，已按子分区查）");
                }
                throw new Exception("没有名为「" + parentArg + "」的直播大区，先用 BiliLiveAreas 看大区列表"
                    + (subAreas.Exists(s => s.Id == parentArg) ? "；" + parentArg + " 是子分区ID，要填到 area 参数里" : ""));
            }
        }

        if (areaArg.Length == 0 || IsSiteArg(areaArg))
        {
            if (namedParent == null)
            {
                return ("0", "0", "全站");
            }
            return (namedParent.Id, "0", namedParent.Name);
        }

        LiveSubArea? matched = subAreas.Find(s => s.Id == areaArg || s.Name == areaArg);
        if (matched == null)
        {
            List<LiveSubArea> candidates = subAreas.FindAll(s => (namedParent == null || s.ParentId == namedParent.Id)
                && s.Name.Contains(areaArg, StringComparison.OrdinalIgnoreCase));
            if (candidates.Count > 1)
            {
                List<string> names = candidates.ConvertAll(c => c.ParentName + "/" + c.Name + "(" + c.Id + ")");
                int shown = Math.Min(8, names.Count);
                throw new Exception("「" + areaArg + "」命中 " + names.Count + " 个子分区，请指明其一："
                    + string.Join("、", names.GetRange(0, shown)) + (shown < names.Count ? "…" : ""));
            }
            matched = candidates.Count == 1 ? candidates[0] : null;
        }
        if (matched == null)
        {
            throw new Exception("没找到子分区「" + areaArg + "」，用 BiliLiveAreas(\"" + areaArg + "\") 可以先搜出它的ID");
        }
        if (namedParent != null && namedParent.Id != matched.ParentId)
        {
            throw new Exception("「" + matched.Name + "」属于 " + matched.ParentName + "（parentArea=" + matched.ParentId + "），不属于 " + namedParent.Name);
        }
        return (matched.ParentId, matched.Id, matched.ParentName + "/" + matched.Name);
    }

    static bool IsSiteArg(string value) => value == "0" || value == "全站" || value == "全部" || value == "all";

    // ============================================================
    // 请求与取值
    // ============================================================

    // 公开接口只需浏览器UA；网络类失败重试一次，B站返回业务错误码直接抛出不重试
    async Task<JsonElement> LiveGetAsync(string url)
    {
        try
        {
            return await RequestAsync(url);
        }
        catch (Exception requestError) when (requestError is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation("B站直播接口首次请求失败，重试一次：{Message}", requestError.Message);
            return await RequestAsync(url);
        }
    }

    async Task<JsonElement> RequestAsync(string url)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.Referrer = new Uri("https://live.bilibili.com/");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(Math.Clamp(Configuration.RequestTimeoutSeconds, 5, 120)));
        using HttpResponseMessage response = await httpClient.SendAsync(request, timeout.Token);
        string body = await response.Content.ReadAsStringAsync(timeout.Token);
        using JsonDocument parsed = JsonDocument.Parse(body);
        JsonElement root = parsed.RootElement.Clone();
        long code = N(root, "code");
        if (code != 0)
        {
            string detail = S(root, "message").Length > 0 ? S(root, "message") : S(root, "msg");
            throw new Exception("B站直播接口返回 " + code + (detail.Length > 0 ? "：" + detail : ""));
        }
        return root.TryGetProperty("data", out JsonElement data) ? data.Clone() : JsonDocument.Parse("null").RootElement.Clone();
    }

    static string AreaTagsUrl(string parentId, string areaId)
        => "https://live.bilibili.com/p/eden/area-tags?parentAreaId=" + parentId + "&areaId=" + areaId;

    static string FormatOnline(long online)
        => online >= 10000
            ? (online / 10000d).ToString("0.0", CultureInfo.InvariantCulture) + "万"
            : online.ToString(CultureInfo.InvariantCulture);

    static string OneLine(string text) => Regex.Replace(text, "\\s+", " ").Trim();

    // 接口自带的 sort_type=online 到第25条之后会抖（实测一次取50条里有33个逆序对），拿回来自己按人气重排
    static List<JsonElement> SortByOnline(List<JsonElement> rooms)
    {
        List<JsonElement> sorted = new(rooms);
        sorted.Sort((left, right) => N(right, "online").CompareTo(N(left, "online")));
        return sorted;
    }

    static List<JsonElement> FlattenArray(JsonElement element)
    {
        List<JsonElement> items = new();
        if (element.ValueKind != JsonValueKind.Array)
        {
            return items;
        }
        foreach (JsonElement item in element.EnumerateArray())
        {
            items.Add(item);
        }
        return items;
    }

    // 这个接口的 id/online 有的字段是字符串有的是数字，取值统一按两种形态处理
    static string S(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out JsonElement value) == false)
        {
            return "";
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
    }

    static long N(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out JsonElement value) == false)
        {
            return 0;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
        {
            return number;
        }
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long text)
            ? text
            : 0;
    }
}

sealed record LiveParentArea(string Id, string Name);

sealed record LiveSubArea(string ParentId, string ParentName, string Id, string Name);
