// Ludus Lunae · 火山的女儿插件：文字化的「状态 + 能做的事」
//
// 给 LLM 玩用。按游戏系统把画面翻成文字，命令用名字不用编号。只在 selfplay 开着时由 SelfPlay 调用。
// 数值都从游戏数据读（DataSys.dataCfg、SelectBtn / SelectCell、Course），能借游戏自己的私有方法算的就借
// （MapSys.BtnEnergy / Request / ShowCondition / ResCoin，CourseSys.EachPrg / CourseAddNature），不从界面上扒字。
// 点东西照样走 Unity 的事件派发（SelfPlay.Press），和鼠标点一样：灰的按钮（UiBtn.enabled 为 false）照样点不动。
//   look    现在在哪、开着什么窗口、这里每个物体能做什么（行动力、金币、加什么、本月还能几次、做不了的原因）、能去哪
//   acts    本月所有地点能做的事，一张表
//   plan    日程：已排了什么、能排的课（花费、每次加什么、进度、心情）、能约谁出去玩
//   find / go / touch / pick   vp_player 的 do 用：按名字找物体和活动、去地点、点物体、选活动
//   course / uncourse / yes / no   排课、撤课、回答确认框
// 另外几件自动的事（Auto）：月中过程页的「下一页」和结算后的「进入下月」自动点；开场之类的演出只有一个「继续」时自动点；
// touch 离得远的物体时人先走过去，到了再点开。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

static class TextPlay
{
    // ---- 游戏的私有方法和字段 ----
    static readonly MethodInfo mBtnEnergy = AccessTools.Method(typeof(MapSys), "BtnEnergy");
    static readonly MethodInfo mRequest = AccessTools.Method(typeof(MapSys), "Request");
    static readonly MethodInfo mShowCondition = AccessTools.Method(typeof(MapSys), "ShowCondition");
    static readonly MethodInfo mResCoin = AccessTools.Method(typeof(MapSys), "ResCoin");
    static readonly FieldInfo fFood = AccessTools.Field(typeof(MapSys), "food");
    static readonly FieldInfo fDessert = AccessTools.Field(typeof(MapSys), "dessert");
    static readonly FieldInfo fCanClick = AccessTools.Field(typeof(mapBtn), "canClick");
    static readonly MethodInfo mEachPrg = AccessTools.Method(typeof(CourseSys), "EachPrg");
    static readonly MethodInfo mCourseAdd = AccessTools.Method(typeof(CourseSys), "CourseAddNature");
    static readonly MethodInfo mCou = AccessTools.Method(typeof(CourseSys), "Cou");
    static readonly MethodInfo mDownMood = AccessTools.Method(typeof(CourseSys), "DownMood");
    static readonly FieldInfo fTempCourse = AccessTools.Field(typeof(CourseSys), "tempCourse");
    static readonly FieldInfo fOwnCourses = AccessTools.Field(typeof(CourseSys), "ownCourses");
    static readonly FieldInfo fPlanType = AccessTools.Field(typeof(CourseSys), "planType");
    static readonly FieldInfo fCouPage = AccessTools.Field(typeof(CourseSys), "tempCouPage");
    static readonly FieldInfo fMetPersons = AccessTools.Field(typeof(NpcSys), "metPersons");
    static readonly FieldInfo fTempStory = AccessTools.Field(typeof(CourseSys), "tempStory");
    static readonly FieldInfo fDauWish = AccessTools.Field(typeof(TaskSys), "dauWish");
    static readonly FieldInfo fDauWishTime = AccessTools.Field(typeof(TaskSys), "dauWishTime");
    static readonly FieldInfo fFinishedDauWish = AccessTools.Field(typeof(TaskSys), "finishedDauWish");
    static readonly FieldInfo fDauWishScore = AccessTools.Field(typeof(TaskSys), "dauWishScore");
    static readonly FieldInfo fExploPaths = AccessTools.Field(typeof(ExploSys), "paths");
    static readonly FieldInfo fExploLevel = AccessTools.Field(typeof(ExploSys), "level");
    static readonly FieldInfo fExploPathType = AccessTools.Field(typeof(ExploSys), "pathType");
    static readonly FieldInfo fShowingWork = AccessTools.Field(typeof(DauSys), "showingWork");
    static readonly FieldInfo fWorrying = AccessTools.Field(typeof(DauSys), "isWorrying");

    static DataConfig Cfg { get { return DataSys.Instan.dataCfg; } }
    static Transform Ui(int i) { return UiSys.Instan.uiTrans[i]; }

    static string T(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try
        {
            var t = ChatSys.Txt(s);
            return SelfPlay.Clean(string.IsNullOrEmpty(t) ? s : t, 40);
        }
        catch { return SelfPlay.Clean(s, 40); }
    }

    static string Signed(int n) { return n > 0 ? "+" + n : n.ToString(); }

    // MapSys 的好几个私有方法按 MapSys.tempBtn 取数据：临时换成要问的物体，问完换回来（同一帧里，不碰别的状态）
    static TR WithBtn<TR>(SelectBtn sb, System.Func<TR> f)
    {
        var saved = MapSys.tempBtn;
        MapSys.tempBtn = sb;
        try { return f(); }
        finally { MapSys.tempBtn = saved; }
    }

    // ================= 地点和物体 =================
    internal static string PlaceName(int p)
    {
        if (p == (int)Place.faRoom) return "爸爸的房间";
        try
        {
            var maps = Cfg.maps;
            if (p >= 0 && p < maps.Length)
            {
                var n = T(maps[p].chName);
                if (n != "") return n;
            }
        }
        catch { }
        return ((Place)p).ToString();
    }

    static int Here { get { return (int)MapSys.tempPlace; } }

    // 地图上现在亮着的地点（地图窗口的子物体顺序就是 Place 的编号）
    static List<int> MapPlaces()
    {
        var list = new List<int>();
        try
        {
            var btns = Ui(5).Find("panel").Find("btn");
            for (int i = 0; i < btns.childCount - 1; i++)
                if (btns.GetChild(i).gameObject.activeSelf) list.Add(i);
        }
        catch { }
        return list;
    }

    static string PlaceGain(int p)
    {
        try
        {
            var parts = new List<string>();
            foreach (var k in Cfg.maps[p].addNature) parts.Add(k == 4 ? "-道德" : "+" + Cfg.natureCh[k]);
            return string.Join(" ", parts.ToArray());
        }
        catch { return ""; }
    }

    // 女儿的名字：存档里起的那个，和图像版的 TextPlay.Dau 一样（开局能给女儿改名，不一定叫露丝）
    internal static string Dau
    {
        get
        {
            try { var n = SelfPlay.Clean(DauSys.Instan.dauName, 20); return n == "" ? "女儿" : n; }
            catch { return "女儿"; }
        }
    }

    // 剧情锁：第 6 回合没去过遗忘森林时，TaskSys.ClickBtn 除了 forest 一律挡下（露丝说「还是先去遗忘森林探险吧！」）
    internal static string StoryLock()
    {
        try
        {
            if (DataSys.Turn() == 6 && !TaskSys.CheckTask(TaskSys.TaskEnu.firstExplo))
                return Dau + "一心想去遗忘森林探险，这个月别的事她都没心思做。（go 遗忘森林，再 do 探险）";
        }
        catch { }
        return null;
    }

    static string MapClosedWhy()
    {
        if (Here == (int)Place.faRoom) return "月初在爸爸的房间，先 go 家（见女儿）";
        if (Active(22) || Active(23)) return "在探险里";
        int m = DataSys.Month();
        if (m == Constant.birdMonth) return "今天是小鸟节：点「安排」（或 plan / schedule）会问要不要去庆典，去了就在庆典上";
        if (m == Constant.volcanoMonth) return "今天是火山节：点「安排」（或 plan / schedule）会问要不要去节日会场";
        return "游戏现在没给地图按钮（可能有剧情或窗口挡着）";
    }

    static bool MapAvailable()
    {
        try
        {
            var b = UiSys.BtnTs(UiSys.UiSysBtn.mapBtn);
            return b != null && b.gameObject.activeInHierarchy;
        }
        catch { return false; }
    }

    static List<mapBtn> ObjectsAt(int place)
    {
        var list = new List<mapBtn>();
        foreach (var mb in MapSys.Instan.tempBtns)
            if (mb != null && mb.data != null && mb.tempPlace == place && mb.gameObject.activeSelf) list.Add(mb);
        return list;
    }

    static readonly HashSet<string> specialObjs = new HashSet<string>
    {
        "wishingPond", "mathCompete", "birdDayDial", "paintingCompete", "fishingCompete", "diceCompete",
        "alchemy", "theatre", "racino", "chooseActor"
    };

    // 点了这个物体会发生什么（和 MapSys.CheckCanOpen 的分支一样）
    static string ObjKind(SelectBtn sb)
    {
        switch (sb.type)
        {
            case "person": case "npc": return "talk";
            case "once": case "show": return "chat";
            case "job": return "job";
            case "shop": return "shop";
        }
        if (sb.enName == "food0" || sb.enName == "food1") return "res";
        if (sb.enName == "bath") return "work";
        if (specialObjs.Contains(sb.enName)) return "special";
        return "select";
    }

