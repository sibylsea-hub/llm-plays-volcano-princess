// Ludus Lunae · 火山的女儿插件：第一次见
//
// 设计文档 docs/设计理念.md 第四节。agent 在一个会话里第一次见到一个人、一个地方、一张 CG、
// 女儿的新样子、一种怪物或首领时，等画面停稳截一张（SelfPlay.Capture，缩到长边 1568），往 events.jsonl 写一条 sight：
//   {"ts","type":"sight","what":"face|crowd|place|cg|dau|monster|boss","name","image":"scenes/0123.png","note":"游戏自带的介绍"}
//   （有的多一个 why：女儿是 first / grow / costume，地方是 first / season，大立绘是 date / dance / lord）
// 印象簿 impressions.json 里没有这一条就建一条（words 空着，由 tools/vp_memory.py 收）；已有的不碰。
// 「见过」只记在内存里，按 agent 的会话算：新会话开头 vp_player 发 hello，这里清空。游戏关了再开也是空的。
// 游戏自己记的「见过」（NpcSys.AddMetNpc、CollectSys.cgUnlocked）跟着存档走，新会话里不会再触发，不用。
//
// 什么时候算见到：
//   人物    第一次在对话里说话（ChatSys.CheckControl，对话框里有他的头像）；约会、舞会、选领主时的大立绘
//   一群人  同一段对话里冒出第三个新面孔起，不再一张张截：第三个人说第一句时截一张，这段对话完了写一条 crowd
//   CG      ShowSys.OpenCg（空名字、translucentBlack、blackFrame 不算）
//   地方    到了一个地方、第一次停稳（和换场景的截图同一张）；成长阶段换了皮再来一次。探险地图也算
//   女儿    在家、画面上没开窗口时：会话里第一次、长大了（DauSys.DauStage）、换上这个会话里没见过的衣服
//   怪物    开战时（FightSys.StartFight）这场里没见过的敌人，几种一起截一张；EnemyData.type 为 2 的是首领
// 截图前要「停稳」：过场的幕布（uiTrans[7]）和换地方的字幕（uiTrans[52]）都收起 0.5 秒以上；截好之前不自动翻台词。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

static class Sights
{
    // 一样第一次见的东西：写 sight 事件、建印象簿条目要的字段
    internal class Sight
    {
        public string key, what, name, note = "", why = "", extra = "";
    }

    // 一张等着截的图：几样东西可以共用一张（同一场战斗里的几种新怪物）
    class Pending
    {
        public float due, deadline;
        public string path;                   // 先定下的文件路径（scenes/NNNN.png）
        public string place = "";
        public List<Sight> sights = new List<Sight>();  // 截好以后写的 sight（对话里的人物在说话前就写好了，这里是空的）
        public System.Func<bool> still;       // 一直得在：不在了就不截（CG 关掉了、仗打完了）
        public System.Func<bool> ready;       // 等它出来再截（约会的大立绘拉完幕才摆出来、战斗的开场字收起来）；到最后也没出来就不截
        public bool hold = true;              // 截好之前不自动翻台词、不自动点
    }

    static readonly HashSet<string> seen = new HashSet<string>();
    static readonly List<Pending> queue = new List<Pending>();
    static DateTime since = DateTime.Now;
    static float calmSince;
    static int lastDauStage = -1;

    // 一段对话里冒出来的新面孔：前两个各截一张，第三个起合成一条 crowd
    static bool chainOn;
    static float lastTalk;
    static int chainFaces;
    static readonly List<string> crowd = new List<string>();
    static string crowdPath;

    const float FaceDelay = 0.9f, CalmFor = 0.5f, MaxWait = 8f;

    static DataConfig Cfg { get { return DataSys.Instan.dataCfg; } }

    // 截好之前、刚截完的一小会儿（图在这一帧画完时才抓）不翻台词、不自动点
    static float shotAt = -1f;

    internal static bool Holding
    {
        get
        {
            foreach (var p in queue) if (p.hold) return true;
            return shotAt >= 0f && Time.unscaledTime - shotAt < 0.3f;
        }
    }

    // 有地方写才看：分了玩家文件夹以后，插件加载以来还没人玩过就没有文件夹可写
    static bool Live { get { return SelfPlay.On && Players.WriteDir != null; } }

