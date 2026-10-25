// Ludus Lunae · 火山的女儿插件：家里的近况
//
// 设计文档 docs/设计理念.md 第六节「合并的粒度」：女儿卧室左下角那一排菜单（资料、背包、天赋、心愿、好感、衣橱……）只是看看的东西，合成一张，一次看完。
// 数都从游戏对象读（DauSys、ItemSys、FeastSys、NpcSys、FightSys、CourseSys），不打开窗口、不点按钮。
// 天赋点只提醒还有几点没用：加哪一项是真正的选择，还是 agent 自己打开「天赋」一点一点加。
// 各节的名字照游戏里按钮上的字；资料窗口里的分页（骑士、资料、朋友）也照游戏。学科成绩游戏里只有分数没有科目名（科目名画在图上），这里不写。
using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;

static class Home
{
    static DataConfig Cfg { get { return DataSys.Instan.dataCfg; } }

    static string T(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try
        {
            var t = ChatSys.Txt(s);
            return SelfPlay.Clean(Func.ChatRe(string.IsNullOrEmpty(t) ? s : t), 200);
        }
        catch { return SelfPlay.Clean(s, 200); }
    }

    internal static string Text()
    {
        var s = new StringBuilder("== 家里的近况（" + DataSys.Year() + " " + DataSys.Month() + "月，第 " + DataSys.Turn() + " 回合；卧室左下角那排菜单合在一起，只是看看）==\n");
        Add(s, "资料", Profile);
        Add(s, "心情", Mood);
        Add(s, "好感", Favor);
        Add(s, "干劲", Motivation);
        Add(s, "天赋", Talent);
        Add(s, "心愿", TextPlay.WishText);
        Add(s, "课程", Courses);
        Add(s, "骑士", Knight);
        Add(s, "朋友", Friends);
        Add(s, "衣橱", Wardrobe);
        Add(s, "背包", Bag);
        return s.ToString().TrimEnd();
    }

    static void Add(StringBuilder s, string title, System.Func<string> f)
    {
        string t;
        try { t = f(); }
        catch (Exception e) { t = "（读不出来：" + e.Message + "）"; }
        if (!string.IsNullOrEmpty(t)) s.Append('【').Append(title).Append('】').Append(t).Append('\n');
    }

    static string Birthday(int b) { return b / 100 + Constant.SoloCh(0) + b % 100 + Constant.SoloCh(1); }

    // 资料页（DauSys.UpdateDataUi）
    static string Profile()
    {
        var d = DauSys.Instan;
        var t = Traverse.Create(d);
        var npc = Cfg.npcDic["dau"];
        var s = new StringBuilder();
        s.Append(d.dauName).Append(string.IsNullOrEmpty(npc.lastName) ? "" : "·" + npc.lastName)
         .Append("，").Append(DauSys.Age()).Append(" 岁，").Append((int)d.height).Append(" cm，")
         .Append(Birthday(d.birth)).Append("生，血型 ").Append(Constant.bloods[d.blood]);
        try { s.Append("，").Append(T(Cfg.classes[npc._class].chName)); } catch { }
        s.Append("。爸爸 ").Append(d.faName).Append("，").Append(Birthday(d.faBirth)).Append("生。");
        var nat = new List<string>();
        for (int i = 0; i < Constant.natureNum && i < Cfg.natureCh.Length; i++)
        {
            int n = DauSys.NatureNum(i);
            string lv = "";
            try { lv = "（" + Cfg.levelCh[Func.ReturnLevel(n, Cfg.levelExp)] + "）"; } catch { }
            nat.Add(Cfg.natureCh[i] + " " + n + lv);
        }
        s.Append("\n  素质：").Append(string.Join("、", nat.ToArray()));
        var attr = new List<string>();
        for (int i = 0; i < Constant.inAttriNum && i < Constant.inAttriCh.Length; i++) attr.Add(Constant.inAttriCh[i] + " " + DauSys.InAttriNum(i));
        s.Append("\n  属性：").Append(string.Join("、", attr.ToArray()));
        s.Append("\n  黑暗 ").Append(t.Field("darkness").GetValue<int>()).Append("，爱慕者 ").Append(t.Field("lover").GetValue<int>());
        try
        {
            var count = DataSys.Instan.countDic;
            s.Append("，累计声望 ").Append(count[StateType.accumFame]).Append("，累计金币 ").Append(count[StateType.accumCoin]);
        }
        catch { }
        try { s.Append("，学完 ").Append(CourseSys.Instan.finishedCourses.Count).Append(" 门课"); } catch { }
        return s.Append('。').ToString();
    }