    static string KindWord(string kind)
    {
        switch (kind)
        {
            case "talk": return "说话";
            case "chat": return "看看（剧情）";
            case "job": return "问问";
            case "shop": return "商店";
            case "work": return "打工窗口";
            case "special": return "打开";
        }
        return "";
    }

    static string ObjName(SelectBtn sb) { return T(sb.chName); }

    static bool CanClick(mapBtn mb)
    {
        try { return fCanClick == null || (bool)fCanClick.GetValue(mb); }
        catch { return true; }
    }

    internal static bool Far(mapBtn mb) { return !CanClick(mb); }

    // ================= 活动（SelectCell） =================
    internal class Cell
    {
        public SelectBtn btn;
        public SelectCell sc;
        public string name = "";
        public int energy, coin;
        public List<string> gains = new List<string>();
        public bool can = true;
        public string why = "", note = "";

        public string Line(string prefix = "")
        {
            var cost = new List<string>();
            if (energy < 0) cost.Add("行动力 " + energy);
            if (coin < 0) cost.Add("金币 " + coin);
            var s = prefix + name + "：" + (cost.Count > 0 ? string.Join("，", cost.ToArray()) : "不花行动力");
            if (gains.Count > 0) s += " → " + string.Join("，", gains.ToArray());
            if (note != "") s += "（" + note + "）";
            if (!can) s = "×" + s + " —— " + why;
            return s;
        }
    }

    static bool IsFood(SelectCell sc) { return sc.request == "food" || sc.request == "dessert"; }

    internal static Cell Describe(SelectCell sc, SelectBtn owner = null)
    {
        var sb = sc.btn ?? owner;
        var ms = MapSys.Instan;
        var c = new Cell { btn = sb, sc = sc, name = T(sc.chName) };
        c.energy = (int)mBtnEnergy.Invoke(ms, new object[] { sc });
        c.coin = IsFood(sc) ? -WithBtn(sb, () => (int)mResCoin.Invoke(ms, new object[] { sc.enName })) : sc.coin;
        if (c.energy > 0) c.gains.Add("行动力 +" + c.energy);
        if (c.coin > 0) c.gains.Add("金币 +" + c.coin);
        if (sc.mood != 0) c.gains.Add("心情 " + Signed(sc.mood));
        bool winter = (sb.enName == "row" || sb.enName == "easel" || sb.enName == "bookshelf" || sb.enName == "listenStory")
            && sc.enName != "easelpainting" && DataSys.Stage() == 3;
        var nat = DauSys.MotivationNature(sc.natures);
        for (int i = 0; i < nat.Length; i++)
            if (nat[i] != 0) c.gains.Add(Cfg.natureCh[i] + " " + Signed(winter ? nat[i] * 2 : nat[i]));
        if (sc.inAttri != null)
            for (int j = 0; j < sc.inAttri.Length && j < Constant.inAttriCh.Length; j++)
                if (sc.inAttri[j] != 0) c.gains.Add(Constant.inAttriCh[j] + " " + Signed(sc.inAttri[j]));
        if (sc.favor > 0) c.gains.Add("好感 +" + sc.favor);
        if (sc.fame != 0) c.gains.Add("声望 " + Signed(sc.fame));
        if (sc.inspiration > 0) c.gains.Add("感悟 +" + sc.inspiration);
        if (sc.randomNature > 0) c.gains.Add("随机一项素质 +" + DauSys.MotivationNature(sc.randomNature));
        if (sc.darkness != 0) c.gains.Add("道德 ↓");
        if (sc.turnClickLimit > 0) c.note = "本月还能 " + Math.Max(0, sc.turnClickLimit - sc.turnClickCount) + " 次";
        if (!string.IsNullOrEmpty(sc.request) && !IsFood(sc)) c.note = (c.note == "" ? "" : c.note + "，") + "条件：" + T(sc.request);

        if (c.energy > 0 && DataSys.energy + c.energy > DataSys.MaxEnergy())
        {
            int waste = DataSys.energy + c.energy - DataSys.MaxEnergy();
            c.note = (c.note == "" ? "" : c.note + "，") + "现在做会溢出 " + Math.Min(waste, c.energy) + " 点（行动力最多 " + DataSys.MaxEnergy() + "）";
        }
        c.can = WithBtn(sb, () => (bool)mRequest.Invoke(ms, new object[] { sc.enName }));
        if (!c.can) c.why = Why(sc, c);
        else if ((sb.enName == "stump" || sb.enName == "bed") && DataSys.energy == DataSys.MaxEnergy())
        {
            c.can = false;
            c.why = "行动力是满的，用不着休息";
        }
        return c;
    }

    static string Why(SelectCell sc, Cell c)
    {
        if (c.energy < 0 && DataSys.energy < -c.energy) return "行动力不够（要 " + (-c.energy) + "，剩 " + DataSys.energy + "）";
        if (sc.coin < 0 && DataSys.Coin() < -sc.coin) return "金币不够（要 " + (-sc.coin) + "，有 " + DataSys.Coin() + "）";
        if (sc.turnClickLimit > 0 && sc.turnClickCount >= sc.turnClickLimit) return "这个月的次数用完了";
        try
        {
            if (sc.request == "food" && (bool)fFood.GetValue(MapSys.Instan)) return "这个月吃过主食了";
            if (sc.request == "dessert" && (bool)fDessert.GetValue(MapSys.Instan)) return "这个月吃过甜点了";
        }
        catch { }
        if (sc.enName == "teacher0") return "声望不够，或者没有能教的课";
        return "现在做不了";
    }

    static List<Cell> CellsOf(SelectBtn sb)
    {
        var list = new List<Cell>();
        var kind = ObjKind(sb);
        if (kind != "select" && kind != "res" && kind != "work") return list;
        foreach (var kv in sb.selectsDic)
        {
            try
            {
                if (kind == "select" && !WithBtn(sb, () => (bool)mShowCondition.Invoke(MapSys.Instan, new object[] { kv.Key }))) continue;
                list.Add(Describe(kv.Value, sb));
            }
            catch (Exception e) { LudusLunaePlugin.Log.LogWarning("读活动 " + kv.Key + " 失败：" + e.Message); }
        }
        return list;
    }

    // 按钮清单用：活动面板里的一格（名字是 SelectCell.enName）、餐厅里的一道菜
    internal static string CellLine(string cellName)
    {
        try
        {
            var sb = MapSys.tempBtn;
            SelectCell sc;
            if (sb != null && sb.selectsDic.TryGetValue(cellName, out sc)) return Describe(sc, sb).Line();
        }
        catch { }
        return "";
    }

    static string ObjectBlock(mapBtn mb, string indent, bool withCells)
    {
        var sb = mb.data;
        var kind = ObjKind(sb);
        var head = indent + ObjName(sb);
        if (sb.type == "item" && sb.level >= 0) head += "（Lv." + (sb.level + 1) + (string.IsNullOrEmpty(sb.expStr) ? "" : "，熟练 " + sb.expStr) + "）";
        var cells = CellsOf(sb);
        if (cells.Count == 0 || !withCells) return head + (KindWord(kind) == "" ? "" : "：" + KindWord(kind));
        var s = new StringBuilder(head).Append(kind == "res" ? "（餐厅）" : kind == "work" ? "（打工）" : "").Append('：');
        foreach (var c in cells) s.Append('\n').Append(indent).Append("  · ").Append(c.Line());
        return s.ToString();
    }

    // 场景里的人：好感、没见过的第一次说话有初次见面的剧情
    static string PersonText(SelectBtn sb)
    {
        var name = ObjName(sb);
        try
        {
            var per = NpcSys.GetNpc(sb.data);
            var met = (List<string>)fMetPersons.GetValue(NpcSys.Instan);
            return name + "（好感 " + per.favor + (met != null && !met.Contains(per.enName) ? "，没见过，第一次说话有初次见面的剧情" : "") + "）";
        }
        catch { return name; }
    }

    // 女儿的心愿：幼年是爸爸的许诺（TaskSys.UpdateDauWishWnd 那些字），之后是领主的任务
    internal static string WishText()
    {
        try
        {
            var ts = TaskSys.Instan;
            if (DataSys.Stage() > 0)
                return "爸爸的目标：" + Constant.faTargetCh[ts.faWish] + "，评分 " + ts.FaTotalScore() + "/100。女儿的心愿（领主的任务）点「女儿的心愿」图标看。";
            int wish = (int)fDauWish.GetValue(ts);
            var time = (int[])fDauWishTime.GetValue(ts);
            var done = (bool[])fFinishedDauWish.GetValue(ts);
            var score = (int[])fDauWishScore.GetValue(ts);
            int sum = 0;
            for (int i = 0; i < score.Length; i++) sum += i == score.Length - 1 ? -score[i] : score[i];
            var s = new StringBuilder("爸爸的许诺：" + Constant.dauWishCh[wish] + "（" + Constant.dauWishNatureCh[1 + wish] + " + " + Constant.dauWishNatureCh[0] + "），"
                + "还剩 " + Math.Max(0, 5 - DataSys.Turn()) + " 回合，评分 " + sum + "/" + Constant.maxDauWishScore + "。许诺里的事：");
            var parts = new List<string>();
            for (int j = 0; j < time.Length && j < Cfg.dauWishCh.GetLength(0); j++)
            {
                int need;
                int.TryParse(Cfg.dauWishCh[j, 1], out need);
                var line = SelfPlay.Clean(Cfg.dauWishCh[j, 0], 60);
                if (done[j]) line += "（完成了）";
                else if (time[j] >= need) line += "（" + time[j] + "/" + need + "，够了：在家 click 女儿的心愿，再点「领取奖励」）";
                else line += "（" + time[j] + "/" + need + "）";
                parts.Add(line);
            }
            return s.Append(string.Join("；", parts.ToArray())).Append('。').ToString();
        }
        catch (Exception e) { return "（读心愿出错：" + e.Message + "）"; }
    }