    internal const string HelloWords = "新的一天：这之后见到的人、地方、CG、女儿的新样子、怪物都当第一次见，各截一张";

    // 换了一个人在玩（Players.cs 看 active.json）：当新会话，等着截的也不要了
    internal static void NewPlayer()
    {
        Drop();
        Hello();
    }

    // ---- 新会话：vp_player hello ----
    internal static string Hello()
    {
        int n = seen.Count;
        var before = since;
        seen.Clear();
        lastDauStage = -1;
        chainFaces = 0;
        crowd.Clear();
        crowdPath = null;
        since = DateTime.Now;
        return HelloWords
            + (n > 0 ? "（上一段从 " + before.ToString("HH:mm") + " 起见过 " + n + " 样，清掉了）" : "") + "。";
    }

    internal static void Drop()
    {
        queue.Clear();
        crowd.Clear();
        crowdPath = null;
        chainFaces = 0;
        chainOn = false;
    }

    static bool Active(int ui)
    {
        try { var t = UiSys.Instan.uiTrans[ui]; return t != null && t.gameObject.activeInHierarchy; }
        catch { return false; }
    }

    // 画面上没有过场的幕布、没有换地方的字幕
    static bool Calm() { return !Active(7) && !Active(52); }

    static string When()
    {
        try { return DataSys.Year() + " " + DataSys.Month() + "月"; }
        catch { return ""; }
    }

    static string Rel(string path)
    {
        return "scenes/" + Path.GetFileName(path);
    }

    // ---- 每 0.25 秒（自己玩开着时） ----
    internal static void Tick(float now)
    {
        if (!Calm()) calmSince = now;
        bool talking = SelfPlay.ChatBusy() || SelfPlay.MenuOpen();
        if (talking) { chainOn = true; lastTalk = now; }
        else if (chainOn && now - lastTalk >= 2f && !Holding)  // 停够 2 秒才算这段对话完了（和陪玩同步分包一样）
        {
            chainOn = false;
            chainFaces = 0;
            FlushCrowd();
        }
        WatchLords(now);
        for (int i = 0; i < queue.Count; i++)
        {
            var p = queue[i];
            bool gone = false, there = true;
            try { gone = p.still != null && !p.still(); } catch { gone = true; }
            try { there = p.ready == null || p.ready(); } catch { there = false; }
            // 不在了，或者等到最后也没出来：不截（对话里先写好的 sight 照旧，工具会说图没截下来）
            if (gone || (!there && now >= p.deadline))
            {
                queue.RemoveAt(i--);
                continue;
            }
            bool ok = there && now >= p.due && now - calmSince >= CalmFor;
            if (!ok && now < p.deadline) continue;  // 幕布一直不收：到最后照样截
            queue.RemoveAt(i--);
            if (p.hold) shotAt = now;
            try
            {
                SelfPlay.Shoot(p.path, p.place, false);
                if (p.sights.Count > 0) Emit(p.sights, p.path);
            }
            catch (Exception e) { LudusLunaePlugin.Log.LogWarning("第一次见的截图失败：" + e.Message); }
        }
    }

    static Pending Queue(float delay, List<Sight> sights, System.Func<bool> still = null, bool hold = true)
    {
        float now = Time.unscaledTime;
        var p = new Pending
        {
            due = now + delay,
            deadline = now + delay + MaxWait,
            path = SelfPlay.ReserveShot(),
            place = SelfPlay.PlaceNow(),
            still = still,
            hold = hold,
        };
        if (sights != null) p.sights.AddRange(sights);
        queue.Add(p);
        return p;
    }

    // ---- 写事件、建印象簿 ----
    internal static void Emit(List<Sight> sights, string path)
    {
        foreach (var s in sights) Write(s, Rel(path));
    }

