// Ludus Lunae · 火山的女儿插件：天赋
//
// 为什么单独做：天赋窗口的按钮清单里 21 格长得一模一样（「Lv.1，下一级……」），看不出哪个锁着、为什么锁；
// 锁住的格子点了没反应，回话却照样是「点了」，点数一点没少；每一级要先点格子再 yes，两步；
// 加完还要点一下空白处才关得掉天赋窗口，不关的话月中过程在播时 screen 还是天赋窗口，看着像卡住了。
//
// 游戏的规矩（DauSys.UpdateTalentUi、SureAddTalent）：天赋分三条线（Talent.path），每条线分几层（stage）。
// 每条线第一层一开始就开着；同一条线上一层的几项加起来到 Constant.needTalent（3）级，下一层才开（DauSys.talentLevelOpen）。
// 每级花 Constant.talentCost（1）点，加到 maxLevel 为止。锁着、满级、没点数时格子上的 UiBtn 是关着的，鼠标点了没反应。
//   talent         列出每一项：几级、下一级加什么（满级的写现在加了什么），锁着的那层写为什么锁、还差几级
//   talent 名字    加一级。照游戏自己的 SureAddTalent 做（点格子、确认框点「是」，最后调的就是它），不弹确认框；
//                  加不了直接说为什么。天赋窗口开着的话加完关上（和点空白处一样，DauSys.OpenTalent(false)）
// 按钮清单里天赋格子的说明也从这里取（SelfPlay.IndexKind 的 19 号）：写名字、几级、锁没锁。
using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;

static class Talents
{
    static DataConfig Cfg { get { return DataSys.Instan.dataCfg; } }

    static int Points { get { return Traverse.Create(DauSys.Instan).Field("talentPoint").GetValue<int>(); } }

    static bool LayerOpen(Talent t)
    {
        try { return DauSys.Instan.talentLevelOpen[t.path * Constant.talentPaths + t.stage]; }
        catch { return true; }
    }

    static string Name(Talent t) { return SelfPlay.Clean(Func.ChatRe(t.chName ?? ""), 20); }

    // 第 lv 级的效果：游戏的说明里 * 换成 lv × unit（DauSys.EnterTalentCell 的写法）
    static string Effect(Talent t, int lv)
    {
        try { return SelfPlay.Clean(Func.ChatRe((t.des ?? "").Replace("*", (lv * t.unit).ToString())), 80); }
        catch { return ""; }
    }

    // 上一层：同一条线、stage 小一的那几项
    static List<Talent> LayerBelow(Talent t)
    {
        try { return t.stage > 0 ? Cfg.talentsByPath[t.path][t.stage - 1] : new List<Talent>(); }
        catch { return new List<Talent>(); }
    }

    static string LockWhy(Talent t)
    {
        var below = LayerBelow(t);
        int sum = 0;
        var names = new List<string>();
        foreach (var b in below) { sum += b.level; names.Add(Name(b)); }
        if (names.Count == 0) return "还锁着";
        return "还锁着：上一层（" + string.Join("、", names.ToArray()) + "）一共加到 " + Constant.needTalent + " 级才开，现在 " + sum + " 级";
    }

    // 这一项现在能不能加，不能的话为什么
    static string CantWhy(Talent t)
    {
        if (t.level >= t.maxLevel) return "已经满级了（Lv." + t.level + "/" + t.maxLevel + "）";
        if (!LayerOpen(t)) return LockWhy(t);
        if (Points < Constant.talentCost) return "没有天赋点了（四项素质每升一个等级给 1 点）";
        return null;
    }

    static string Line(Talent t)
    {
        var s = Name(t) + " Lv." + t.level + "/" + t.maxLevel;
        if (t.level >= t.maxLevel) return s + "（满级：" + Effect(t, t.level) + "）";
        return s + "：下一级 " + Effect(t, t.level + 1) + (t.level > 0 ? "（现在 " + Effect(t, t.level) + "）" : "");
    }

    // 天赋窗口上三栏的标题（画在界面上，代码里没有；顺序就是 Talent.path 0、1、2，和截图对过）
    static readonly string[] PathNames = { "战斗", "生活", "社交" };

    // ---- talent：每一项 ----
    internal static string Text()
    {
        var s = new StringBuilder("天赋（还有 " + Points + " 点，每级 " + Constant.talentCost + " 点；talent 名字 加一级）：");
        var paths = Cfg.talentsByPath;
        for (int p = 0; p < paths.Count; p++)
        {
            s.Append("\n【").Append(p < PathNames.Length ? PathNames[p] : "第 " + (p + 1) + " 条线").Append("】");
            for (int st = 0; st < paths[p].Count; st++)
            {
                var layer = paths[p][st];
                if (layer.Count == 0) continue;
                bool open = LayerOpen(layer[0]);
                if (st > 0) s.Append("\n  —— 第 ").Append(st + 1).Append(" 层").Append(open ? "（开了）" : "（" + LockWhy(layer[0]) + "）");
                foreach (var t in layer) s.Append("\n  ").Append(open ? "" : "×").Append(Line(t));
            }
        }
        return s.ToString();
    }