    // 对话选项的效果：能从数据里查到的写上（现在是睡前故事：CourseSys.tempStory）
    internal static string OptionEffect(string label)
    {
        try
        {
            if (!CourseSys.isReadingStory) return "";
            var stories = fTempStory.GetValue(CourseSys.Instan) as System.Collections.IList;
            if (stories == null) return "";
            foreach (var o in stories)
            {
                var st = o as Course;
                if (st == null) continue;
                var name = T(st.chName);
                if (name == "" || !label.Contains(name)) continue;
                var parts = new List<string>();
                for (int i = 0; i < st.inAttri.Length && i < Constant.inAttriCh.Length; i++)
                    if (st.inAttri[i] != 0) parts.Add(Constant.inAttriCh[i] + " " + Signed(st.inAttri[i]));
                for (int i = 0; i < st.natures.Length && i < Cfg.natureCh.Length; i++)
                    if (st.natures[i] != 0) parts.Add(Cfg.natureCh[i] + " " + Signed(st.natures[i]));
                if (st.moral != 0) parts.Add("道德 " + Signed(st.moral));
                if (st.favor != 0) parts.Add("好感 " + Signed(st.favor));
                return string.Join("，", parts.ToArray());
            }
        }
        catch { }
        return "";
    }

    // 主界面上的图标按钮（UiSys.btnTrans，下标是 UiSysBtn）翻成中文
    static readonly Dictionary<string, string> sysBtnZh = new Dictionary<string, string>
    {
        { "furniture", "家具" }, { "chatDau", "和女儿聊天" }, { "penletter", "笔友来信" }, { "farmBtn", "农场" },
        { "cookingBtn", "做饭" }, { "catchHorse", "抓马" }, { "exploBtn", "探险" }, { "letter", "信" }, { "news", "报纸" },
        { "bill", "账本" }, { "stable", "马厩" }, { "lord", "领主" }, { "reviewBtn", "回顾" }, { "danceBtn", "舞会" },
        { "fogAlley", "雾巷" }, { "rogueKnight", "流浪骑士" }, { "mapBtn", "地图（go 地点）" }, { "faRoom", "爸爸的房间" },
        { "wishBtn", "女儿的心愿（wish 看）" }, { "workshop", "工坊" }, { "wardrobe", "衣柜" }, { "masterCourse", "大师课" },
        { "fishingBtn", "钓鱼" }, { "travelBtn", "旅行" }, { "crystal", "水晶" }, { "homework", "作业" }, { "planBtn", "日程安排（plan）" },
    };

    internal static string IconName(GameObject go)
    {
        try
        {
            var bt = UiSys.Instan.btnTrans;
            for (int i = 0; i < bt.Length; i++)
            {
                if (bt[i] == null || !(go.transform == bt[i] || go.transform.IsChildOf(bt[i]))) continue;
                var key = ((UiSys.UiSysBtn)i).ToString();
                string zh;
                return sysBtnZh.TryGetValue(key, out zh) ? zh : key;
            }
        }
        catch { }
        return "";
    }

    internal static string FaWorkName(string index)
    {
        try
        {
            var shown = (List<int>)fShowingWork.GetValue(DauSys.Instan);
            return T(Cfg.faWorks[shown[int.Parse(index)]].chName);
        }
        catch { return index; }
    }

    // 女儿有心事时，地图、做事都会被挡（DauSys.CheckIsDauWorrying 弹「露丝好像有心事」）
    internal static string WorryNote()
    {
        try
        {
            if ((bool)fWorrying.GetValue(DauSys.Instan)) return Dau + "好像有心事，闷闷的不说话。先去问问她吧（click 女儿的心事），不然她出门、做什么都没心思。";
        }
        catch { }
        return null;
    }

    internal static string LordName(string index)
    {
        try { return T(Cfg.lords[int.Parse(index)].chName); }
        catch { return index; }
    }

    // 悬停会改游戏状态的按钮不去探提示：战斗里（选好的技能会被取消）、选领主、骰子、选成员 / 武器 / 技能 / 物品、舞伴
    static readonly HashSet<int> hoverChangesState = new HashSet<int> { 8, 9, 15, 16, 25, 26, 27, 44, 57, 66, 142 };

    internal static bool ProbeSafe(GameObject go)
    {
        try
        {
            if (FightSys.fightPhase != FightSys.FightPhase.endFight) return false;
            var ub = go.GetComponent<UiBtn>();
            if (ub == null) return true;
            if (ub.type == BtnType.dice) return false;
            return !(ub.type == BtnType.index && hoverChangesState.Contains(ub.index));
        }
        catch { return true; }
    }

    // 开着的窗口里的字（题目、信、事件结果……）：清单里所有按钮共同的那层窗口下面的字，去掉按钮自己的字
    static string WindowText()
    {
        try
        {
            var items = SelfPlay.Snapshot;
            Transform common = null;
            var labels = new HashSet<string>();
            foreach (var it in items)
            {
                if (it.go == null || IsGlobalUi(it.go.transform)) continue;
                if (it.label != "") labels.Add(it.label);
                common = common == null ? it.go.transform.parent : CommonAncestor(common, it.go.transform);
                if (common == null) return "";
            }
            if (common == null || common.parent == null) return "";
            for (int i = 0; i < UiSys.Instan.sceneTrans.Length; i++)
                if (UiSys.Instan.sceneTrans[i] != null && (common == UiSys.Instan.sceneTrans[i] || UiSys.Instan.sceneTrans[i].IsChildOf(common))) return "";
            var lines = new List<string>();
            foreach (var row in SelfPlay.ReadTexts(common).Split('\n'))
            {
                var r = row.Trim();
                if (r == "" || labels.Contains(r)) continue;
                lines.Add(r);
            }
            var text = string.Join(" ｜ ", lines.ToArray());
            return text.Length > 500 ? text.Substring(0, 500) + "…" : text;
        }
        catch { return ""; }
    }

    static bool IsGlobalUi(Transform t)
    {
        try
        {
            foreach (var b in UiSys.Instan.btnTrans) if (b != null && (t == b || t.IsChildOf(b))) return true;
            return t.IsChildOf(Ui(0)) || t.IsChildOf(Ui(36));
        }
        catch { return false; }
    }

    static Transform CommonAncestor(Transform a, Transform b)
    {
        for (var x = a; x != null; x = x.parent)
            if (b.IsChildOf(x)) return x;
        return null;
    }

    // ================= 窗口 =================
    static bool Active(int ui) { try { return Ui(ui).gameObject.activeInHierarchy; } catch { return false; } }

    internal static bool SureOpen()
    {
        try { return Active(16) && Ui(16).Find("panel").Find("btn").gameObject.activeSelf; }
        catch { return false; }
    }

    static string SureText()
    {
        try { return SelfPlay.Clean(Ui(16).Find("panel").Find("txt").GetComponent<Text>().text, 200); }
        catch { return ""; }
    }




    static bool SelectOpen()
    {
        try { return Active(4) && Ui(4).Find("panel").Find("choice").gameObject.activeSelf; }
        catch { return false; }
    }

    static bool Doing()
    {
        try { return Active(4) && Ui(4).Find("panel").Find("doing").gameObject.activeSelf; }
        catch { return false; }
    }

    static bool ResOpen() { return Active(62); }
    static bool MapOpen() { return Active(5); }

    static bool PlanOpen()
    {
        try { return Active(1) && fPlanType.GetValue(CourseSys.Instan).ToString() == "normal"; }
        catch { return false; }
    }

    static string OpenWindowText()
    {
        if (SureOpen()) return "确认框：「" + SureText() + "」→ yes 确认 / no 取消";
        if (Doing()) return "正在做：" + T(MapSys.tempBtn == null ? "" : MapSys.tempBtn.chName);
        if (SelectOpen()) return SelectPanelText();
        if (ResOpen()) return ResPanelText();
        if (PlanOpen()) return PlanText();
        if (Active(23)) return ExploText();
        if (Active(22)) return ExploMapText();
        if (MapOpen()) return "地图开着（go 地点）。" + ExitsText();
        var wish = WishWndText();
        if (wish != "") return wish;
        if (Active(3)) return "月中过程在播（自动往下翻）";
        return "";
    }