    static void Write(Sight s, string image)
    {
        seen.Add(s.key);
        SelfPlay.Event("sight", Json.Field("what", s.what) + "," + Json.Field("name", s.name) + "," + Json.Field("image", image) + ","
            + Json.Field("note", s.note ?? "") + (s.why == "" ? "" : "," + Json.Field("why", s.why)) + s.extra);
        Impressions.Add(s.what + ":" + s.name, "{" + Json.Field("name", s.name) + "," + Json.Field("what", s.what) + ","
            + Json.Field("image", image) + "," + Json.Field("note", s.note ?? "") + ","
            + Json.Field("first", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")) + "," + Json.Field("when", When()) + ",\"words\":[]}");
    }

    // ================= 人物：对话里第一次说话 =================
    // 在 ChatLinePatch 里、记台词之前调：sight 写在这句台词前面，图在 0.9 秒后（头像出来、字打出一些）截
    internal static void OnLine(Chat chat)
    {
        if (!Live || chat == null) return;
        string key, name, note;
        if (!Speaker(chat, out key, out name, out note) || seen.Contains(key)) return;
        seen.Add(key);
        chainOn = true;
        lastTalk = Time.unscaledTime;
        chainFaces++;
        if (chainFaces <= 2)
        {
            var p = Queue(FaceDelay, null);
            Write(new Sight { key = key, what = "face", name = name, note = note }, Rel(p.path));
            return;
        }
        // 第三个起：这一个说话时截一张，后面的只记名字，这段对话完了一起写
        if (crowdPath == null) crowdPath = Queue(FaceDelay, null).path;
        crowd.Add(name);
    }

    static void FlushCrowd()
    {
        if (crowd.Count == 0 || crowdPath == null) { crowd.Clear(); crowdPath = null; return; }
        var image = Rel(crowdPath);
        if (crowd.Count == 1)
            Write(new Sight { key = "face:" + crowd[0], what = "face", name = crowd[0], note = DesOf(crowd[0]) }, image);
        else
            Write(new Sight
            {
                key = "crowd:" + string.Join("、", crowd.ToArray()),
                what = "crowd",
                name = SelfPlay.PlaceNow() + "：" + string.Join("、", crowd.ToArray()),
                note = "一下子来了好几个人，图是「" + crowd[0] + "」说第一句时的画面",
            }, image);
        crowd.Clear();
        crowdPath = null;
    }

    // 说话的是谁：和 ChatSys.StartChatEnu 定头像的逻辑一样。女儿、爸爸、旁白不算（女儿另算，见 AtStable）
    static bool Speaker(Chat chat, out string key, out string name, out string note)
    {
        key = name = note = "";
        var n = chat.name ?? "";
        if (n == "" || n == "dau" || n == "fa" || n == "aside") return false;
        Person p = null;
        if (Cfg.npcDic.ContainsKey(n)) p = NpcSys.GetNpc(n);
        else
        {
            int index;
            if (int.TryParse(n, out index) && index >= 0 && index < Cfg.people.Length) p = Cfg.people[index];
        }
        if (p != null)
        {
            key = "face:" + p.enName;
            name = SelfPlay.Clean(p.chName, 30);
            note = PersonDes(p);
            return name != "";
        }
        // 没有资料、只有一个称呼的人（文字表里的名字）：带头像的才算露了脸
        if (n.Length == 5 && !string.IsNullOrEmpty(chat.icon))
        {
            name = SelfPlay.Clean(ChatSys.Txt(n), 30);
            key = "face:" + name;
            return name != "";
        }
        return false;
    }

    // 游戏自带的介绍：村民资料页（NpcSys.UpdateNpcInfo）那段，关系每升一级多露出一段；还是陌生人时什么都没有
    internal static string PersonDes(Person p)
    {
        try
        {
            if (p == null || string.IsNullOrEmpty(p.des) || p.relationLevel <= 0) return "";
            var parts = p.des.Split('#');
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length && i < p.relationLevel; i++) sb.Append(parts[i]);
            return SelfPlay.Clean(Func.ChatRe(sb.ToString()), 400);
        }
        catch { return ""; }
    }

    static string DesOf(string name)
    {
        var p = FindPerson(name);
        return p == null ? "" : PersonDes(p);
    }

    internal static Person FindPerson(string name)
    {
        try
        {
            foreach (var p in Cfg.npcDic.Values)
                if (p != null && p.enName != "dau" && p.enName != "father" && SelfPlay.Clean(p.chName, 30) == name) return p;
            foreach (var p in Cfg.people)
                if (p != null && SelfPlay.Clean(p.chName, 30) == name) return p;
        }
        catch { }
        return null;
    }