    // 按钮清单里天赋格子（UiBtn 19，物体是格子里的 icon，格子的名字是天赋编号）
    internal static string Kind(UnityEngine.GameObject go)
    {
        try
        {
            var t = Cfg.talents[int.Parse(go.transform.parent.name)];
            var why = CantWhy(t);
            return "天赋：" + Name(t) + " Lv." + t.level + "/" + t.maxLevel + (why == null ? "（talent " + Name(t) + " 加一级）" : "（" + why + "）");
        }
        catch { return "天赋"; }
    }

    internal static bool WindowOpen()
    {
        try { return UiSys.Instan.uiTrans[37].gameObject.activeInHierarchy; }
        catch { return false; }
    }

    // 现在能不能打开天赋：卧室左下角那排里的「天赋」在画面上（DauSys.UpdateTalentPointTip 用的那个按钮），或者窗口已经开着
    static bool Reachable()
    {
        if (WindowOpen()) return true;
        try
        {
            var b = UiSys.Instan.uiTrans[2].Find("talentBtn");
            return b != null && b.gameObject.activeInHierarchy;
        }
        catch { return false; }
    }

    static Talent Find(string want, out string err)
    {
        err = null;
        Talent best = null;
        int top = 0, ties = 0;
        foreach (var t in Cfg.talents)
        {
            var n = Name(t);
            int s = n == want ? 3 : n.Contains(want) ? 2 : want.Contains(n) ? 1 : 0;
            if (s > top) { top = s; best = t; ties = 1; }
            else if (s == top && s > 0) ties++;
        }
        if (best == null) err = "没有叫「" + want + "」的天赋。talent 看全部";
        else if (ties > 1 && top < 3) { err = "「" + want + "」对得上好几个，写全一点。talent 看全部"; best = null; }
        return best;
    }

    // ---- talent 名字：加一级 ----
    internal static string Add(string text, out bool ok)
    {
        ok = false;
        var want = (text ?? "").Trim();
        if (want == "") return "加哪一项？talent 名字（talent 看全部）";
        if (FightPlay.InFight) return "在打仗，打完再加天赋";
        var busy = TextPlay.Busyness();
        if (busy != null) return busy;
        if (!Reachable()) return "现在画面上没有「天赋」（在家时卧室左下角那排里有），回家再加";
        string err;
        var t = Find(want, out err);
        if (t == null) return err;
        var why = CantWhy(t);
        if (why != null) return Name(t) + " 加不了：" + why + "。";
        if (TextPlay.SureOpen())
        {
            // 手点过格子、确认框还开着：是这个天赋的确认框就收起来，换成这里一步加；别的确认框不碰
            if (Traverse.Create(typeof(UiSys)).Field("sureType").GetValue<SureUi>() != SureUi.addTalent)
                return "确认框开着，先 yes / no";
            UiSys.SureUI(SureUi.none, open: false);
        }
        int lv0 = t.level, pt0 = Points;
        var opened0 = new HashSet<int>();
        for (int i = 0; i < DauSys.Instan.talentLevelOpen.Length; i++) if (DauSys.Instan.talentLevelOpen[i]) opened0.Add(i);
        Traverse.Create(DauSys.Instan).Field("tempChooseTalent").SetValue(t.index);
        DauSys.Instan.SureAddTalent();
        if (t.level != lv0 + 1) return Name(t) + " 没加上（游戏没接这一下），screen 看看";
        ok = true;
        var s = new StringBuilder(Name(t) + " Lv." + lv0 + "→" + t.level + "/" + t.maxLevel + "：" + Effect(t, t.level).TrimEnd('。') + "。天赋点 " + pt0 + "→" + Points + "。");
        // 这一下让上面一层开了
        try
        {
            var layers = Cfg.talentsByPath[t.path];
            if (t.stage + 1 < layers.Count && !opened0.Contains(t.path * Constant.talentPaths + t.stage + 1) && LayerOpen(layers[t.stage + 1][0]))
            {
                var names = new List<string>();
                foreach (var u in layers[t.stage + 1]) names.Add(Name(u));
                s.Append("这一条线的下一层开了：").Append(string.Join("、", names.ToArray())).Append('。');
            }
        }
        catch { }
        if (WindowOpen())
        {
            DauSys.Instan.OpenTalent(false);  // 和点空白处一样
            s.Append("（天赋窗口关上了）");
        }
        SelfPlay.Acted("加了天赋");
        return s.ToString();
    }
}