    // 「女儿的心愿」图标开的窗口：幼年是爸爸的许诺（uiTrans[50] 的 panel/target，TaskSys.OpenDauWishWnd），之后是领主面板
    // （uiTrans[46]，TaskSys.OpenWishWnd）。窗口后面的家还在按钮清单上，WindowText 认不出这是窗口，
    // 不单独认的话回话只写「在家」，要再 screen 才看得到「领取奖励」
    static string WishWndText()
    {
        try
        {
            if (Active(50) && Ui(50).Find("panel").Find("target").gameObject.activeInHierarchy)
                return "心愿窗口开着。" + WishText() + "\n够了的那件事点「领取奖励」（click 按钮上的字）；看完把窗口关掉。";
            if (Active(46))
            {
                var lines = new List<string>();
                foreach (var row in SelfPlay.ReadTexts(Ui(46).Find("panel")).Split('\n'))
                    if (row.Trim() != "") lines.Add(row.Trim());
                var text = string.Join(" ｜ ", lines.ToArray());
                return "领主面板开着（女儿的心愿、领主的任务）。窗口里的字：" + (text.Length > 500 ? text.Substring(0, 500) + "…" : text);
            }
        }
        catch { }
        return "";
    }

    static readonly Dictionary<string, string> placeTypeZh = new Dictionary<string, string>
    {
        { "random", "随机事件" }, { "relic", "遗迹" }, { "spring", "泉水（回血）" }, { "village", "村庄" }, { "treasure", "宝箱" },
        { "normalEne", "普通敌人" }, { "headEne", "精英敌人" }, { "boss", "首领" }, { "start", "起点" },
    };

    // 探险里：地点、层数、两条路上各有什么（ExploSys.paths），路线按钮（path/0、path/1）上写的字
    static string ExploText()
    {
        var s = new StringBuilder("探险中：");
        try
        {
            var name = Ui(23).Find("placeName").GetComponent<Text>();
            s.Append(name == null ? "" : SelfPlay.Clean(name.text, 40));
            var paths = fExploPaths.GetValue(ExploSys.Instan) as System.Collections.IList;
            int num = (int)fExploLevel.GetValue(ExploSys.Instan) % 4;
            int pathType = (int)fExploPathType.GetValue(ExploSys.Instan);
            var pathTs = Ui(23).Find("path");
            if (pathTs.gameObject.activeInHierarchy)
            {
                s.Append("。下一步（click 按钮上的字）：");
                for (int i = 0; i < pathTs.childCount; i++)
                {
                    var ch = pathTs.GetChild(i);
                    if (!ch.gameObject.activeSelf) continue;
                    string next;
                    if (num == 0) next = "下一个是普通敌人，打完再分两条路：" + PathZh(paths, 0) + "；或 " + PathZh(paths, 1);
                    else if (num == 1) next = "这条路：" + PathZh(paths, i);
                    else if (num == 2) next = "下一个：" + PointZh(paths, pathType, 1);
                    else next = "走完这一段，到下一段的起点";
                    s.Append("\n  · 「").Append(SelfPlay.LabelOf(ch.gameObject)).Append("」：").Append(next);
                }
            }
            else s.Append("。战斗、事件的按钮看下面的清单；看不懂的界面用 shot 截图。");
        }
        catch (Exception e) { s.Append("（读探险出错：" + e.Message + "）"); }
        return s.ToString();
    }

    static string PointZh(System.Collections.IList paths, int path, int i)
    {
        try
        {
            var list = paths[path] as System.Collections.IList;
            var t = list[i].ToString();
            string zh;
            return placeTypeZh.TryGetValue(t, out zh) ? zh : t;
        }
        catch { return "?"; }
    }

    static string PathZh(System.Collections.IList paths, int path)
    {
        try
        {
            var list = paths[path] as System.Collections.IList;
            var parts = new List<string>();
            for (int i = 0; i < list.Count; i++) parts.Add(PointZh(paths, path, i));
            return string.Join(" → ", parts.ToArray());
        }
        catch { return "?"; }
    }

    // 选探险地点：开放的写推荐难度，锁着的写打通哪里才开
    static string ExploMapText()
    {
        var s = new StringBuilder("探险地图（click 地点名；选好再「出发探险」）：");
        try
        {
            var points = Ui(22).Find("panel").Find("points");
            for (int i = 0; i < points.childCount && i < Cfg.exploMaps.Length; i++)
            {
                var ch = points.GetChild(i);
                var map = Cfg.exploMaps[i];
                var ub = ch.GetComponent<UiBtn>();
                bool open = ub != null && ub.enabled;
                s.Append("\n  · ").Append(T(map.chName));
                if (open)
                {
                    var lv = ch.Find("level").Find("num").GetComponent<Text>();
                    s.Append("：难度 ").Append(lv == null ? "?" : SelfPlay.Clean(lv.text, 10)).Append("，打通过 ").Append(map.finishedCount).Append(" 次");
                }
                else
                {
                    var tip = ch.Find("lock").Find("tip").GetComponent<Text>();
                    s.Append("（锁着：").Append(tip == null ? "" : SelfPlay.Clean(tip.text, 40)).Append('）');
                }
            }
            if (Ui(22).Find("chooseMap").gameObject.activeInHierarchy)
                s.Append("\n地点说明开着：").Append(SelfPlay.ReadTexts(Ui(22).Find("chooseMap")).Replace("\n", " ｜ "));
        }
        catch (Exception e) { s.Append("（读探险地图出错：" + e.Message + "）"); }
        return s.ToString();
    }

    static string SelectPanelText()
    {
        var sb = MapSys.tempBtn;
        var s = new StringBuilder("活动面板「" + (sb == null ? "?" : ObjName(sb)) + "」（pick 活动名；点外面关掉）：");
        try
        {
            var cells = Ui(4).Find("panel").Find("choice").Find("cells");
            for (int i = 0; i < cells.childCount; i++)
            {
                var ch = cells.GetChild(i);
                if (!ch.gameObject.activeSelf) continue;
                SelectCell sc;
                if (sb != null && sb.selectsDic.TryGetValue(ch.name, out sc)) s.Append("\n  · ").Append(Describe(sc, sb).Line());
                else
                {
                    var label = SelfPlay.LabelOf(ch.gameObject);
                    if (label != "") s.Append("\n  · ").Append(label);
                }
            }
        }
        catch (Exception e) { s.Append("\n（读面板出错：" + e.Message + "）"); }
        return s.ToString();
    }

    static string ResPanelText()
    {
        var sb = MapSys.tempBtn;
        var s = new StringBuilder("餐厅「" + (sb == null ? "?" : ObjName(sb)) + "」（pick 菜名）：");
        try
        {
            var choice = Ui(62).Find("panel").Find("choice");
            for (int i = 0; i < choice.childCount; i++)
            {
                var ch = choice.GetChild(i);
                SelectCell sc;
                if (!ch.gameObject.activeSelf || sb == null || !sb.selectsDic.TryGetValue(ch.name, out sc)) continue;
                var c = Describe(sc, sb);
                var ub = ch.GetComponent<UiBtn>();
                if (ub != null && !ub.enabled && c.can) { c.can = false; c.why = "这个月吃过了"; }
                var dishes = SelfPlay.LabelOf(ch.gameObject);
                s.Append("\n  · ").Append(c.Line()).Append(dishes == "" ? "" : "（" + dishes + "）");
            }
        }
        catch (Exception e) { s.Append("\n（读餐厅出错：" + e.Message + "）"); }
        return s.ToString();
    }

    static string ExitsText()
    {
        if (!MapAvailable() && !MapOpen()) return "现在打不开地图：" + MapClosedWhy() + "。";
        var places = MapPlaces();
        var parts = new List<string>();
        foreach (var p in places)
            if (p != Here) parts.Add(PlaceName(p) + (PlaceGain(p) == "" ? "" : " " + PlaceGain(p)));
        var s = parts.Count == 0 ? "地图上还没有能去的地方。" : "能去（go 地点，路上不花行动力）：" + string.Join("、", parts.ToArray()) + "。";
        var closed = ClosedPlaces();
        if (closed != "") s += "还没开放：" + closed + "。";
        return s;
    }

    // 地图上还没亮的地点和开放的回合（MapData.openTurn）
    static string ClosedPlaces()
    {
        var parts = new List<string>();
        try
        {
            var btns = Ui(5).Find("panel").Find("btn");
            int turn = DataSys.Turn();
            for (int i = 0; i < btns.childCount - 1 && i < Cfg.maps.Length; i++)
            {
                if (btns.GetChild(i).gameObject.activeSelf) continue;
                int open = Cfg.maps[i].openTurn;
                if (open > turn && open < Constant.totalTurn) parts.Add(PlaceName(i) + "（第 " + open + " 回合，还有 " + (open - turn) + " 个月）");
            }
        }
        catch { }
        return string.Join("、", parts.ToArray());
    }

    static string Header()
    {
        int turn = DataSys.Turn();
        return DataSys.Year() + " " + DataSys.Month() + "月（第 " + turn + " 回合，还有 " + (Constant.totalTurn - turn) + " 回合成年）  "
            + "行动力 " + DataSys.energy + "/" + DataSys.MaxEnergy() + "（下个月重新回满，留不到下个月）  金币 " + DataSys.Coin() + "  心情 " + DauSys.Mood();
    }