    // 卧室里「家里的女儿」那个心情图标（DauSys.ShowHomeStateDes）
    static string Mood()
    {
        var s = DauSys.Mood() + "";
        try
        {
            int i = Traverse.Create(DauSys.Instan).Field("homeState").GetValue<int>();
            var hs = Cfg.homeStates[i];
            s += "（" + T(hs.chName) + "：" + T(hs.des) + "）";
        }
        catch { }
        if (DauSys.Mood() < 50) s += "。心情低于 50，属性表现减半";
        return s;
    }

    static string Favor()
    {
        int lv = DauSys.favorLevel;
        string s = "Lv." + (lv + 1) + (lv < Constant.favorLevels.Length ? "（" + DauSys.Instan.favor + "/" + Constant.favorLevels[lv] + "）" : "（已满级）");
        return s + "。好感到 5 级、6 级，结局时女儿会送一份特别的礼物（悬停说明里写的）";
    }

    // 干劲和身上的状态（主界面顶上那几个图标，悬停说明的写法：干劲 +40，还剩 2 个月，来源）
    static string Motivation()
    {
        var parts = new List<string>();
        foreach (var i in Traverse.Create(DauSys.Instan).Field("ownState").GetValue<List<int>>())
        {
            var a = Cfg.achievements[i];
            var des = T((a.des ?? "").Replace("#", a.data.ToString()));
            parts.Add(T(a.chName) + "（干劲 +" + a.motivation + "，还剩 " + a.remainTurn + " 个月" + (des == "" ? "" : "，来源：" + des) + "）");
        }
        return DauSys.Motivation() + (parts.Count == 0 ? "" : "：" + string.Join("、", parts.ToArray())) + "。干劲越高，加的属性越多";
    }

    static string Talent()
    {
        int n = Traverse.Create(DauSys.Instan).Field("talentPoint").GetValue<int>();
        if (n <= 0) return "点数都用完了";
        return "还有 " + n + " 点没用。加哪一项自己挑：talent 看每一项（锁着的写着为什么），talent 名字 加一级";
    }

    static string Courses()
    {
        var s = new StringBuilder();
        try
        {
            int done = Traverse.Create(CourseSys.Instan).Field("yearFinishCourse").GetValue<int>();
            s.Append("这一学年学完 ").Append(done).Append('/').Append(Constant.courseTarget[DataSys.Stage()]).Append(" 门");
        }
        catch { }
        try { s.Append(s.Length > 0 ? "，" : "").Append("一共学完 ").Append(CourseSys.Instan.finishedCourses.Count).Append(" 门"); } catch { }
        return s.Length == 0 ? "" : s.Append("（排课：plan）").ToString();
    }

    // 资料窗口的「骑士」页（FightSys.UpdateCharaInfoUI）：等级、经验、战斗属性、身上的装备
    static string Knight()
    {
        var f = FightSys.FightNpc("dau");
        var nums = f.FightNums();
        var s = new StringBuilder("Lv." + (f.level + 1));
        if (f.level < Constant.maxFightLevel)
            try { s.Append("（经验 ").Append(f.exp).Append('/').Append(Cfg.levelUpEXP[f.level + 1]).Append('）'); } catch { }
        s.Append("：HP ").Append(nums[0]).Append("，攻击 ").Append(nums[1]).Append("，防御 ").Append(nums[2])
         .Append("，闪避 ").Append(nums[3]).Append("%，暴击 ").Append(nums[4]).Append('%');
        var eq = new List<string>();
        try
        {
            foreach (var i in f.npc.equips)
                if (i >= 0 && i < Cfg.items.Length) eq.Add(T(Cfg.items[i].chName));
        }
        catch { }
        if (eq.Count > 0) s.Append("；装备 ").Append(string.Join("、", eq.ToArray()));
        return s.ToString();
    }