    // ================= 大立绘：约会、舞会、选领主 =================
    internal static void OnPortrait(Person p, string enName, string chName, string why, int ui)
    {
        if (!Live) return;
        var key = "portrait:" + enName;
        if (seen.Contains(key)) return;
        seen.Add(key);
        var name = SelfPlay.Clean(chName, 30);
        var q = Queue(1.2f, new List<Sight> { new Sight { key = key, what = "face", name = name, note = p == null ? "" : PersonDes(p), why = why } });
        q.ready = () => Active(ui);  // 约会要先拉幕、等 1.7 秒才摆出立绘
    }

    // 选领主：几家的领主并排站着（ShowSys 的 showSelectLord / showChooseLordChoice，窗口在 ShowSys.tempTs）
    static float lordsSeenAt = -1f;

    static void WatchLords(float now)
    {
        if (!Live || seen.Contains("lords")) return;
        bool open = false;
        try
        {
            var ts = ShowSys.Instan == null ? null : ShowSys.Instan.tempTs;
            var choice = ts == null ? null : ts.Find("panel/choice");
            open = choice != null && choice.gameObject.activeInHierarchy && ts.Find("panel/bg/per") != null;
        }
        catch { }
        if (!open) { lordsSeenAt = -1f; return; }
        if (lordsSeenAt < 0f) { lordsSeenAt = now; return; }
        if (now - lordsSeenAt < 0.5f) return;
        seen.Add("lords");
        var names = new List<string>();
        try { foreach (var l in Cfg.lords) if (l != null && l.person != null) names.Add(SelfPlay.Clean(l.person.chName, 20)); }
        catch { }
        Queue(0.5f, new List<Sight> { new Sight { key = "lords", what = "crowd", name = "选领主：" + string.Join("、", names.ToArray()), why = "lord" } },
            () => ShowSys.Instan != null && ShowSys.Instan.tempTs != null && ShowSys.Instan.tempTs.gameObject.activeInHierarchy);
    }

    // ================= CG =================
    internal static void OnCg(string cg, int layer)
    {
        if (!Live || string.IsNullOrEmpty(cg) || cg == "translucentBlack" || cg == "blackFrame") return;
        var key = "cg:" + cg;
        if (seen.Contains(key)) return;
        seen.Add(key);
        string note = "";
        try
        {
            CollectSys.CollectionCG c;
            if (Cfg.collectionCgs.TryGetValue(cg, out c)) note = "收藏室里的第 " + (c.index + 1) + " 张";
            // 结局那张大图：结局时的「cg」对话交给 ShowSys.OpenCg("ending编号")，写上是哪个结局
            int n;
            if (cg.StartsWith("ending") && int.TryParse(cg.Substring(6), out n))
                foreach (var e in Cfg.endings)
                    if (e.index == n) { note = "结局：" + SelfPlay.Clean(e.chName, 30) + (note == "" ? "" : "，" + note); break; }
        }
        catch { }
        Queue(1f, new List<Sight> { new Sight { key = key, what = "cg", name = cg, note = note } }, () =>
        {
            var t = UiSys.Instan.uiTrans[49].Find("cg").Find("cg");
            return t.gameObject.activeInHierarchy && layer < t.childCount && t.GetChild(layer).gameObject.activeSelf;
        });
    }