    // ================= look / acts / view =================
    internal static string Look()
    {
        var s = new StringBuilder(Header()).Append('\n');
        int here = Here;
        var lockWhy = StoryLock();
        if (lockWhy != null) s.Append("【剧情】").Append(lockWhy).Append('\n');
        var worry = WorryNote();
        if (worry != null) s.Append("【心事】").Append(worry).Append('\n');
        s.Append("在：").Append(PlaceName(here)).Append('\n');
        var win = OpenWindowText();
        if (win != "") s.Append(win).Append('\n');
        if (here == (int)Place.faRoom)
        {
            s.Append("月初在爸爸的房间。go 家（就是「见女儿」）回到家里，才能做事和排日程（plan）。\n");
            return s.ToString().TrimEnd();
        }
        var objs = ObjectsAt(here);
        var people = new List<string>();
        var rest = new List<string>();
        var others = new List<string>();
        foreach (var mb in objs)
        {
            var kind = ObjKind(mb.data);
            if (kind == "talk") people.Add(PersonText(mb.data));
            else if (kind == "select" || kind == "res" || kind == "work")
            {
                var block = ObjectBlock(mb, "  ", true);
                if (CellsOf(mb.data).Count == 0) others.Add(ObjName(mb.data));
                else rest.Add(block);
            }
            else if (kind == "chat" && mb.data.type == "once" && mb.data.chatDic != "battle")
                others.Add(ObjName(mb.data) + "（看看" + (mb.data.turnClickCount == 0 ? "；本月第一次看 感悟 +1" : "；本月看过了") + "）");
            else others.Add(ObjName(mb.data) + "（" + KindWord(kind) + "）");
        }
        if (rest.Count > 0) s.Append("这里有（do 物体.活动）：\n").Append(string.Join("\n", rest.ToArray())).Append('\n');
        if (people.Count > 0) s.Append("在这里的人：").Append(string.Join("、", people.ToArray())).Append("（do 名字 打个招呼，不花行动力）\n");
        if (others.Count > 0) s.Append("别的：").Append(string.Join("、", others.ToArray())).Append('\n');
        var extra = SceneButtons(here);
        if (extra.Count > 0) s.Append("场景里的按钮（do 按钮上的字）：").Append(string.Join("、", extra.ToArray())).Append('\n');
        if (here == (int)Place.home) s.Append("这个月的课（plan 看，schedule 排）：上课不花行动力，每节课心情 ").Append(Signed(DownMood())).Append("。\n");
        if (here == (int)Place.home) s.Append(WishText()).Append('\n');
        s.Append(ExitsText());
        return s.ToString().TrimEnd();
    }

    static int DownMood()
    {
        try { return (int)mDownMood.Invoke(CourseSys.Instan, new object[0]); }
        catch { return DataSys.Stage() == 0 ? -5 : -10; }
    }

    // 场景里不是物体（mapBtn）的按钮：遗忘森林的「探险」「石碑」之类。只认场景自己的 panel 下面、开着的、有字的
    static List<string> SceneButtons(int place)
    {
        var list = new List<string>();
        var objs = SceneButtonObjects(place);
        foreach (var go in objs) list.Add(SelfPlay.LabelOf(go));
        return list;
    }