    // 资料窗口的「朋友」页：见过的村民，关系和好感（好感高的在前）
    static string Friends()
    {
        var met = Traverse.Create(NpcSys.Instan).Field("metPersons").GetValue<List<string>>();
        if (met == null || met.Count == 0) return "还没认识谁";
        var people = new List<Person>();
        foreach (var en in met)
        {
            try
            {
                var p = NpcSys.GetNpc(en);
                if (p != null && p.enName != "dau" && p.enName != "father" && !people.Contains(p)) people.Add(p);
            }
            catch { }
        }
        people.Sort((a, b) => b.favor.CompareTo(a.favor));
        var parts = new List<string>();
        foreach (var p in people)
        {
            int rel = Math.Max(0, Math.Min(p.relationLevel, Constant.relationshipCh.Length - 1));
            parts.Add(SelfPlay.Clean(p.chName, 20) + "（" + Constant.relationshipCh[rel] + "，好感 " + p.favor + "）");
        }
        return "见过 " + parts.Count + " 人：" + string.Join("、", parts.ToArray()) + "。想细看一个人：look 名字";
    }

    // 衣橱（FeastSys.OwnClothes、TempCloth）。冬火年以前穿不了 3 级以上的衣服（FeastSys.ClickCostume 会挡）
    static string Wardrobe()
    {
        var fs = FeastSys.Instan;
        int cur = fs.TempCloth();
        var own = fs.OwnClothes();
        var others = new List<string>();
        foreach (var i in own)
        {
            if (i == cur || i < 0 || i >= Cfg.items.Length) continue;
            var it = Cfg.items[i];
            others.Add(T(it.chName) + (DataSys.Stage() < 3 && it.level >= 3 ? "（冬火年才能穿）" : ""));
        }
        var s = cur >= 0 && cur < Cfg.items.Length ? "穿着" + T(Cfg.items[cur].chName) : "";
        if (others.Count > 0) s += (s == "" ? "" : "。") + "衣橱里还有：" + string.Join("、", others.ToArray());
        return s == "" ? "" : s + "（换衣服：卧室里点「衣橱」）";
    }

    // 背包（ItemSys.UpdateBagPage）：按背包上的分类，服装（type 11）在衣橱里不在这
    static string Bag()
    {
        var own = Traverse.Create(ItemSys.Instan).Field("ownItems").GetValue<Dictionary<int, int>>();
        if (own == null || own.Count == 0) return "空的";
        var groups = new List<KeyValuePair<string, List<string>>>();
        var done = new HashSet<int>();
        int total = 0;
        var types = Cfg.itemUiType;
        for (int t = 1; types != null && t < types.Length; t++)
        {
            var list = new List<string>();
            foreach (var kv in own)
            {
                if (kv.Value <= 0 || done.Contains(kv.Key) || types[t] == null || !types[t].Contains(kv.Key)) continue;
                if (kv.Key < 0 || kv.Key >= Cfg.items.Length || Cfg.items[kv.Key].type == 11) continue;
                done.Add(kv.Key);
                list.Add(ItemText(kv.Key, kv.Value));
            }
            if (list.Count == 0) continue;
            string name = "";
            try { name = T(Cfg.itemUiTypeName[t]); } catch { }
            groups.Add(new KeyValuePair<string, List<string>>(name == "" ? ((ItemUiType)t).ToString() : name, list));
            total += list.Count;
        }
        var rest = new List<string>();
        foreach (var kv in own)
        {
            if (kv.Value <= 0 || done.Contains(kv.Key) || kv.Key < 0 || kv.Key >= Cfg.items.Length || Cfg.items[kv.Key].type == 11) continue;
            rest.Add(ItemText(kv.Key, kv.Value));
        }
        if (rest.Count > 0) { groups.Add(new KeyValuePair<string, List<string>>("别的", rest)); total += rest.Count; }
        if (total == 0) return "空的";
        var s = new StringBuilder(total + " 样");
        foreach (var g in groups) s.Append("\n  ").Append(g.Key).Append("：").Append(string.Join("、", g.Value.ToArray()));
        return s.ToString();
    }

    static string ItemText(int i, int n)
    {
        var it = Cfg.items[i];
        var eff = T(it.natureDes);
        return T(it.chName) + (n > 1 ? " ×" + n : "") + (eff == "" ? "" : "（" + SelfPlay.Clean(eff, 40) + "）");
    }
}