    // ================= 结局的信、后日谈：图是 Func.SetImg 直接换上去的，不走 OpenCg =================
    // 结局的信第三页（EndingSys.UpdateEndingStage 的 case 2）有一张插画（Ui/common/endingImg/编号），
    // 换上以后隔一秒才显出来（ActiveAfter），再淡入一会儿；后日谈每一页一张图（ShowSys.UpdateAfterwards，cg/T页 或 cg/N页）。
    // 后日谈第一页：面板一建出来先放自己的开场（黑底上写「四个季年后……」），几秒以后才露出图，所以多等一会儿；
    // 第二页起是先拉幕（uiTrans[7]）再换图，「停稳」本来就等幕布。等的时候拦着自动翻页（hold），
    // 不然翻到下一页才截，图和页码对不上；第一页只等 1.5 秒的话，截到的是黑底。
    // 好不容易打到结局，结局的图都该给 agent 看到。
    internal static void OnEndingLetter(int stage, Transform letter)
    {
        if (!Live || letter == null || stage != 2) return;
        Transform img = null;
        string pic = "";
        try
        {
            img = letter.Find("letter").Find("page").GetChild(stage).Find("img");
            var ui = img.GetComponent<UnityEngine.UI.Image>();
            if (ui != null && ui.sprite != null) pic = ui.sprite.name;
        }
        catch { }
        if (img == null) return;
        var key = "cg:endingLetter" + pic;
        if (seen.Contains(key)) return;
        seen.Add(key);
        var p = Queue(3.5f, new List<Sight> { new Sight { key = key, what = "cg", name = "endingLetter" + pic, note = "结局的信里的插画" } },
            () => letter != null && letter.gameObject.activeInHierarchy);
        p.ready = () => img != null && img.gameObject.activeInHierarchy;
    }

    internal static void OnAfterwards(Transform panel, int page)
    {
        if (!Live || panel == null) return;
        string pic = "";
        try
        {
            var ui = panel.Find("panel").Find("cg").GetComponent<UnityEngine.UI.Image>();
            if (ui != null && ui.sprite != null) pic = ui.sprite.name;
        }
        catch { }
        var key = "cg:afterwards" + (pic == "" ? page.ToString() : pic);
        if (seen.Contains(key)) return;
        seen.Add(key);
        Queue(page == 0 ? 5f : 1.5f, new List<Sight> { new Sight { key = key, what = "cg", name = "afterwards" + (pic == "" ? page.ToString() : pic), note = "后日谈 第 " + (page + 1) + " 页" } },
            () => panel != null && panel.gameObject.activeInHierarchy);
    }

    // ================= 怪物和首领：开战时 =================
    internal static void OnFight(List<FightEnemy> roster)
    {
        if (!Live || roster == null) return;
        var list = new List<Sight>();
        foreach (var e in roster)
        {
            try
            {
                var name = SelfPlay.Clean(ChatSys.Txt(e.data.chName), 20);
                if (name == "") name = SelfPlay.Clean(e.data.chName, 20);
                var key = "monster:" + name;
                if (name == "" || seen.Contains(key)) continue;
                seen.Add(key);
                list.Add(new Sight { key = key, what = e.data.type == 2 ? "boss" : "monster", name = name, note = e.data.type == 1 ? "精英" : "" });
            }
            catch { }
        }
        if (list.Count == 0) return;
        var p = Queue(1.5f, list, () => FightPlay.InFight);
        p.ready = () => !FightPlay.SloganOn || Time.unscaledTime > p.due + 3f;  // 「回合开始」的大字收起来再截；一直不收就照样截
    }

    // ================= 地方、女儿：停稳、发出新清单时（SelfPlay.StableShot 问） =================
    // 返回这一刻第一次见的（还没记成见过），没有就返回 null。截图由 SelfPlay 截（和换场景的图共用一张），截好调 Emit
    internal static List<Sight> AtStable()
    {
        if (!Live || DataSys.Instan == null || MapSys.Instan == null || UiSys.Instan == null || FightPlay.InFight) return null;
        var list = new List<Sight>();
        if (ExploSys.isExploring && Active(23))
        {
            var map = ExploSys.tempMap;
            if (map != null)
            {
                var name = SelfPlay.Clean(map.chName, 30) + "（探险）";
                var key = "place:explo:" + map.index;
                if (!seen.Contains(key))
                {
                    string note = "";
                    try { note = SelfPlay.Clean(Func.ChatRe(map.des.Replace("|", " ")), 400); } catch { }
                    list.Add(new Sight { key = key, what = "place", name = name, note = note, why = "first" });
                }
            }
            return list.Count == 0 ? null : list;
        }
        if (TextPlay.ViewKind != "scene") return null;  // 开着窗口时不算看风景，等窗口关了
        int here = (int)MapSys.tempPlace;
        int stage = DataSys.Stage();
        // 场景按成长阶段换皮（MapSys.TurnMapUi）：家、餐厅、港口、湖从春火年就换，别的地方夏火年以前是同一张
        int skin = here == 0 || here == 2 || here == 4 || here == 8 ? stage : Math.Max(1, stage);
        var pkey = "place:" + here + "#" + skin;
        if (!seen.Contains(pkey))
        {
            bool before = false;
            foreach (var k in seen) if (k.StartsWith("place:" + here + "#")) { before = true; break; }
            list.Add(new Sight
            {
                key = pkey, what = "place", name = TextPlay.PlaceName(here), why = before ? "season" : "first",
                extra = before ? "," + Json.Field("year", DataSys.Year()) : "",
            });
        }
        if (here == (int)Place.home)
        {
            var d = DauSight();
            if (d != null) list.Add(d);
        }
        return list.Count == 0 ? null : list;
    }