    static List<GameObject> SceneButtonObjects(int place)
    {
        var list = new List<GameObject>();
        try
        {
            var panel = UiSys.SceneTs(place).Find("panel");
            foreach (var mb in panel.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!(mb is IPointerClickHandler) || mb is sceneMove || mb is mapBtn || !mb.gameObject.activeSelf) continue;
                if (mb.GetComponentInParent<mapBtn>(true) != null || list.Contains(mb.gameObject)) continue;
                var label = SelfPlay.LabelOf(mb.gameObject);
                if (label != "" && mb.gameObject.transform.parent != null && mb.gameObject.transform.parent.gameObject.activeSelf) list.Add(mb.gameObject);
            }
        }
        catch { }
        return list;
    }

    // 本月能去的地方（含家和现在所在）
    static List<int> ReachablePlaces()
    {
        var list = new List<int>();
        int here = Here;
        if (here != (int)Place.faRoom) list.Add(here);
        if (!list.Contains(0)) list.Add(0);
        foreach (var p in MapPlaces()) if (!list.Contains(p)) list.Add(p);
        return list;
    }

    internal static string Acts()
    {
        var s = new StringBuilder("这个月能去的地方、能做的事（行动力 " + DataSys.energy + "/" + DataSys.MaxEnergy() + "，留不到下个月；do 物体.活动，不在这里会先走过去）：");
        var lockWhy = StoryLock();
        if (lockWhy != null) s.Append("\n【剧情】").Append(lockWhy);
        if (!MapAvailable() && Here != (int)Place.faRoom) s.Append("\n【地图】现在打不开地图：").Append(MapClosedWhy()).Append("。");
        foreach (var p in ReachablePlaces())
        {
            var lines = new List<string>();
            var people = new List<string>();
            foreach (var mb in ObjectsAt(p))
            {
                var kind = ObjKind(mb.data);
                if (kind == "talk") { people.Add(ObjName(mb.data)); continue; }
                foreach (var c in CellsOf(mb.data)) lines.Add("  " + c.Line(ObjName(mb.data) + "."));
            }
            foreach (var label in SceneButtons(p)) lines.Add("  按钮：" + label);
            if (lines.Count == 0 && people.Count == 0) continue;
            s.Append("\n【").Append(PlaceName(p)).Append('】');
            if (p == Here) s.Append("（在这里）");
            else if (p == 0 && Here == (int)Place.faRoom) s.Append("（见女儿后就在家）");
            var gain = PlaceGain(p);
            if (gain != "" && p != 0) s.Append(' ').Append(gain);
            foreach (var l in lines) s.Append('\n').Append(l);
            if (people.Count > 0) s.Append("\n  人：").Append(string.Join("、", people.ToArray()));
        }
        s.Append("\n课程（plan）不花行动力，每节课心情 ").Append(Signed(DownMood())).Append("。");
        return s.ToString();
    }

    // 这段说明是哪一种：scene 场景（有 look 可看）、panel 插件读得懂的窗口、window 读不懂的窗口（工具这时把按钮清单也打出来）
    internal static string ViewKind = "scene";

    // state.json 里的一小段说明：wait 时打印
    internal static string View()
    {
        try
        {
            if (DataSys.Instan == null || MapSys.Instan == null || UiSys.Instan == null) return "";
            var win = OpenWindowText();
            var wtext = WindowText();
            var warn = wtext.Contains("不可更改") || wtext.Contains("无法更改") ? "⚠ 这一步选了就不能改。" : "";
            if (win != "")
            {
                ViewKind = SureOpen() || SelectOpen() || ResOpen() || PlanOpen() || MapOpen() ? "panel" : "window";
                return warn + win + (ViewKind == "window" && wtext != "" ? "\n窗口里的字：" + wtext : "");
            }
            if (wtext != "") { ViewKind = "window"; return warn + "窗口里的字：" + wtext; }
            ViewKind = "scene";
            int here = Here;
            var lockWhy = StoryLock() ?? WorryNote();
            if (here == (int)Place.faRoom) return (lockWhy == null ? "" : "【剧情】" + lockWhy + "\n") + "在爸爸的房间（月初）。go 家 = 见女儿，回家做事、排日程。";
            if (here < 0 || here > 11) return "";
            var acts = new List<string>();
            var people = new List<string>();
            foreach (var mb in ObjectsAt(here))
            {
                if (ObjKind(mb.data) == "talk") { people.Add(ObjName(mb.data)); continue; }
                foreach (var c in CellsOf(mb.data))
                    if (c.can && (c.energy != 0 || c.coin != 0)) acts.Add(ObjName(mb.data) + "." + c.name + " " + (c.energy < 0 ? "行动力" + c.energy : c.energy > 0 ? "行动力+" + c.energy : "金币" + c.coin));
            }
            var s = new StringBuilder((lockWhy == null ? "" : "【剧情】" + lockWhy + "\n") + "在" + PlaceName(here) + "。行动力 " + DataSys.energy + "/" + DataSys.MaxEnergy() + "。");
            var extra = SceneButtons(here);
            if (acts.Count > 0) s.Append("这里能做：").Append(string.Join("、", acts.ToArray())).Append("。");
            if (people.Count > 0) s.Append("人：").Append(string.Join("、", people.ToArray())).Append("。");
            if (extra.Count > 0) s.Append("按钮：").Append(string.Join("、", extra.ToArray())).Append("。");
            s.Append(ExitsText()).Append("（look 看详情，acts 看全部地点，plan 看日程）");
            return s.ToString();
        }
        catch (Exception e) { return "（读不出说明：" + e.Message + "）"; }
    }

    // ================= 日程 =================
    static Course Cou(string name) { return (Course)mCou.Invoke(CourseSys.Instan, new object[] { name }); }

    static List<string> TempCourse() { return (List<string>)fTempCourse.GetValue(CourseSys.Instan); }

    static string Natures(int[] arr)
    {
        var parts = new List<string>();
        for (int i = 0; i < arr.Length && i < Cfg.natureCh.Length; i++)
            if (arr[i] != 0) parts.Add(Cfg.natureCh[i] + " " + Signed(arr[i]));
        return string.Join("，", parts.ToArray());
    }

    static int CourseMood(Course cou) { return cou.isActivity ? cou.mood : DownMood(); }

    internal static string CourseLine(string name)
    {
        try
        {
            var cou = Cou(name);
            var cs = CourseSys.Instan;
            var gain = Natures((int[])mCourseAdd.Invoke(cs, new object[] { cou.enName, false }));
            int attri = cou.inAttri != null && cou.inAttriType < cou.inAttri.Length ? cou.inAttri[cou.inAttriType] : 0;
            if (attri != 0) gain += (gain == "" ? "" : "，") + Constant.inAttriCh[cou.inAttriType] + " " + Signed(attri);
            var s = new StringBuilder(T(cou.chName)).Append("：每节 ").Append(gain == "" ? "（无）" : gain)
                .Append("，心情 ").Append(Signed(CourseMood(cou)));
            if (cou.cost != 0) s.Append("，金币 ").Append(Signed(cou.cost));
            if (cou.prg >= 100) return "×" + T(cou.chName) + " —— 学完了";
            int each = (int)mEachPrg.Invoke(cs, new object[] { cou.enName });
            s.Append("；进度 ").Append(cou.prg).Append('%');
            if (cou.tempPrg > 0) s.Append("（本月已排 +").Append(cou.tempPrg).Append("%）");
            s.Append("，每节 +").Append(each).Append('%');
            var best = new List<string>();
            for (int i = 0; i < cou.natures.Length; i++)
                if (cou.natures[i] > 0)
                {
                    int now = DauSys.NatureNum(i);
                    best.Add(Cfg.natureCh[i] + " " + cou.natures[i] + "（现在 " + now + (now >= cou.natures[i] ? "，够了" : "，差 " + (cou.natures[i] - now)) + "）");
                }
            if (best.Count > 0) s.Append("；最佳属性 ").Append(string.Join("、", best.ToArray()));
            if (cou.prg + cou.tempPrg >= 100) s.Append(" —— 本月再排就学完了");
            return s.ToString();
        }
        catch (Exception e) { return name + "（读课程出错：" + e.Message + "）"; }
    }

    // 计划窗口里能约出去玩的人：UiBtn 42，名字是 father 或村民的英文名
    static List<UiBtn> GoOutBtns()
    {
        var list = new List<UiBtn>();
        try
        {
            foreach (var ub in Ui(1).GetComponentsInChildren<UiBtn>(false))
                if (ub.type == BtnType.index && ub.index == 42) list.Add(ub);
        }
        catch { }
        return list;
    }

    static string GoOutName(string enName)
    {
        if (enName == "father") return DauSys.Instan.faName;
        try { return NpcSys.GetNpc(enName).chName; } catch { return enName; }
    }

    static List<string> OwnCourses() { return (List<string>)fOwnCourses.GetValue(CourseSys.Instan); }

    internal static string PlanText()
    {
        var s = new StringBuilder();
        try
        {
            var temp = TempCourse();
            var names = new List<string>();
            int coin = 0, mood = 0;
            var sum = new int[Constant.natureNum];
            foreach (var n in temp)
            {
                var cou = Cfg.courseDic[n];
                names.Add(T(cou.chName));
                coin += cou.cost;
                mood += CourseMood(cou);
                if (!cou.isActivity)
                {
                    var add = (int[])mCourseAdd.Invoke(CourseSys.Instan, new object[] { n, false });
                    for (int i = 0; i < sum.Length && i < add.Length; i++) sum[i] += add[i];
                }
            }
            s.Append("日程（已排 ").Append(temp.Count).Append('/').Append(Constant.courseNum).Append("）：")
             .Append(names.Count == 0 ? "空" : string.Join("、", names.ToArray()));
            if (temp.Count > 0)
            {
                s.Append("\n  合计：").Append(Natures(sum));
                s.Append("，心情 ").Append(Signed(mood));
                if (coin != 0) s.Append("，金币 ").Append(Signed(coin));
                s.Append("（现在金币 ").Append(DataSys.Coin()).Append("，心情 ").Append(DauSys.Mood()).Append('）');
            }
            int stage = DataSys.Stage();
            var own = OwnCourses();
            s.Append("\n能排的课（course 课名，一节占一格）：");
            if (stage == 0)
                foreach (var n in own) s.Append("\n  · ").Append(CourseLine(n));
            else
                for (int page = 0; page < Constant.natureNum; page++)
                {
                    var group = new List<string>();
                    foreach (var n in own)
                    {
                        try { if (Cou(n).type / 2 == page) group.Add(n); } catch { }
                    }
                    if (group.Count == 0) continue;
                    s.Append("\n  【").Append(Cfg.natureCh[page]).Append("类】");
                    foreach (var n in group) s.Append("\n  · ").Append(CourseLine(n));
                }
            var outs = new List<string>();
            foreach (var ub in GoOutBtns()) outs.Add(GoOutName(ub.name) + (ub.enabled ? "" : "（本月约过了）"));
            if (outs.Count > 0) s.Append("\n出去玩（也占一格，不是课；心情 +10，好感 +3）：").Append(string.Join("、", outs.ToArray()));
            if (!PlanOpen()) s.Append("\n（日程窗口没开着：course / schedule 会自己打开，要在家里）");
            else if (SureOpen()) s.Append("\n确认框：「").Append(SureText()).Append("」→ yes 开始这个月 / no 清空重排");
            else s.Append("\nuncourse 撤掉最后一格。排满 5 格会弹确认框。");
        }
        catch (Exception e) { s.Append("（读日程出错：" + e.Message + "）"); }
        return s.ToString();
    }

    // ================= 名字匹配 =================
    static int Score(string have, string want)
    {
        if (string.IsNullOrEmpty(have) || string.IsNullOrEmpty(want)) return 0;
        if (have == want) return 3;
        if (have.Contains(want)) return 2;
        if (want.Contains(have)) return 1;
        return 0;
    }

    static string[] Split(string text)
    {
        return (text ?? "").Trim().Split(new[] { '.', '·', '．', '。', '/', '：', ':' }, StringSplitOptions.RemoveEmptyEntries);
    }

    internal class Target
    {
        public int place;
        public mapBtn obj;
        public string button;  // 场景里不是物体的按钮（探险、石碑、烹饪……）：按钮上的字
        public Cell cell;
        public int score;
        public string Label { get { return obj != null ? ObjName(obj.data) : button; } }
        public string Name { get { return PlaceName(place) + "." + Label + (cell == null ? "" : "." + cell.name); } }
    }

    static List<Target> AllTargets()
    {
        var list = new List<Target>();
        foreach (var p in ReachablePlaces())
            foreach (var mb in ObjectsAt(p))
            {
                list.Add(new Target { place = p, obj = mb });
                foreach (var c in CellsOf(mb.data)) list.Add(new Target { place = p, obj = mb, cell = c });
            }
        foreach (var p in ReachablePlaces())
            foreach (var label in SceneButtons(p)) list.Add(new Target { place = p, button = label });
        return list;
    }

    // 「物体.活动」「地点.物体.活动」「活动」「物体」都认；同名时优先这里
    static List<Target> Match(string text)
    {
        var seg = Split(text);
        var best = new List<Target>();
        int top = 0;
        foreach (var t in AllTargets())
        {
            string place = PlaceName(t.place), obj = t.Label, cell = t.cell == null ? null : t.cell.name;
            int sc = 0;
            if (seg.Length >= 3)
            {
                if (cell == null) continue;
                int a = Score(place, seg[0]), b = Score(obj, seg[1]), c = Score(cell, seg[2]);
                if (a > 0 && b > 0 && c > 0) sc = a + b + c + 3;
            }
            else if (seg.Length == 2)
            {
                if (cell != null)
                {
                    int b = Score(obj, seg[0]), c = Score(cell, seg[1]);
                    if (b > 0 && c > 0) sc = b + c + 3;
                }
                else
                {
                    int a = Score(place, seg[0]), b = Score(obj, seg[1]);
                    if (a > 0 && b > 0) sc = a + b;
                }
            }
            else if (seg.Length == 1)
            {
                sc = cell != null ? Score(cell, seg[0]) * 2 : Score(obj, seg[0]) * 2 + 1;
                if (cell != null && Score(cell, seg[0]) == 0) sc = 0;
                if (cell == null && Score(obj, seg[0]) == 0) sc = 0;
            }
            if (sc == 0) continue;
            if (t.place == Here) sc += 1;
            t.score = sc;
            if (sc > top) { top = sc; best.Clear(); }
            if (sc == top) best.Add(t);
        }
        return best;
    }

    // ================= 点东西 =================
    static GameObject pendingGo;
    static System.Func<bool> pendingReady;
    static float pendingUntil;
    static string pendingWhat = "";

    static void SetPending(GameObject go, System.Func<bool> ready, float seconds, string what)
    {
        pendingGo = go;
        pendingReady = ready;
        pendingUntil = Time.unscaledTime + seconds;
        pendingWhat = what;
    }

    // 有东西在动（走路、换地方、做事、演出），不算轮到 agent
    internal static string Busy()
    {
        try
        {
            if (pendingGo != null) return pendingWhat;
            if (Active(52)) return "换地方";
            if (Doing()) return "在做事";
        }
        catch { }
        return null;
    }

    internal static bool IsSkipButton(GameObject go)
    {
        try { return go.transform.IsChildOf(Ui(49).Find("skipBtn")); }
        catch { return false; }
    }

    static readonly Dictionary<int, float> autoSeen = new Dictionary<int, float>();

    // 每 0.25 秒：等着点的东西、不用做决定的翻页
    internal static void Auto(float now, bool idle)
    {
        if (pendingGo != null)
        {
            bool ready = false;
            try { ready = pendingGo.activeInHierarchy && pendingReady(); } catch { }
            if (ready)
            {
                var go = pendingGo;
                pendingGo = null;
                SelfPlay.Press(go);
                SelfPlay.Acted(pendingWhat);
            }
            else if (now > pendingUntil || pendingGo == null)
            {
                SelfPlay.Event("note", Json.Field("text", "没等到：" + pendingWhat));
                // 去地点没去成（剧情挡下、被别的窗口盖住）：把自己打开的地图关回去，免得一直开着挡在那里
                if (pendingWhat.StartsWith("去") && MapOpen()) MapSys.Instan.MapBtn(false);
                pendingGo = null;
            }
            return;
        }
        if (!idle || SureOpen()) { autoSeen.Clear(); return; }
        GameObject next = null;
        string what = null, pageText = "";
        float delay = 0.8f;
        try
        {
            // 庆祝窗口（课程完成、状态、成就）：把标题、说明、奖励记成一条事件，再点「确认」
            if (Active(29))
                foreach (var b in Ui(29).GetComponentsInChildren<Button>(false))
                {
                    for (int i = 0; i < b.onClick.GetPersistentEventCount() && next == null; i++)
                        if (b.onClick.GetPersistentMethodName(i) == "CloseCelebrateWnd") { next = b.gameObject; what = "庆祝窗口：确认"; delay = 1.2f; }
                    if (next != null) break;
                }
            // 月中过程：「下一页」和结算后的「进入下月」
            if (next == null && Active(3))
                foreach (var b in Ui(3).GetComponentsInChildren<Button>(false))
                {
                    if (!b.IsInteractable()) continue;
                    for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                    {
                        var m = b.onClick.GetPersistentMethodName(i);
                        if (m == "StartProcessPage") { next = b.gameObject; what = "过程页：下一页"; }
                        else if (m == "EndProcessBtn") { next = b.gameObject; what = "结算完，进入下月"; delay = 2f; }
                    }
                    if (next != null) break;
                }
            // 结局的下一页、后日谈的下一页、生日贺卡的下一张：没有选择，记下这一页的字再翻
            if (next == null)
                foreach (var it in SelfPlay.Snapshot)
                {
                    if (it.go == null || !it.go.activeInHierarchy) continue;
                    var ub = it.go.GetComponent<UiBtn>();
                    if (ub == null || !ub.enabled || ub.type != BtnType.index || !(ub.index == 70 || ub.index == 39 || ub.index == 114)) continue;
                    next = it.go;
                    what = ub.index == 114 ? "生日贺卡：下一张" : "下一页";
                    pageText = SelfPlay.ReadTexts(PageRoot(it.go));
                    delay = Mathf.Clamp(1.5f + pageText.Length * 0.04f, 2f, 8f);
                    break;
                }
            // 演出（开场之类）只有一个「继续」按钮
            if (next == null && Active(49))
            {
                var cg = Ui(49).Find("cg").Find("btn");
                if (cg.gameObject.activeInHierarchy)
                    foreach (var mb in cg.GetComponentsInChildren<MonoBehaviour>(false))
                        if (mb is IPointerClickHandler && mb.isActiveAndEnabled) { next = mb.gameObject; what = "演出：继续"; delay = 1.5f; break; }
            }
        }
        catch { }
        if (next == null) { autoSeen.Clear(); return; }
        Vector2 pt;
        if (!SelfPlay.Reachable(next, out pt)) return;
        int id = next.GetInstanceID();
        float since;
        if (!autoSeen.TryGetValue(id, out since)) { autoSeen.Clear(); autoSeen[id] = now; return; }
        if (now - since < delay) return;
        autoSeen.Clear();
        if ((what == "下一页" || what == "生日贺卡：下一张") && pageText != "") SelfPlay.Event("story", Json.Field("text", pageText));
        if (what == "庆祝窗口：确认")
        {
            var text = SelfPlay.ReadTexts(Ui(29).Find("panel"));
            if (text != "") SelfPlay.Event("celebrate", Json.Field("text", text));
        }
        if (what == "结算完，进入下月")
        {
            var text = SelfPlay.ReadTexts(Ui(3).Find("panel").Find("result"));
            if (text != "") SelfPlay.Event("month-result", Json.Field("text", text));
        }
        SelfPlay.Press(next);
        SelfPlay.Acted(what);
    }

    // 翻页按钮所在的那一页：往上找到有不少字的那一层
    static Transform PageRoot(GameObject go)
    {
        var t = go.transform.parent;
        for (int i = 0; i < 4 && t != null && t.parent != null; i++)
        {
            if (t.GetComponentsInChildren<Text>(false).Length >= 3) return t;
            t = t.parent;
        }
        return go.transform.parent;
    }

    internal static void WalkThenPress(mapBtn mb)
    {
        var name = ObjName(mb.data);
        SelfPlay.Press(mb.gameObject);
        SetPending(mb.gameObject, () => CanClick(mb), 10f, "走到" + name + "跟前");
        SelfPlay.Acted("走过去");
    }

    internal static int ScheduledCount()
    {
        try { return TempCourse().Count; } catch { return -1; }
    }

    internal static string OpenPlan()
    {
        var busy = Busyness();
        if (busy != null) return busy;
        string err;
        return EnsurePlan(out err) ? null : err;
    }

    // ================= 命令 =================
    internal static string Busyness()
    {
        if (pendingGo != null) return "还在" + pendingWhat;
        // 先看选项：选项开着时 chatQueue 里常常还排着话，ChatBusy 也是真；反过来查的话第一次只会回「对话还在播」
        if (SelfPlay.MenuOpen()) return "对话选项开着，先 choose（wait 看选项）";
        if (SelfPlay.ChatBusy()) return "对话还在播（wait 等它播完）";
        return null;
    }

    internal static string Find(string text, out string fields)
    {
        fields = "";
        var found = Match(text);
        if (found.Count == 0) return "没找到「" + text + "」。acts 看本月能做的事（写法：物体.活动，或者只写活动名）";
        if (found.Count > 1)
        {
            var names = new List<string>();
            foreach (var t in found) if (names.Count < 8) names.Add(t.Name);
            return "「" + text + "」对得上好几个：" + string.Join("、", names.ToArray()) + "。写全一点（地点.物体.活动）";
        }
        var f = found[0];
        fields = Json.Field("place", f.place) + "," + Json.Field("placeName", PlaceName(f.place)) + ","
            + Json.Field("obj", f.Label) + "," + Json.Field("cell", f.cell == null ? "" : f.cell.name) + ","
            + Json.Field("here", f.place == Here) + "," + Json.Field("kind", f.obj != null ? ObjKind(f.obj.data) : "button") + ","
            + Json.Field("can", f.cell == null || f.cell.can) + "," + Json.Field("why", f.cell == null ? "" : f.cell.why);
        return null;
    }

    internal static string Go(string text, out bool again)
    {
        again = false;
        var busy = Busyness();
        if (busy != null) return busy;
        var want = (text ?? "").Trim();
        if (SureOpen()) return "确认框开着：「" + SureText() + "」，先 yes / no";
        int target = -1, top = 0;
        var cands = new List<int>(MapPlaces());
        if (!cands.Contains(0)) cands.Add(0);
        foreach (var p in cands)
        {
            int sc = Score(PlaceName(p), want);
            if (want == "家" || want == "回家" || want == "见女儿") sc = p == 0 ? 3 : 0;
            if (sc > top) { top = sc; target = p; }
        }
        if (target < 0) return "去不了「" + want + "」。" + ExitsText();
        var lockWhy = StoryLock();
        if (lockWhy != null && target != (int)Place.forest && Here != (int)Place.faRoom) return lockWhy;
        if (Active(22) || Active(23)) return "在探险里，先打完或者离开探险";
        int here = Here;
        if (target == here) return "已经在" + PlaceName(target) + "了";
        if (here == (int)Place.faRoom)
        {
            // 月初在爸爸房间：先「见女儿」回家
            var see = SelfPlay.FindClickable(go => SelfPlay.LabelOf(go).Contains("见女儿"));
            if (see == null) return "在爸爸的房间，但找不到「见女儿」按钮（screen 看看）";
            SelfPlay.Press(see);
            SelfPlay.Acted("见女儿，回家");
            again = target != 0;
            return "见女儿，回家" + (again ? "；到家后再去" + PlaceName(target) : "");
        }
        if (SelectOpen() || ResOpen() || PlanOpen()) return "先关掉开着的窗口（" + OpenWindowText().Split('\n')[0] + "）";
        if (!MapAvailable() && !MapOpen()) return "现在打不开地图";
        if (!MapPlaces().Contains(target) && target != 0) return PlaceName(target) + " 这个月去不了。" + ExitsText();
        if (!MapOpen()) MapSys.Instan.MapBtn(true);  // 和点「地图」一样：露丝有心事时会弹对话、地图不开
        if (!MapOpen()) { SelfPlay.Acted("地图没打开"); return "地图没打开（" + Dau + "可能有心事，wait 看看）"; }
        var btn = Ui(5).Find("panel").Find("btn").GetChild(target).gameObject;
        SetPending(btn, () => { Vector2 pt; return SelfPlay.Reachable(btn, out pt); }, 3f, "去" + PlaceName(target));
        SelfPlay.Acted("打开地图");
        return "去" + PlaceName(target);
    }

    internal static string Touch(string text)
    {
        var busy = Busyness();
        if (busy != null) return busy;
        if (SureOpen()) return "确认框开着：「" + SureText() + "」，先 yes / no";
        if (SelectOpen() || ResOpen() || PlanOpen() || MapOpen()) return "先关掉开着的窗口（" + OpenWindowText().Split('\n')[0] + "）";
        if (Active(22) || Active(23)) return "在探险里：用 click 按钮上的字";
        int here = Here;
        var want = (text ?? "").Trim();
        var lockWhy = StoryLock();
        mapBtn best = null;
        int top = 0;
        foreach (var mb in ObjectsAt(here))
        {
            int sc = Score(ObjName(mb.data), want);
            if (sc < 2) continue;  // 只认名字对得上或者包含要找的字，免得长按钮名里碰巧含着物体名就点错
            if (sc > top) { top = sc; best = mb; }
        }
        if (best == null)
        {
            // 场景里的按钮（探险、石碑……）
            foreach (var go in SceneButtonObjects(here))
                if (Score(SelfPlay.LabelOf(go), want) >= 2)
                {
                    var label = SelfPlay.LabelOf(go);
                    SelfPlay.Press(go);
                    SelfPlay.Acted("点了" + label);
                    return "点了「" + label + "」";
                }
            foreach (var t in Match(want))
                if (t.cell == null) return "「" + want + "」在" + PlaceName(t.place) + "，先 go " + PlaceName(t.place) + "（或者直接 do）";
            return "这里没有「" + want + "」。look 看这里有什么";
        }
        var name = ObjName(best.data);
        if (lockWhy != null) return lockWhy;
        var mbRef = best;
        if (!CanClick(best))
        {
            SelfPlay.Press(best.gameObject);  // 离得远：游戏会让人先走过去
            SetPending(best.gameObject, () => CanClick(mbRef), 10f, "走到" + name + "跟前");
            SelfPlay.Acted("走过去");
            return "走到" + name + "跟前，到了自动点开";
        }
        SelfPlay.Press(best.gameObject);
        SelfPlay.Acted("点了" + name);
        var kind = KindWord(ObjKind(best.data));
        return "点了" + name + (kind == "" ? "" : "（" + kind + "）");
    }

    internal static string Pick(string text)
    {
        var busy = Busyness();
        if (busy != null) return busy;
        var want = (text ?? "").Trim();
        Transform parent;
        if (SelectOpen()) parent = Ui(4).Find("panel").Find("choice").Find("cells");
        else if (ResOpen()) parent = Ui(62).Find("panel").Find("choice");
        else return "没有开着的活动面板（先 touch 物体，或者直接 do 物体.活动）";
        var sb = MapSys.tempBtn;
        Transform best = null;
        Cell bestCell = null;
        string bestLabel = "";
        int top = 0;
        for (int i = 0; i < parent.childCount; i++)
        {
            var ch = parent.GetChild(i);
            if (!ch.gameObject.activeSelf) continue;
            SelectCell sc;
            Cell c = null;
            string label;
            if (sb != null && sb.selectsDic.TryGetValue(ch.name, out sc)) { c = Describe(sc, sb); label = c.name; }
            else label = SelfPlay.LabelOf(ch.gameObject);
            int s = Score(label, want);
            if (s > top) { top = s; best = ch; bestCell = c; bestLabel = label; }
        }
        if (best == null) return "面板里没有「" + want + "」。" + OpenWindowText();
        var ub = best.GetComponent<UiBtn>();
        if (ub != null && !ub.enabled)
            return "「" + bestLabel + "」是灰的：" + (bestCell != null && !bestCell.can ? bestCell.why : "现在选不了");
        var before = bestCell == null ? "" : bestCell.Line();
        SelfPlay.Press(best.gameObject);
        SelfPlay.Acted("选了" + bestLabel);
        return "选了 " + (before == "" ? bestLabel : before);
    }

    static bool EnsurePlan(out string err)
    {
        err = null;
        if (PlanOpen()) return true;
        if (SureOpen()) { err = "确认框开着：「" + SureText() + "」，先 yes / no"; return false; }
        if (Here != (int)Place.home) { err = "日程要在家里排（现在在" + PlaceName(Here) + "，先 go 家）"; return false; }
        if (SelectOpen() || ResOpen() || MapOpen()) { err = "先关掉开着的窗口"; return false; }
        GameObject go = null;
        try
        {
            var btn = UiSys.BtnTs(UiSys.UiSysBtn.planBtn);
            if (btn != null && btn.gameObject.activeInHierarchy) go = btn.gameObject;
        }
        catch { }
        if (go == null) go = SelfPlay.FindClickable(g => SelfPlay.LabelOf(g) == "安排");
        if (go == null) { err = "找不到「安排」按钮"; return false; }
        SelfPlay.Press(go);
        if (!PlanOpen()) { err = "日程窗口没打开（可能弹了对话，wait 看看）"; SelfPlay.Acted("点了安排"); return false; }
        return true;
    }

    internal static string CourseCmd(string text)
    {
        var busy = Busyness();
        if (busy != null) return busy;
        string err;
        if (!EnsurePlan(out err)) return err;
        if (SureOpen()) return "5 格已经排满，确认框：「" + SureText() + "」→ yes / no";
        var want = (text ?? "").Trim();
        var temp = TempCourse();
        if (temp.Count >= Constant.courseNum) return "5 格已经排满了";
        // 出去玩
        foreach (var ub in GoOutBtns())
        {
            var who = GoOutName(ub.name);
            if (Score(who, want) >= 2 || (want == "出去玩" && ub.name == "father") || Score("和" + who + "出去玩", want) >= 2)
            {
                if (!ub.enabled) return "这个月已经约过" + who + "了";
                SelfPlay.Press(ub.gameObject);
                SelfPlay.Acted("排了出去玩");
                return "排了：和" + who + "出去玩\n" + PlanText();
            }
        }
        string best = null;
        int top = 0;
        foreach (var n in OwnCourses())
        {
            int s;
            try { s = Score(T(Cou(n).chName), want); } catch { continue; }
            if (s > top) { top = s; best = n; }
        }
        if (best == null) return "没有叫「" + want + "」的课。\n" + PlanText();
        var cou = Cou(best);
        if (cou.prg >= 100) return T(cou.chName) + " 已经学完了";
        if (DataSys.Stage() > 0)
        {
            int page = cou.type / 2;
            if ((int)fCouPage.GetValue(CourseSys.Instan) != page) CourseSys.Instan.ChangeCouPageBtn(page);
        }
        Transform cell = null;
        try { cell = Ui(1).Find("panel").Find("course").Find("course").Find("view").Find("content").Find(cou.enName); } catch { }
        if (cell == null) return "日程窗口里找不到「" + T(cou.chName) + "」（screen 看看）";
        var cub = cell.GetComponent<UiBtn>();
        if (cub != null && !cub.enabled) return T(cou.chName) + " 现在排不了：" + (cou.prg + cou.tempPrg >= 100 ? "本月排的已经够学完了" : "5 格满了");
        SelfPlay.Press(cell.gameObject);
        SelfPlay.Acted("排了" + T(cou.chName));
        return "排了：" + T(cou.chName) + "\n" + PlanText();
    }

    internal static string Uncourse(bool all)
    {
        if (!PlanOpen()) return "日程窗口没开着";
        if (SureOpen()) return "确认框开着，用 no 清空重排";
        if (TempCourse().Count == 0) return "日程本来就是空的";
        if (all) CourseSys.Instan.ClearCourse();
        else CourseSys.Instan.CancelCourse();
        SelfPlay.Acted("撤了课");
        return (all ? "清空了日程\n" : "撤掉了最后一格\n") + PlanText();
    }

    internal static string Answer(bool yes)
    {
        if (!SureOpen()) return "没有确认框";
        var text = SureText();
        var method = yes ? "ClickSureBtn" : "ClickCancelBtn";
        GameObject target = null;
        foreach (var b in Ui(16).GetComponentsInChildren<Button>(false))
            for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                if (b.onClick.GetPersistentMethodName(i) == method) target = b.gameObject;
        if (target != null) SelfPlay.Press(target);
        else if (yes) UiSys.Instan.ClickSureBtn();
        else UiSys.Instan.ClickCancelBtn();
        SelfPlay.Acted(yes ? "点了是" : "点了否");
        return (yes ? "是" : "否") + "：「" + text + "」";
    }
}