    static Sight DauSight()
    {
        try
        {
            var dau = UiSys.SceneTs(0).Find("panel").Find("bg").Find("dau");
            if (dau == null || !dau.gameObject.activeInHierarchy) return null;
        }
        catch { return null; }
        int stage = DauSys.DauStage();
        int cos = FeastSys.Instan == null ? -1 : FeastSys.Instan.TempCloth();
        var key = "dau:" + stage + ":" + cos;
        if (seen.Contains(key)) return null;
        string why = lastDauStage < 0 ? "first" : stage > lastDauStage ? "grow" : "costume";
        lastDauStage = stage;
        string cloth = "";
        try { if (cos >= 0) cloth = SelfPlay.Clean(ChatSys.Txt(Cfg.items[cos].chName), 20); } catch { }
        var dauName = DauSys.Instan.dauName;
        var year = DataSys.Year();
        return new Sight
        {
            key = key, what = "dau", why = why,
            name = dauName + "（" + year + (cloth == "" ? "" : "，" + cloth) + "）",
            extra = "," + Json.Field("dau", dauName) + "," + Json.Field("cloth", cloth) + "," + Json.Field("year", year),
        };
    }

    // ================= look 名字：游戏里的资料 =================
    // 工具那边再配上印象簿和最近一张图。这里只给游戏自己写着的：介绍（人随关系露出来的那几段）、关系和好感
    internal static string LookName(string q, out string fields)
    {
        fields = Json.Field("what", "") + "," + Json.Field("name", q) + "," + Json.Field("des", "");
        q = (q ?? "").Trim();
        if (q == "") return "要看谁？look 名字";
        if (q == DauSys.Instan.dauName)
        {
            string cloth = "";
            try { int cos = FeastSys.Instan.TempCloth(); if (cos >= 0) cloth = SelfPlay.Clean(ChatSys.Txt(Cfg.items[cos].chName), 20); } catch { }
            fields = Json.Field("what", "dau") + "," + Json.Field("name", q) + "," + Json.Field("des", "");
            return q + "：" + DauSys.Age() + " 岁，" + (int)DauSys.Instan.height + " cm" + (cloth == "" ? "" : "，穿着" + cloth) + "。";
        }
        var p = FindPerson(q);
        if (p != null)
        {
            var des = PersonDes(p);
            int rel = Math.Max(0, Math.Min(p.relationLevel, Constant.relationshipCh.Length - 1));
            var info = "关系：" + Constant.relationshipCh[rel] + "，好感 " + p.favor;
            // met：游戏里认不认识（资料窗口「朋友」那一页，NpcSys.metPersons）。这个会话还没截过图时，认识的照给资料（home 里提示用 look 名字 细看朋友）
            bool met = false;
            try { var list = Traverse.Create(NpcSys.Instan).Field("metPersons").GetValue<List<string>>(); met = list != null && list.Contains(p.enName); }
            catch { }
            fields = Json.Field("what", "face") + "," + Json.Field("name", q) + "," + Json.Field("des", des) + "," + Json.Field("info", info) + "," + Json.Field("met", met);
            return q + "：" + info + (des == "" ? "。资料里还没写什么（再熟一点会多露出一些）。" : "。\n" + des);
        }
        try
        {
            for (int i = 0; i < Cfg.maps.Length; i++)
                if (TextPlay.PlaceName(i) == q)
                {
                    fields = Json.Field("what", "place") + "," + Json.Field("name", q) + "," + Json.Field("des", "");
                    return q + "：地图上的地方（游戏里没有写介绍）。";
                }
            foreach (var m in Cfg.exploMaps)
            {
                var n = SelfPlay.Clean(m.chName, 30);
                if (n != q && n + "（探险）" != q) continue;
                var des = SelfPlay.Clean(Func.ChatRe((m.des ?? "").Replace("|", " ")), 400);
                fields = Json.Field("what", "place") + "," + Json.Field("name", n + "（探险）") + "," + Json.Field("des", des);
                return n + "（探险）：" + des;
            }
        }
        catch { }
        return "游戏里找不到叫「" + q + "」的人或地方。";
    }

    // ================= 地图上新开了地方 =================
    // MapSys.TurnMapUi 每回合按 openTurn 打开地图上的地点；这一回合刚开的写一条 opened（读档时一次全开的不算：openTurn 不是这一回合）
    static readonly HashSet<int> openBefore = new HashSet<int>();

    internal static void BeforeMapUi()
    {
        openBefore.Clear();
        try
        {
            var btns = UiSys.Instan.uiTrans[5].Find("panel").Find("btn");
            for (int i = 0; i < btns.childCount; i++) if (btns.GetChild(i).gameObject.activeSelf) openBefore.Add(i);
        }
        catch { }
    }

    internal static void AfterMapUi(int turn)
    {
        if (!Live) return;
        try
        {
            var btns = UiSys.Instan.uiTrans[5].Find("panel").Find("btn");
            for (int i = 0; i < btns.childCount && i < Cfg.maps.Length; i++)
            {
                if (!btns.GetChild(i).gameObject.activeSelf || openBefore.Contains(i) || Cfg.maps[i].openTurn != turn) continue;
                var gain = new List<string>();
                foreach (var k in Cfg.maps[i].addNature) gain.Add(k == 4 ? "-道德" : "+" + Cfg.natureCh[k]);
                SelfPlay.Event("opened", Json.Field("name", TextPlay.PlaceName(i)) + "," + Json.Field("gain", string.Join(" ", gain.ToArray())));
            }
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记新开的地方失败：" + e.Message); }
    }
}

// ---- 印象簿：data/selfplay/impressions.json ----
// 键是「what:名字」，值是 {"name","what","image","note","first","when","words":[]}。这里只建新条目，已有的（包括 vp_memory.py 收进来的话）不碰：
// 不整份重写，只认出顶层有哪些键，把新条目插在最后的 } 前面。读不懂这个文件就不动它，只写日志。
static class Impressions
{
    static string PathOf() { return Path.Combine(Players.WriteDir ?? SelfPlay.Dir, "impressions.json"); }  // 在玩的那个人的

    internal static void Add(string key, string entry)
    {
        try
        {
            var path = PathOf();
            string text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            if (text.Trim() == "")
            {
                SelfPlay.WriteAtomic(path, "{\n  " + Json.Str(key) + ": " + entry + "\n}\n");
                return;
            }
            var keys = new HashSet<string>();
            int close;
            if (!TopKeys(text, keys, out close))
            {
                LudusLunaePlugin.Log.LogWarning("impressions.json 读不懂，没加「" + key + "」");
                return;
            }
            if (keys.Contains(key)) return;
            int last = close - 1;
            while (last >= 0 && char.IsWhiteSpace(text[last])) last--;
            bool empty = last >= 0 && text[last] == '{';
            var insert = (empty ? "\n  " : ",\n  ") + Json.Str(key) + ": " + entry + "\n";
            SelfPlay.WriteAtomic(path, text.Substring(0, last + 1) + insert + text.Substring(close));
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("写印象簿失败：" + e.Message); }
    }

    // 顶层对象有哪些键（值整段跳过），closeAt 是最后那个 } 的位置
    static bool TopKeys(string s, HashSet<string> keys, out int closeAt)
    {
        closeAt = -1;
        int i = 0;
        Ws(s, ref i);
        if (i >= s.Length || s[i] != '{') return false;
        i++;
        while (true)
        {
            Ws(s, ref i);
            if (i >= s.Length) return false;
            if (s[i] == '}') { closeAt = i; return true; }
            if (s[i] == ',') { i++; continue; }
            string key;
            if (!Str(s, ref i, out key)) return false;
            keys.Add(key);
            Ws(s, ref i);
            if (i >= s.Length || s[i] != ':') return false;
            i++;
            if (!Skip(s, ref i)) return false;
        }
    }

    static void Ws(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    static bool Str(string s, ref int i, out string value)
    {
        value = null;
        if (i >= s.Length || s[i] != '"') return false;
        var sb = new StringBuilder();
        for (i++; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') { i++; value = sb.ToString(); return true; }
            if (c != '\\') { sb.Append(c); continue; }
            if (++i >= s.Length) return false;
            switch (s[i])
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 >= s.Length) return false;
                    sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                default: sb.Append(s[i]); break;  // \" \\ \/
            }
        }
        return false;
    }

    static bool Skip(string s, ref int i)
    {
        Ws(s, ref i);
        if (i >= s.Length) return false;
        if (s[i] == '"') { string v; return Str(s, ref i, out v); }
        if (s[i] == '{' || s[i] == '[')
        {
            int depth = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { string v; if (!Str(s, ref i, out v)) return false; continue; }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') { depth--; if (depth == 0) { i++; return true; } }
                i++;
            }
            return false;
        }
        while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && !char.IsWhiteSpace(s[i])) i++;
        return true;
    }
}

// ---- 挂钩 ----
[HarmonyPatch(typeof(ShowSys), "OpenCg")]
static class SightCgPatch
{
    static void Postfix(string cg, int layer)
    {
        try { Sights.OnCg(cg, layer); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记 CG 失败：" + e.Message); }
    }
}

// 结局的信：每翻一页 UpdateEndingStage 换一页，第三页有插画（私有方法和字段，Harmony 按名字找）
[HarmonyPatch(typeof(EndingSys), "UpdateEndingStage")]
static class SightEndingLetterPatch
{
    static void Postfix(int ___endingLetterStage, Transform ___letterTs)
    {
        try { Sights.OnEndingLetter(___endingLetterStage, ___letterTs); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记结局的信失败：" + e.Message); }
    }
}

// 后日谈：每一页 UpdateAfterwards 换字、换图
[HarmonyPatch(typeof(ShowSys), "UpdateAfterwards")]
static class SightAfterwardsPatch
{
    static void Postfix(Transform ___afterwardsTs, int ___afterwardsPage)
    {
        try { Sights.OnAfterwards(___afterwardsTs, ___afterwardsPage); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记后日谈失败：" + e.Message); }
    }
}

// 约会：SureDatePlace 开始约会（StartDate 拉幕、摆好对方的大立绘 portrait/enName）
[HarmonyPatch(typeof(NpcSys), "SureDatePlace")]
static class SightDatePatch
{
    static void Postfix()
    {
        try
        {
            var p = NpcSys.tempNpc;
            if (p != null) Sights.OnPortrait(p, p.enName, p.chName, "date", 44);
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记约会立绘失败：" + e.Message); }
    }
}

// 舞会：选了舞伴（跳过小游戏时 MiniGames.DanceSkip 也照样摆好立绘），tempDancingPer 已经是这位舞伴
[HarmonyPatch(typeof(FeastSys), "ChooseDancingPer")]
static class SightDancePatch
{
    static void Postfix(FeastSys __instance)
    {
        try
        {
            var per = Traverse.Create(__instance).Field<FeastSys.DancingPer>("tempDancingPer").Value;
            if (per == null) return;
            Person p = null;
            try { p = NpcSys.GetNpc(per.character); } catch { }
            Sights.OnPortrait(p, "dance:" + per.character, per.chName, "dance", 53);
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记舞伴立绘失败：" + e.Message); }
    }
}

[HarmonyPatch(typeof(MapSys), "TurnMapUi")]
static class SightMapPatch
{
    static void Prefix() { try { Sights.BeforeMapUi(); } catch { } }

    static void Postfix(int turn)
    {
        try { Sights.AfterMapUi(turn); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记新开的地方失败：" + e.Message); }
    }
}
