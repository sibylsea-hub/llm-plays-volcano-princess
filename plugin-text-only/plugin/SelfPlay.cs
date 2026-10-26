// Ludus Lunae · 火山的女儿插件：agent 自己玩（selfplay）
//
// 只在 modes.json 的 selfplay 为 true 时动作，关着时这里什么都不做（只回答工具「没开」）。
// 和命令行工具 plugin-text-only/tools/vp_player.py 之间用聊天服务 data 文件夹下 selfplay/ 里的文件通信：
//   state.json    插件写：现在轮到谁（phase）、状态、对话选项或按钮清单。有变化就写，没变化也每 5 秒写一次。
//   inbox.jsonl   工具写命令：{"id","cmd","n","snap","text"}
//   outbox.jsonl  插件写回复：{"id","ok","text",…}
//   events.jsonl  插件写：台词、选项、选择、换月、跳过的小游戏……工具的 wait 从上次读到的地方往后打印。
// 对话自动往下翻（停多久看字数和面板上「看剧情」的速度）；轮到 agent 的条件是：没有对话在播、没在跳小游戏，画面上能点的东西 1 秒没变。
// 人用真鼠标、真键盘动了游戏（点按钮、翻台词），记成 human 事件告诉 agent；人自己翻的时候插件停手，歇一阵再接着翻。
// 点按钮走 Unity 自己的事件派发（ExecuteEvents），和鼠标点一样：UiBtn、Unity Button、地图上的人物都能点。
// 除了按编号点，还有按名字操作的文字命令（look / acts / plan / go / touch / pick / course / yes / no，在 TextPlay.cs）。
// 自己玩开着时游戏在后台也继续跑（Application.runInBackground），user 切到面板打字时 agent 不用停。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 插件自己的常驻物体：BepInEx 的管理物体在有的游戏里会被销毁，Update 放在这里更稳
class SelfPlayRunner : MonoBehaviour
{
    string lastError;

    void Update()
    {
        try { SelfPlay.Tick(); }
        catch (Exception e)
        {
            if (e.Message == lastError) return;
            lastError = e.Message;
            LudusLunaePlugin.Log.LogWarning("自己玩出错：" + e);
        }
    }
}

[Serializable]
public class SelfPlayCmd
{
    public string id = "";
    public string cmd = "";
    public int n;
    public int snap;
    public string text = "";
}

static class SelfPlay
{
    const float ScanEvery = 0.25f, HeartbeatEvery = 5f;

    static ConfigEntry<float> stableSeconds;

    internal static bool Muted;  // 探悬停提示、模拟移入移出时不出声

    static bool wasOn, started;
    static float nextScan, nextHeartbeat;
    static long inboxOffset = -1;

    // 一行台词什么时候出现、算几个字：自动翻页用
    static float lineShownAt = -1f;
    static float lineUnits;

    // 当前清单：最近一次发给工具的那份，click N 按它找按钮
    internal class Item
    {
        public int n;
        public GameObject go;
        public Vector2 pt;
        public string label = "", kind = "", tip = "", note = "";
        public bool off, blocked;  // blocked：结束游戏、打开网页，照常列出来（名字写清楚），点了插件拒绝
        public float y, x;
    }
    static List<Item> snapshot = new List<Item>();
    internal static List<Item> Snapshot { get { return snapshot; } }
    static List<Item> menu = new List<Item>();
    static int snapId, hiddenCount;

    static string phase = "", why = "", lastWritten = "", view = "";
    static bool? bgBefore;  // 打开自己玩之前的 runInBackground，关掉时还原
    static string lastAutoError;
    static string lastSig;
    static float sigSince;
    static bool published;

    static readonly FieldInfo fShowing = AccessTools.Field(typeof(ChatSys), "showingChat");
    static readonly FieldInfo fLogType = AccessTools.Field(typeof(ChatSys), "logType");
    static readonly FieldInfo fClickBtn = AccessTools.Field(typeof(ChatSys), "chatClickBtn");
    static readonly FieldInfo fChatTs = AccessTools.Field(typeof(ChatSys), "chatTs");

    static string Dir { get { return Path.Combine(LudusLunaePlugin.DataDir, "selfplay"); } }

    internal static void Init(ConfigFile config)
    {
        // 每句台词停多久不在这里配，在面板上「看剧情」那根滑条（modes.json 的 readCps）
        stableSeconds = config.Bind("Selfplay", "StableSeconds", 1f,
            "画面上能点的东西多久没变，才算轮到 agent（秒）");
        var go = new GameObject("LudusLunaeSelfplay");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<SelfPlayRunner>();
        if (On) RunInBackground(true);  // 插件加载时就设，否则游戏没焦点时 Tick 根本不跑
    }

    internal static bool On { get { return LudusLunaePlugin.Selfplay; } }

    // ---- 每帧 ----
    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (!started)
        {
            started = true;
            Directory.CreateDirectory(Dir);
            var inbox = Path.Combine(Dir, "inbox.jsonl");
            inboxOffset = File.Exists(inbox) ? new FileInfo(inbox).Length : 0;  // 旧命令不管
        }
        if (now < nextScan) return;
        nextScan = now + ScanEvery;

        bool on = On;
        if (on != wasOn)
        {
            wasOn = on;
            lastSig = null;
            published = false;
            if (on) Event("selfplay", Json.Field("on", true));
            RunInBackground(on);
        }
        PollInbox();
        WatchPlace();
        if (humanLines > 0 && !ChatBusy()) FlushHuman();  // 人翻的那一段对话播完了：攒的几句记成一条
        try { LudusLunaePlugin.CompanionTick(ChatBusy() || MenuOpen(), now); } catch { }
        if (!on)
        {
            SetPhase("off", "selfplay 关着");
            WriteState(false, now);
            return;
        }
        AutoAdvance(now);
        try { TextPlay.Auto(now, !ChatBusy() && !MenuOpen()); }
        catch (Exception e)
        {
            if (e.Message != lastAutoError) LudusLunaePlugin.Log.LogWarning("自动翻页出错：" + e);
            lastAutoError = e.Message;
        }
        UpdatePhase(now);
        WriteState(false, now);
    }

    static int lastPlace = -1;

    static void WatchPlace()
    {
        try
        {
            if (MapSys.Instan == null || DataSys.Instan == null) return;
            int p = (int)MapSys.tempPlace;
            if (p == lastPlace) return;
            int from = lastPlace;
            lastPlace = p;
            if (from < 0) return;
            LudusLunaePlugin.OnSceneChange(TextPlay.PlaceName(from), TextPlay.PlaceName(p));
            Event("scene", Json.Field("from", TextPlay.PlaceName(from)) + "," + Json.Field("to", TextPlay.PlaceName(p)));
        }
        catch { }
    }

    // 游戏默认没焦点就暂停；自己玩时 user 常切到面板打字，所以开着时让它在后台也跑
    static void RunInBackground(bool on)
    {
        try
        {
            if (on)
            {
                if (bgBefore == null) bgBefore = Application.runInBackground;
                Application.runInBackground = true;
            }
            else if (bgBefore != null)
            {
                Application.runInBackground = bgBefore.Value;
                bgBefore = null;
            }
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("设后台运行失败：" + e.Message); }
    }

    // 对话在播或排着队
    internal static bool ChatBusy()
    {
        LogType type;
        if (ChatSys.chatQueue.Count > 0) return true;
        return ChatShowing(out type) && !MenuOpen() && type != LogType.select && type != LogType.battle && type != LogType.goonBtn;
    }

    // ---- 对话自动往下翻 ----
    internal static void OnChatStep(LogType type, string content)
    {
        if (type != LogType.normal && type != LogType.showNature) return;
        lineShownAt = Time.unscaledTime;
        lineUnits = ReadUnits(Clean(content, int.MaxValue));
    }

    // 看剧情的速度：agent 读台词不用等（事件里都有），这些停顿全是留给旁边看的人的，所以按人读的速度、由人在面板上拖。
    // 停多久 = 字数 ÷ 每秒几个字，按比例、不封顶（封顶的话，长句不管多长都只给那么几秒，人还没读完就翻过去了）。
    // 0 是自己翻：插件不翻台词、不翻页，等人按空格、点对话框
    internal static bool ManualRead { get { return LudusLunaePlugin.ReadCps <= 0f; } }

    // 算几个字：英文字母、数字、空格按三分之一个字（读一个字母比读一个汉字快得多），别的（汉字、假名、谚文、全角标点）一个算一个
    internal static float ReadUnits(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0f;
        float n = 0f;
        foreach (var ch in s) n += ch < 0x0250 ? 1f / 3f : 1f;
        return n;
    }

    // least：最少停几秒，两三个字的句子不至于一闪而过
    internal static float ReadDelay(float units, float least = 1f)
    {
        float cps = LudusLunaePlugin.ReadCps;
        if (cps <= 0f) return float.MaxValue;
        return Mathf.Max(least, units / cps);
    }

    // 人最近一次自己点、自己翻的时候：这一句、这一页从这一刻重新计时，插件不和人抢着翻。
    // 不另外停一阵：那样人点完一个按钮，后面的台词也干等着，像卡住了
    internal static float HumanAt { get { return humanAt; } }

    static Button ChatButton()
    {
        return fClickBtn == null ? null : fClickBtn.GetValue(null) as Button;
    }

    static bool ChatShowing(out LogType type)
    {
        type = LogType.nothing;
        var chat = ChatSys.Instan;
        if (chat == null || fShowing == null) return false;
        type = (LogType)fLogType.GetValue(chat);
        return (bool)fShowing.GetValue(chat);
    }

    static void AutoAdvance(float now)
    {
        LogType type;
        if (!ChatShowing(out type) || MenuOpen()) return;
        if (type != LogType.normal && type != LogType.showNature) return;
        var btn = ChatButton();
        if (btn == null || !btn.interactable || !btn.gameObject.activeInHierarchy) return;
        if (lineShownAt < 0) lineShownAt = now;  // 没赶上这一句的开头（或上次翻页没翻动），从现在开始计时
        if (ManualRead) return;
        if (now - Mathf.Max(lineShownAt, humanAt) < ReadDelay(lineUnits)) return;  // 人刚按过空格、点过东西：从那一刻重新算
        lineShownAt = -1f;  // 等下一句的 CheckControl 再设
        // 和游戏里按空格一样（ChatSys.Update：对话框背景能点时按空格就 ClickToNextChat）
        Pressing++;
        try { ChatSys.Instan.ClickToNextChat(); }
        finally { Pressing--; }
    }

    // ---- 人自己动手 ----
    // 人用鼠标点了按钮、按空格翻了台词，插件不说的话，agent 只看到清单换了版、台词往前走了，会以为画面自己跳了。
    // 插件自己点按钮都走 Press（ExecuteEvents 直接派发，不经过鼠标），自己翻台词走 AutoAdvance，这两处举着 Pressing；
    // 按钮被点、台词被翻的那一下有钩子（HumanClickPatch、HumanLinePatch），没举着就是人。不用每帧去问键盘鼠标：
    // 游戏把点到的是哪个按钮直接交到钩子手上，没人动的时候钩子一行都不跑
    internal static int Pressing;
    static float humanAt = -1000f;
    static int humanLines, humanFrame = -1, lastHumanSnap = -1;
    static float lastHumanAt = -1000f;
    static bool humanSkipped;
    static GameObject humanGo;
    static string lastHuman;

    internal static void HumanClick(GameObject go)
    {
        if (Pressing > 0 || go == null || !On) return;
        if (Time.frameCount == humanFrame && go == humanGo) return;  // UiBtn 和 Unity Button 常挂在同一个物体上，一次点击两个钩子都响
        humanFrame = Time.frameCount;
        humanGo = go;
        humanAt = Time.unscaledTime;
        var chat = ChatButton();
        if (chat != null && go == chat.gameObject) { humanLines++; return; }  // 点对话框：和按空格一样，翻一句
        FlushHuman();
        TextPlay.HumanPressed(go);  // 人翻的是插件等着翻的那一页：页上的字照样记下来
        string did = "click", what;
        Item it = null;
        if (MenuOpen())
        {
            it = FindGo(ReadMenu(), go);
            if (it != null) did = "choose";
        }
        if (it == null) it = FindGo(snapshot, go);
        if (it != null) what = did == "choose" ? it.label : (it.label != "" ? "「" + it.label + "」" : "") + it.kind;
        else if (go.GetComponent<sceneMove>() != null) what = "场景里的空地（人走过去）";
        else
        {
            bool blocked;
            var label = LabelOf(go);
            what = (label != "" ? "「" + label + "」" : "") + KindOf(go, out blocked);
        }
        what = what.Replace("（会自动点）", "");  // 清单上提醒 agent 不用点的话，人点了就不用说了
        lastHuman = what;
        lastHumanSnap = snapId;
        lastHumanAt = Time.unscaledTime;
        // 在 agent 手里那一版清单上的，带上编号
        var n = did == "click" && it != null ? "," + Json.Field("n", it.n) : "";
        Event("human", Json.Field("did", did) + "," + Json.Field("label", what) + n);
    }

    // skip：按住 Ctrl 快进（游戏自己的 SkipToNextChat）
    internal static void HumanLine(bool skip)
    {
        if (Pressing > 0 || !On) return;
        // ClickToNextChat 游戏自己也调（选了选项接着播、等待和拉幕播完），只有按空格的那一帧算人；点对话框在 HumanClick 里算过了
        if (!skip && !Input.GetKeyDown(KeyCode.Space)) return;
        humanAt = Time.unscaledTime;
        humanLines++;
        if (skip) humanSkipped = true;
    }

    // 人翻的那几句攒成一条：对话播完、或者人又点了别的时记
    static void FlushHuman()
    {
        if (humanLines == 0) return;
        Event("human", Json.Field("did", humanSkipped ? "skip" : "lines") + "," + Json.Field("count", humanLines));
        humanLines = 0;
        humanSkipped = false;
    }

    static Item FindGo(List<Item> list, GameObject go)
    {
        foreach (var it in list) if (it.go == go) return it;
        return null;
    }

    // 清单换版是因为有人在游戏里点过，就说是谁动的，不然像是画面自己跳了
    static string Changed(int given)
    {
        // 只算最近半分钟里点的：更早点过一下、后来是游戏自己换的画面，不算人点的
        var who = lastHuman != null && lastHumanSnap >= given && Time.unscaledTime - lastHumanAt < 30f ? "刚才有人在游戏里点了" + lastHuman + "，" : "";
        return who + "清单已经换成第 " + snapId + " 版了（你看的是第 " + given + " 版）";
    }

    // ---- 现在轮到谁 ----
    static void UpdatePhase(float now)
    {
        LogType type;
        bool showing = ChatShowing(out type);
        bool menuOpen = MenuOpen();
        string busy = null;
        if (menuOpen) { }  // 选项开着就是在等 agent 回答，哪怕后面还排着对话（神秘商人那种大头像对话框）
        else if (ChatSys.chatQueue.Count > 0) busy = "对话排队中";
        else if (showing && type != LogType.select && type != LogType.battle && type != LogType.goonBtn)
            busy = ManualRead ? "对话在播（看剧情设成了自己翻，等人按空格）" : "对话在播";

        if (busy == null) busy = TextPlay.Busy();
        string sig;
        List<Item> items = null;
        if (busy != null) sig = "busy:" + busy;
        else if (menuOpen)
        {
            items = ReadMenu();
            sig = "menu:" + Sig(items);
        }
        else
        {
            int hidden;
            items = Scan(out hidden);
            sig = "click:" + Sig(items);
            if (items.Count == 0) busy = "画面上没有能点的";
            else if (items.TrueForAll(it => TextPlay.IsSkipButton(it.go))) busy = "演出在播";  // 开场这类演出按计时播，只剩「跳过」
        }
        if (sig != lastSig)
        {
            lastSig = sig;
            sigSince = now;
            published = false;
        }
        if (busy != null) { SetPhase("busy", busy); return; }
        if (now - sigSince < stableSeconds.Value) { SetPhase("busy", "画面在变"); return; }
        if (published) return;

        // 稳定了：发一份新清单
        published = true;
        if (menuOpen)
        {
            menu = items;
            view = "";
            SetPhase("choose", "对话选项");
        }
        else
        {
            Publish(true);
            view = TextPlay.View();
            // 探悬停提示时动过画面，按探完的样子重新记一次，免得下一轮以为画面变了又发一版
            int hidden;
            lastSig = "click:" + Sig(Scan(out hidden));
            SetPhase("click", "等着点按钮");
        }
        WriteState(true, now);
    }

    static void SetPhase(string p, string w)
    {
        phase = p;
        why = w;
    }

    static string Sig(List<Item> items)
    {
        var sb = new StringBuilder();
        foreach (var it in items)
            sb.Append(it.go.GetInstanceID()).Append(':').Append(it.go.name).Append(it.off ? "-" : "+").Append(';');
        return sb.ToString();
    }

    // 重新扫一遍画面，编号，带上悬停提示，作为新的一版清单
    static void Publish(bool withTips)
    {
        int hidden;
        snapshot = Scan(out hidden);
        hiddenCount = hidden;
        snapId++;
        if (withTips)
            foreach (var it in snapshot) if (!it.blocked) it.tip = Probe(it);
    }

    // ---- 对话选项 ----
    static Transform ChatTs()
    {
        return fChatTs == null ? null : fChatTs.GetValue(null) as Transform;
    }

    static IEnumerable<Transform> MenuRoots()
    {
        var chatTs = ChatTs();
        if (chatTs == null) yield break;
        var a = chatTs.Find("menu");
        if (a != null) yield return a;
        if (chatTs.parent != null)
        {
            var b = chatTs.parent.Find("chatMenu");
            if (b != null) yield return b;
        }
    }

    internal static bool MenuOpen()
    {
        foreach (var root in MenuRoots())
        {
            if (!root.gameObject.activeInHierarchy) continue;
            for (int i = 0; i < root.childCount; i++)
                if (root.GetChild(i).gameObject.activeSelf && root.GetChild(i).GetComponent<UiBtn>() != null) return true;
        }
        return false;
    }

    static List<Item> ReadMenu()
    {
        var list = new List<Item>();
        foreach (var root in MenuRoots())
        {
            if (!root.gameObject.activeInHierarchy) continue;
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                var btn = child.GetComponent<UiBtn>();
                if (!child.gameObject.activeSelf || btn == null) continue;
                var txt = child.Find("txt");
                var t = txt == null ? null : txt.GetComponent<Text>();
                list.Add(new Item
                {
                    n = list.Count + 1,
                    go = child.gameObject,
                    pt = Center(child.gameObject),
                    label = t == null ? child.name : Clean(t.text, 120),
                    kind = OptionKind(t == null ? child.name : Clean(t.text, 120)),
                    off = !btn.enabled,
                });
            }
        }
        return list;
    }

    static string OptionKind(string label)
    {
        var eff = TextPlay.OptionEffect(label);
        return eff == "" ? "对话选项" : "对话选项：" + eff;
    }

    // ---- 扫画面上能点的东西 ----
    static readonly List<RaycastResult> hits = new List<RaycastResult>();

    // missed：screen --debug 用，记下每个没列出来的东西和原因
    static List<Item> Scan(out int hidden, List<string> missed = null)
    {
        hidden = 0;
        var list = new List<Item>();
        var seen = new HashSet<int>();
        foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            // 灰的 UiBtn（enabled 为 false）也列出来、标灰：跳过它们的话，画面上看得见的灰按钮清单里却没有，对不上
            if (!IsClicker(mb) || mb is sceneMove || !mb.gameObject.activeInHierarchy) continue;
            var go = mb.gameObject;
            if (!seen.Add(go.GetInstanceID())) continue;
            Vector2 pt;
            string miss, note = "";
            if (!Reachable(go, out pt, out miss))
            {
                // 滚动列表里滚出可视区的：鼠标要先滚动才点得到，这里直接列出来，click 照样能点
                if (!InScrollView(go, out pt))
                {
                    if (missed != null) missed.Add(PathOf(go) + "：" + miss);
                    continue;
                }
                note = "在滚动列表里看不到的地方，click 照样能点";
            }
            bool blocked;
            var item = new Item { go = go, pt = pt, kind = KindOf(go, out blocked), off = !Usable(go), note = note };
            // 插件不点的按钮也照常列出来、写清楚是什么（知道是什么就不会去点），只在真点下去时拒绝（防脚本盲点）
            item.blocked = blocked;
            item.label = LabelOf(go);
            item.x = pt.x;
            item.y = pt.y;
            list.Add(item);
        }
        // 从上到下、从左到右
        list.Sort((a, b) =>
        {
            int ya = Mathf.RoundToInt(a.y / 24f), yb = Mathf.RoundToInt(b.y / 24f);
            if (ya != yb) return yb.CompareTo(ya);
            return a.x.CompareTo(b.x);
        });
        for (int i = 0; i < list.Count; i++) list[i].n = i + 1;
        return list;
    }

    static bool Usable(GameObject go)
    {
        if (go.GetComponent<mapBtn>() != null) return true;  // 场景里离得远的物体按钮是灰的，但点了人会走过去，不算不能点
        var sel = go.GetComponent<Selectable>();
        if (sel != null && !sel.IsInteractable()) return false;
        var ub = go.GetComponent<UiBtn>();
        if (ub != null && !ub.enabled && !(ub.type == BtnType.index && (ub.index == 3 || ub.index == 16 || ub.index == 19))) return false;
        return true;
    }

    // 这个物体真的能被鼠标点到吗：在它身上取几个点做射线检测，最上面的那个可点物体得是它自己
    internal static bool Reachable(GameObject go, out Vector2 at)
    {
        string miss;
        return Reachable(go, out at, out miss);
    }

    static bool Reachable(GameObject go, out Vector2 at, out string miss)
    {
        at = Vector2.zero;
        miss = "不在屏幕上";
        GameObject blocker = null;
        if (TryPoints(go, ProbePoints(go, false), ref at, ref miss, ref blocker)) return true;
        // 第一轮没打中、挡住它的东西又没把它整个盖住（比如按钮的图偏在一边、上面压着一小块装饰图）：再细细地打一轮
        if (blocker != null && !Covers(blocker, go) && TryPoints(go, ProbePoints(go, true), ref at, ref miss, ref blocker)) return true;
        return false;
    }

    static bool TryPoints(GameObject go, List<Vector2> pts, ref Vector2 at, ref string miss, ref GameObject blocker)
    {
        var es = EventSystem.current;
        foreach (var pt in pts)
        {
            if (pt.x < 0 || pt.y < 0 || pt.x > Screen.width || pt.y > Screen.height) continue;
            if (es == null) { at = pt; return true; }
            hits.Clear();
            es.RaycastAll(Ped(pt), hits);
            if (hits.Count == 0) { miss = "射线什么也没打到（透明或没有可点的图）"; continue; }
            if (ClickOwner(hits[0].gameObject) == go) { at = pt; return true; }
            blocker = hits[0].gameObject;
            miss = "被「" + PathOf(blocker) + "」挡住";
        }
        return false;
    }

    // 探测点：按钮自己的框（九个点），加上它下面能接射线的图（按钮的图常常不在按钮自己的框里）。dense 时每块打 5×5 的网格
    static List<Vector2> ProbePoints(GameObject go, bool dense)
    {
        var pts = dense ? new List<Vector2>() : Points(go);
        var rects = new List<RectTransform>();
        var own = go.transform as RectTransform;
        if (dense && own != null) rects.Add(own);
        foreach (var g in go.GetComponentsInChildren<Graphic>(false))
        {
            if (!g.raycastTarget || g.gameObject == go || rects.Count >= 8) continue;
            if (ClickOwner(g.gameObject) != go) continue;  // 里面另一个按钮的图
            rects.Add(g.rectTransform);
        }
        var canvas = go.GetComponentInParent<Canvas>();
        if (canvas == null) return pts;
        var root = canvas.rootCanvas;
        Camera cam = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
        int n = dense ? 5 : 1;
        foreach (var rt in rects)
        {
            var r = rt.rect;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    float fx = n == 1 ? 0.5f : 0.1f + 0.8f * i / (n - 1), fy = n == 1 ? 0.5f : 0.1f + 0.8f * j / (n - 1);
                    pts.Add(RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(new Vector3(r.x + r.width * fx, r.y + r.height * fy, 0f))));
                }
        }
        return pts;
    }

    // 挡住的东西在屏幕上是不是把按钮整个盖住了（盖住了就不用再细打）
    static bool Covers(GameObject blocker, GameObject go)
    {
        Rect a, b;
        if (!ScreenRect(blocker, out a) || !ScreenRect(go, out b)) return true;
        return a.xMin <= b.xMin && a.yMin <= b.yMin && a.xMax >= b.xMax && a.yMax >= b.yMax;
    }

    static readonly Vector3[] corners = new Vector3[4];

    static bool ScreenRect(GameObject go, out Rect rect)
    {
        rect = new Rect();
        var rt = go.transform as RectTransform;
        var canvas = go.GetComponentInParent<Canvas>();
        if (rt == null || canvas == null) return false;
        var root = canvas.rootCanvas;
        Camera cam = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
        rt.GetWorldCorners(corners);
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var c in corners)
        {
            var p = RectTransformUtility.WorldToScreenPoint(cam, c);
            x0 = Mathf.Min(x0, p.x); y0 = Mathf.Min(y0, p.y); x1 = Mathf.Max(x1, p.x); y1 = Mathf.Max(y1, p.y);
        }
        rect = Rect.MinMaxRect(x0, y0, x1, y1);
        return true;
    }

    // 能接鼠标的组件：点击、滑动条。关着的 UiBtn 也算（它就是画面上那个灰按钮），别的关着就当不存在
    static bool IsClicker(MonoBehaviour c)
    {
        if (c == null || !(c is IPointerClickHandler || c is Slider)) return false;
        return c.enabled || c is UiBtn;
    }

    // 这一点上鼠标点到的是哪个按钮：从打到的物体往上找第一个挂着能接鼠标的组件的
    static GameObject ClickOwner(GameObject hit)
    {
        for (var t = hit.transform; t != null; t = t.parent)
            foreach (var c in t.GetComponents<MonoBehaviour>())
                if (IsClicker(c)) return t.gameObject;
        return null;
    }

    static bool InScrollView(GameObject go, out Vector2 at)
    {
        at = Vector2.zero;
        var sr = go.GetComponentInParent<ScrollRect>();
        var es = EventSystem.current;
        if (sr == null || es == null) return false;
        // 滚动区本身得看得见、没被弹窗挡住：在视口上打射线，打到的得是滚动区里面的东西
        var vp = sr.viewport != null ? sr.viewport.gameObject : sr.gameObject;
        foreach (var pt in Points(vp))
        {
            if (pt.x < 0 || pt.y < 0 || pt.x > Screen.width || pt.y > Screen.height) continue;
            hits.Clear();
            es.RaycastAll(Ped(pt), hits);
            if (hits.Count > 0 && hits[0].gameObject.transform.IsChildOf(sr.transform)) { at = Center(go); return true; }
        }
        return false;
    }

    internal static string PathOf(GameObject go)
    {
        var parts = new List<string>();
        for (var t = go.transform; t != null && parts.Count < 4; t = t.parent) parts.Insert(0, t.name);
        return string.Join("/", parts.ToArray());
    }

    // 九个探测点：中间、上下左右、四个角往里收一点（字压在按钮中间时，边上还点得到）
    static readonly float[,] probes =
    {
        { 0.5f, 0.5f }, { 0.25f, 0.5f }, { 0.75f, 0.5f }, { 0.5f, 0.25f }, { 0.5f, 0.75f },
        { 0.15f, 0.2f }, { 0.85f, 0.2f }, { 0.15f, 0.8f }, { 0.85f, 0.8f }
    };

    static List<Vector2> Points(GameObject go)
    {
        var pts = new List<Vector2>();
        var rt = go.transform as RectTransform;
        if (rt != null)
        {
            var canvas = go.GetComponentInParent<Canvas>();
            if (canvas == null) return pts;
            var root = canvas.rootCanvas;
            Camera cam = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
            var r = rt.rect;
            for (int i = 0; i < probes.GetLength(0); i++)
            {
                var local = new Vector3(r.x + r.width * probes[i, 0], r.y + r.height * probes[i, 1], 0f);
                pts.Add(RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(local)));
            }
            return pts;
        }
        var cam2 = Camera.main;
        var col = go.GetComponent<Collider2D>();
        if (cam2 != null && col != null) pts.Add(cam2.WorldToScreenPoint(col.bounds.center));
        return pts;
    }

    static Vector2 Center(GameObject go)
    {
        var pts = Points(go);
        return pts.Count > 0 ? pts[0] : Vector2.zero;
    }

    static PointerEventData Ped(Vector2 pt)
    {
        var es = EventSystem.current;
        var ped = new PointerEventData(es) { position = pt, button = PointerEventData.InputButton.Left, clickCount = 1 };
        return ped;
    }

    // ---- 按钮上的字 ----
    static readonly Regex tags = new Regex("<[^>]*>");

    internal static string Clean(string s, int max = 60)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = tags.Replace(s, "").Replace("\r", "").Replace("\n", " ").Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length > max ? s.Substring(0, max) + "…" : s;
    }

    internal static string LabelOf(GameObject go)
    {
        var parts = new List<string>();
        foreach (var t in go.GetComponentsInChildren<Text>(false))
        {
            if (!t.enabled) continue;
            var s = Clean(t.text);
            if (s == "") continue;
            var owner = ClickOwner(t.gameObject);
            if (owner != null && owner != go) continue;  // 里面另一个按钮的字
            if (!parts.Contains(s)) parts.Add(s);
            if (parts.Count >= 6) break;
        }
        return string.Join(" / ", parts.ToArray());
    }

    // 插件不点的按钮只有两样：结束游戏（关了得由 user 在电脑前重新打开）、打开网页。存档、读档、成就、排行榜、设置、继承天赋都不挡
    // （agent 自己打出来的成就也算数）；跳过小游戏得来的成就另有一道闸，在 MiniGames.cs。
    static readonly HashSet<int> blockedIndex = new HashSet<int>();
    static readonly Regex blockedMethod = new Regex("Quit|Exit|ClickURL", RegexOptions.IgnoreCase);

    static string KindOf(GameObject go, out bool blocked)
    {
        blocked = false;
        // UiBtn 和 Unity Button 常常挂在同一个物体上：UiBtn 只管悬停、换图（编号 0 的什么都不做），真正干活的是 Button 上接的方法
        string calls = ButtonCalls(go, ref blocked);
        var ub = go.GetComponent<UiBtn>();
        bool functional = ub != null && ub.type == BtnType.index && !noopIndex.Contains(ub.index);  // 礼物格这类本身有功能的不算
        if (!functional && (closeNames.Contains(go.name) || LabelOf(go).Contains("点击空白处关闭")))
            return "关掉窗口（点空白处）" + (calls == "" ? "" : "（" + calls + "）");
        if (ub != null)
        {
            string k;
            if (ub.type == BtnType.index)
            {
                if (blockedIndex.Contains(ub.index)) blocked = true;
                k = ub.index == 0 ? "" : IndexKind(ub.index, go);
                if (ub.index == 49)
                {
                    var c = TextPlay.CellLine(go.name);
                    if (c != "") k = "餐厅的菜：" + c;
                }
            }
            else if (ub.type == BtnType.selectChoice)
            {
                var c = TextPlay.CellLine(go.name);
                k = c == "" ? TypeKind(ub.type) : "活动：" + c;
            }
            else if (ub.type == BtnType.courseBtn) k = "课程：" + TextPlay.CourseLine(go.name);
            else if (ub.type == BtnType.chatSelect)
            {
                int m = MenuNumber(go);
                k = m > 0 ? "对话选项（choose " + m + "）" : TypeKind(ub.type);
            }
            else k = Decorative(ub.type) && calls != "" ? "" : TypeKind(ub.type);
            if (calls != "") k = k == "" ? calls : k + " → " + calls;
            var icon = TextPlay.IconName(go);
            if (icon != "") return icon + (k == "" || k == "图标" ? "" : "（" + k + "）");
            if (k == "" && ub.type == BtnType.index && noopIndex.Contains(ub.index))
                return "只是图标，点了没反应（悬停说明用 tip 看；" + Unnamed(go) + "）";
            if (k == "" || k == "图标" || k == "按钮") return (k == "" ? "按钮" : k) + "（" + Unnamed(go) + "）";
            return k;
        }
        var mb = go.GetComponent<mapBtn>();
        if (mb != null)
        {
            string name = mb.data == null ? go.name : ChatSys.Txt(mb.data.chName);
            return "场景里的「" + name + "」" + (TextPlay.Far(mb) ? "（离得远，点一下人先走过去）" : "");
        }
        if (go.GetComponent<FightEnemy>() != null) return "敌人";
        var slider = go.GetComponent<Slider>();
        if (slider != null) return "滑动条（现在 " + slider.normalizedValue.ToString("0.00") + "，slide N 0~1 拖到哪）";
        if (go.GetComponent<Button>() != null) return calls == "" ? "按钮" : calls;
        if (go.GetComponent<InputField>() != null) return "输入框（type 文字）";
        if (go.GetComponent<Toggle>() != null) return "勾选框";
        return "可点的 " + go.name;
    }

    // 没字也没方法的按钮：写出物体名和贴图名，好歹能猜
    static string Unnamed(GameObject go)
    {
        var img = go.GetComponent<Image>();
        var sprite = img != null && img.sprite != null ? img.sprite.name : "";
        return "物体 " + go.name + (sprite == "" || sprite == go.name ? "" : "，图 " + sprite);
    }

    static int MenuNumber(GameObject go)
    {
        if (!MenuOpen()) return 0;
        foreach (var it in ReadMenu()) if (it.go == go) return it.n;
        return 0;
    }

    // 「点击空白处关闭」的背景：写清楚它是关窗口用的
    static readonly HashSet<string> closeNames = new HashSet<string> { "bg", "mask", "bgWnd", "closeBg", "closeMask", "blackBg" };

    static bool Decorative(BtnType t)
    {
        return t == BtnType.enterIcon || t == BtnType.enterAnimator || t == BtnType.showDes || t == BtnType.showTip
            || t == BtnType.enterDestroy || t == BtnType.destroyUntil;
    }

    // 编辑器里接在 Unity Button 上的方法，常见的翻成中文
    static readonly Dictionary<string, string> methodZh = new Dictionary<string, string>
    {
        { "UiSys.ClickSureBtn", "确认框：是（yes）" },
        { "UiSys.ClickCancelBtn", "确认框：否（no）" },
        { "CourseSys.PlanBtn", "打开日程（plan）" },
        { "CourseSys.ClosePlanBtn", "关掉日程" },
        { "CourseSys.CancelCourse", "撤掉最后一节课（uncourse）" },
        { "CourseSys.StartProcessPage", "过程页：下一页（会自动点）" },
        { "CourseSys.EndProcessBtn", "结算完，进入下月（会自动点）" },
        { "MapSys.MapBtn", "打开地图（go 地点）" },
        { "MapSys.OpenSelectPanel", "关掉活动面板" },
        { "MapSys.UpdateVegBtn", "餐厅：换一批菜" },
        { "MapSys.BuyVegBtn", "餐厅：买食材" },
        { "MapSys.EnterRes", "关掉餐厅" },
        { "ShowSys.ClickSkipBtn", "跳过演出" },
        { "ShowSys.ClickShowBtn", "演出：继续（会自动点）" },
        { "DauSys.EnterFatherRoom", "进出爸爸的房间" },
        { "DataSys.TransTurnBtn", "结束这个月" },
        { "ChatSys.ClickToNextChat", "对话：下一句（会自动翻）" },
        { "ItemSys.OpenFurnitureWnd", "家具" },
        { "ItemSys.OpenItem", "背包" },
        { "DauSys.OpenTalent", "天赋" },
        { "DauSys.EnterFaRoomBtn", "见女儿 / 回爸爸的房间" },
        { "DauSys.CloseCelebrateWnd", "庆祝窗口：确认（会自动点）" },
        { "TaskSys.ClickWishBtn", "女儿的心愿（wish 看）" },
        { "ExploSys.EnterExplo", "探险（行动力 8）" },
        { "ExploSys.CloseMap", "离开探险地图" },
        { "ExploSys.StartExploBtn", "出发探险" },
        { "ExploSys.OpenMapDataWnd", "关掉地图说明" },
        { "ExploSys.FinishExploBtn", "结束探险" },
        { "FightSys.OpenLevelUp", "石碑：队员升级" },
        { "ChatSys.OpenLetterBtn", "看信" },
        { "ChatSys.ClickLetterGift", "收下信里的礼物" },
        { "UiSys.OpenData", "资料" },
        { "CourseSys.ShowCourseBtn", "看课程" },
        { "CourseSys.SummaryNextPageBtn", "学期总结：下一页" },
        { "CourseSys.CloseSummaryBtn", "学期总结：确认" },
        { "FeastSys.StartBirdGameBtn", "参加" },
        { "FeastSys.ChooseDialColor", "转盘：押这个颜色" },
        { "FeastSys.OpenBirdDayDesPage", "离开" },
        { "DauSys.ClickDauBtn", "女儿（点了会聊几句）" },
        { "GameObject.SetActive", "关掉这一层" },
        { "MapSys.OpenWishingPond", "关掉许愿池" },
        { "MapSys.ClickWishingPondItem", "许愿池：兑换幸运" },
        { "TaskSys.OpenWishWnd", "心愿 / 领主面板（开着时点它就是关掉）" },
        { "UiSys.ChangeDataPage", "资料：换分页（战斗 / 资料 / 朋友）" },
        { "ItemSys.MaxCookingNum", "烹饪：做最大份数" },
        { "FightSys.ClickCancelMask", "取消选好的技能" },
        { "FightSys.EndPlayerTurnBtn", "结束回合" },
        { "FightSys.DefenseBtn", "防御（1 能量，全队减伤 25%）" },
    };

    // 只按方法名认的：存档、读档、设置这类系统按钮，目标类型不固定
    static readonly Dictionary<string, string> methodOnlyZh = new Dictionary<string, string>
    {
        { "OpenSaveGame", "存档" }, { "OpenLoadGame", "读档（回溯时光）" }, { "OpenSettingWnd", "设置（读档、回标题、全屏、同步成就都在里面）" },
        { "BackToTitleBtn", "回标题" }, { "WndBtn", "全屏 / 窗口" }, { "ResolutionBtn", "分辨率" },
        { "OpenInheritTalent", "继承天赋" }, { "ResetTalentBtn", "重置继承天赋" },
        { "CheckAllAchievementsBtns", "同步 Steam 成就" }, { "OpenAchieveWnd", "成就" }, { "ClickRankingBtn", "排行榜" },
        { "ClickURL", "打开网页" }, { "QuitGame", "结束游戏" }, { "Quit", "结束游戏" }, { "ExitGame", "结束游戏" },
        { "OpenCollectWnd", "收藏室" }, { "OpenStaffWnd", "制作人员" },
    };

    static string ButtonCalls(GameObject go, ref bool blocked)
    {
        var btn = go.GetComponent<Button>();
        if (btn == null) return "";
        var calls = new List<string>();
        for (int i = 0; i < btn.onClick.GetPersistentEventCount(); i++)
        {
            var target = btn.onClick.GetPersistentTarget(i);
            var method = btn.onClick.GetPersistentMethodName(i) ?? "";
            if (blockedMethod.IsMatch(method)) blocked = true;
            var key = (target == null ? "?" : target.GetType().Name) + "." + method;
            string zh;
            calls.Add(methodZh.TryGetValue(key, out zh) || methodOnlyZh.TryGetValue(method, out zh) ? zh : key);
        }
        return string.Join("，", calls.ToArray());
    }

    static string TypeKind(BtnType type)
    {
        switch (type)
        {
            case BtnType.clickDes: return "摆设 / 说明（点了会说一句话）";
            case BtnType.createBtn: return "建角色";
            case BtnType.dice: return "骰子";
            case BtnType.chatSelect: return "对话选项";
            case BtnType.specialChoice: return "特殊选项";
            case BtnType.courseBtn: return "课程";
            case BtnType.selectChoice: return "选项";
            case BtnType.paintingTag: return "画作标签";
            case BtnType.tutorialBtn: return "教程";
            case BtnType.clickDestroy: return "关掉";
            case BtnType.enterIcon: return "图标";
            case BtnType.showDes: return "说明（tip 看）";
            case BtnType.showTip: return "提示（tip 看）";
        }
        return type.ToString();
    }

    static string Safe(string[] arr, string name)
    {
        int i;
        if (arr != null && int.TryParse(name, out i) && i >= 0 && i < arr.Length) return "：" + arr[i];
        return "";
    }

    // UiBtn.OnPointerClick 里那个大 switch，翻成人话
    static string IndexKind(int index, GameObject go)
    {
        switch (index)
        {
            case 1: return "去地图上的地点";
            case 2: return "背包分类";
            case 3: return "背包物品";
            case 4: return "画作标题";
            case 5: return "大师课";
            case 6: return "探险地图选项";
            case 8: return "战斗：选成员";
            case 10: return "村民名册";
            case 12: return "抓马";
            case 13: return "生日：日";
            case 14: return "探险路线";
            case 15: return "战斗：选武器";
            case 16: return "战斗：选技能";
            case 17: return "村民头像";
            case 18: return "回信选项";
            case 19: return "天赋";
            case 20: return "喂马";
            case 22: return "学技能";
            case 23: return "教课";
            case 24: return "选颜色";
            case 25: return "爸爸的身份" + Safe(Constant.dauWishCh, go.name);
            case 27: return "战斗物品";
            case 28: return "马改名";
            case 29: return "马的收入";
            case 30: case 31: return "竖排对话按钮";
            case 33: return "赛马：选马";
            case 35: return "辩论话题";
            case 36: return "家里的女儿";
            case 38: return "约会地点";
            case 39: return "下一页";
            case 40: return "服装";
            case 41: return "打工";
            case 42: return "外出课程";
            case 45: return "礼物";
            case 46: return "存档";
            case 47: return "偷东西";
            case 49: return "资源选项";
            case 51: return "看戏";
            case 52: return "和女儿聊天";
            case 53: return "舞会场地";
            case 54: return "读档";
            case 55: return "抓马选择";
            case 56: return "配方说明";
            case 57: return "舞伴";
            case 58: return "话题";
            case 59: return "种子";
            case 60: return "台词选项";
            case 61: return "打工选项";
            case 62: return "领主任务完成";
            case 63: return "炼金物品";
            case 64: return "骑士阶段";
            case 65: return "生日：月";
            case 66: return "选领主：" + TextPlay.LordName(go.name);
            case 67: return "课程里的随机事件选项";
            case 68: return "钓鱼";
            case 69: return "结局选择";
            case 70: return "结局下一页";
            case 72: return "比赛项目";
            case 73: return "事件里的村民";
            case 74: return "女儿的名字";
            case 75: return "魔法商店物品";
            case 76: return "关魔法商店";
            case 96: return "排行榜";
            case 101: case 102: return "成就";
            case 111: return "伙伴升级";
            case 112: return "关升级窗口";
            case 113: return "登岸物品";
            case 114: return "下一张生日贺卡";
            case 116: return "换马";
            case 117: return "跳过结局评分";
            case 118: return "关村民来信";
            case 119: return "大师课翻页";
            case 122: return "女儿的烦恼：开始";
            case 123: return "女儿的烦恼：选项";
            case 124: return "爸爸的工作：" + TextPlay.FaWorkName(go.name);
            case 125: return "爸爸工作的说明";
            case 126: return "爸爸的目标" + Safe(Constant.faTargetCh, go.name);
            case 127: return "关爸爸目标窗口";
            case 128: return "关制作人员";
            case 129: return "结局牧场";
            case 130: return "关冬季提示";
            case 131: return "爸爸房间提示";
            case 132: return "关抓马";
            case 133: return "抓马选项";
            case 134: return "存档翻页";
            case 135: return "确认批量";
            case 137: return "关批量窗口";
            case 138: case 300: return "结束这个月";
            case 139: return "返回（结局选择回到上一层）";
            case 140: return "确认数量";
            case 141: return "关数量窗口";
            case 143: return "和领主说话";
            case 144: return "雾巷";
            case 147: return "选项";
            case 149: return "黑市物品";
            case 150: return "关黑市";
            case 200: return "修复收藏";
        }
        switch (index)
        {
            case 103: return "关成就窗口";
            case 104: return "继承天赋：翻页";
            case 106: return "关继承天赋窗口";
            case 107: return "继承天赋：升级（花火石）";
            case 108: case 109: case 110: return "教程";
            case 115: return "收藏室：爸爸 / 妈妈";
            case 120: return "重置继承天赋";
            case 121: return "收藏室：换音乐";
        }
        if (index >= 77 && index <= 121 || index == 145 || index == 146 || index == 148) return "收藏室";
        if (noopIndex.Contains(index)) return "";  // 点了游戏什么都不做：真正干活的是同一个物体上的 Unity Button，或者它只是带悬停说明的图标
        return "按钮 " + index;
    }

    // UiBtn.OnPointerClick 里什么都不做的编号（0 不在 switch 里，也是什么都不做）
    static readonly HashSet<int> noopIndex = new HashSet<int> { 0, 7, 9, 11, 21, 26, 32, 37, 43, 44, 48, 50, 71, 105 };

    // ---- 悬停提示：鼠标移上去，看画面上多出了哪些字，再移开 ----
    static Dictionary<int, string> VisibleTexts()
    {
        var d = new Dictionary<int, string>();
        foreach (var t in UnityEngine.Object.FindObjectsByType<Text>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (t.enabled && !string.IsNullOrEmpty(t.text)) d[t.GetInstanceID()] = t.text;
        return d;
    }

    static string Probe(Item it)
    {
        var ub = it.go.GetComponent<UiBtn>();
        if (ub == null || it.off || !TextPlay.ProbeSafe(it.go)) return "";
        switch (ub.type)
        {
            case BtnType.enterDestroy:
            case BtnType.destroyUntil:
            case BtnType.clickDestroy:
            case BtnType.enterIcon:
            case BtnType.enterAnimator:
            case BtnType.chatSelect:
            case BtnType.selectChoice:
                return "";
        }
        var before = VisibleTexts();
        var parts = new List<string>();
        Muted = true;
        try
        {
            ExecuteEvents.Execute(it.go, Ped(it.pt), ExecuteEvents.pointerEnterHandler);
            foreach (var t in UnityEngine.Object.FindObjectsByType<Text>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!t.enabled || t.transform.IsChildOf(it.go.transform)) continue;
                string old;
                if (before.TryGetValue(t.GetInstanceID(), out old) && old == t.text) continue;
                var s = Clean(t.text, 200);
                if (s != "" && !parts.Contains(s)) parts.Add(s);
            }
        }
        catch { }
        finally
        {
            try { ExecuteEvents.Execute(it.go, Ped(it.pt), ExecuteEvents.pointerExitHandler); } catch { }
            Muted = false;
        }
        var tip = string.Join(" | ", parts.ToArray());
        return tip.Length > 400 ? tip.Substring(0, 400) + "…" : tip;
    }

    // ---- 画面上的字（read 命令）：按位置排，看弹窗、结果用 ----
    internal static string ReadTexts(Transform root = null)
    {
        var list = new List<KeyValuePair<Vector2, string>>();
        var texts = root == null
            ? UnityEngine.Object.FindObjectsByType<Text>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            : root.GetComponentsInChildren<Text>(false);
        foreach (var t in texts)
        {
            if (!t.enabled || t.color.a < 0.05f) continue;
            if (t.canvasRenderer != null && t.canvasRenderer.GetInheritedAlpha() < 0.05f) continue;
            var s = Clean(t.text, 300);
            if (s == "") continue;
            var pt = Center(t.gameObject);
            if (pt.x < 0 || pt.y < 0 || pt.x > Screen.width || pt.y > Screen.height) continue;
            list.Add(new KeyValuePair<Vector2, string>(pt, s));
        }
        list.Sort((a, b) =>
        {
            int ya = Mathf.RoundToInt(a.Key.y / 24f), yb = Mathf.RoundToInt(b.Key.y / 24f);
            if (ya != yb) return yb.CompareTo(ya);
            return a.Key.x.CompareTo(b.Key.x);
        });
        var sb = new StringBuilder();
        int lastRow = int.MinValue;
        foreach (var kv in list)
        {
            int row = Mathf.RoundToInt(kv.Key.y / 24f);
            if (sb.Length > 0) sb.Append(row == lastRow ? "  ｜  " : "\n");
            sb.Append(kv.Value);
            lastRow = row;
            if (sb.Length > 6000) { sb.Append("\n……"); break; }
        }
        return sb.ToString();
    }

    // ---- 命令 ----
    static void PollInbox()
    {
        var path = Path.Combine(Dir, "inbox.jsonl");
        if (!File.Exists(path)) return;
        long size = new FileInfo(path).Length;
        if (size < inboxOffset) inboxOffset = 0;
        if (size == inboxOffset) return;
        byte[] data;
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            f.Seek(inboxOffset, SeekOrigin.Begin);
            data = new byte[size - inboxOffset];
            int read = 0;
            while (read < data.Length)
            {
                int r = f.Read(data, read, data.Length - read);
                if (r <= 0) break;
                read += r;
            }
        }
        int end = Array.LastIndexOf(data, (byte)'\n');
        if (end < 0) return;
        inboxOffset += end + 1;
        foreach (var raw in Encoding.UTF8.GetString(data, 0, end).Split('\n'))
        {
            var line = raw.Trim();
            if (line == "") continue;
            SelfPlayCmd cmd;
            try { cmd = JsonUtility.FromJson<SelfPlayCmd>(line); }
            catch (Exception e) { LudusLunaePlugin.Log.LogWarning("看不懂的命令：" + line + " " + e.Message); continue; }
            string reply;
            try { reply = Run(cmd); }
            catch (Exception e) { reply = Fail(e.GetType().Name + "：" + e.Message); }
            Reply(cmd.id, reply);
        }
    }

    static string Ok(string text, string extra = "")
    {
        return Json.Field("ok", true) + "," + Json.Field("text", text) + extra;
    }

    static string Fail(string text)
    {
        return Json.Field("ok", false) + "," + Json.Field("text", text);
    }

    static string Run(SelfPlayCmd c)
    {
        if (c.cmd == "ping") return Ok("在", "," + Json.Field("phase", phase));
        if (!On) return Fail("selfplay 关着：在面板上打开「agent 自己玩」（data/modes.json 的 selfplay）");
        switch (c.cmd)
        {
            case "screen":
                {
                    Publish(true);
                    // 这一版就是现在稳定的画面，别让下一轮扫描又发一版新的（编号会对不上）
                    int hidden;
                    lastSig = "click:" + Sig(Scan(out hidden));
                    published = true;
                    view = TextPlay.View();
                    if (phase == "click") WriteState(true, Time.unscaledTime);
                    return Ok("第 " + snapId + " 版清单（现在：" + phase + "，" + why + "）", "," + ItemsJson() + "," + Json.Field("view", view));
                }
            case "click":
                {
                    if (c.snap != 0 && c.snap != snapId)
                        return Json.Field("ok", false) + "," + Json.Field("text", Changed(c.snap) + "，看下面的新清单再点")
                            + "," + ItemsJson();
                    var it = Find(snapshot, c.n);
                    if (it == null) return Fail("第 " + snapId + " 版清单里没有 " + c.n + " 号");
                    return Click(it, "click");
                }
            case "choose":
                {
                    var list = MenuOpen() ? ReadMenu() : null;
                    if (list == null || list.Count == 0) return Fail("现在没有对话选项");
                    var it = Find(list, c.n);
                    if (it == null) return Fail("选项只有 1 到 " + list.Count);
                    if (it.off) return Fail("这个选项是灰的，选不了：" + it.label);
                    return Click(it, "choose");
                }
            case "tip":
                {
                    if (c.snap != 0 && c.snap != snapId)
                        return Json.Field("ok", false) + "," + Json.Field("text", Changed(c.snap) + "，看下面的新清单")
                            + "," + ItemsJson();
                    var it = Find(snapshot, c.n);
                    if (it == null) return Fail("第 " + snapId + " 版清单里没有 " + c.n + " 号");
                    if (it.blocked) return Fail("这个按钮（" + it.kind + "）插件不碰，也不探提示");
                    var tip = Probe(it);
                    return Ok(tip == "" ? "没有悬停提示" : tip);
                }
            case "read":
                return Ok(ReadTexts());
            case "type":
                return TypeText(c.text);
            case "slide":
                {
                    if (c.snap != 0 && c.snap != snapId)
                        return Json.Field("ok", false) + "," + Json.Field("text", Changed(c.snap) + "，看下面的新清单")
                            + "," + ItemsJson();
                    var it = Find(snapshot, c.n);
                    if (it == null || it.go == null) return Fail("第 " + snapId + " 版清单里没有 " + c.n + " 号");
                    var sl = it.go.GetComponent<Slider>();
                    if (sl == null) return Fail(c.n + " 号不是滑动条");
                    if (!sl.IsInteractable()) return Fail("这个滑动条是灰的");
                    float v;
                    if (!float.TryParse(c.text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
                        return Fail("slide 要一个 0 到 1 之间的数");
                    var before = sl.normalizedValue;
                    sl.normalizedValue = Mathf.Clamp01(v);  // 和拖动一样会触发 onValueChanged，游戏据此改数量、押注
                    Event("slide", Json.Field("n", c.n) + "," + Json.Field("value", sl.normalizedValue));
                    Acted("拖了滑动条");
                    return Ok("滑动条 " + before.ToString("0.00") + " → " + sl.normalizedValue.ToString("0.00"));
                }
            case "debug":
                {
                    // 画面上每个能接鼠标的东西：列出来的带屏幕坐标（左上角为原点），没列出来的写原因
                    int hidden;
                    var missed = new List<string>();
                    var items = Scan(out hidden, missed);
                    var sb = new StringBuilder("屏幕 " + Screen.width + "×" + Screen.height + "，列出 " + items.Count + " 个，没列 " + missed.Count + " 个");
                    foreach (var it in items)
                        sb.Append("\n  ").Append(it.n).Append(". (").Append(Mathf.RoundToInt(it.pt.x)).Append(',').Append(Mathf.RoundToInt(Screen.height - it.pt.y)).Append(") ")
                          .Append(it.label == "" ? "" : "「" + it.label + "」").Append(it.kind).Append(it.off ? "（灰）" : "").Append(it.note == "" ? "" : "【" + it.note + "】")
                          .Append("  ").Append(PathOf(it.go));
                    sb.Append("\n没列出来的：");
                    foreach (var m in missed) sb.Append("\n  ").Append(m);
                    return Ok(sb.ToString());
                }
            case "shot":
                {
                    var path = Path.Combine(Dir, "shot.png");
                    ScreenCapture.CaptureScreenshot(path);  // 这一帧画完才写，工具那边等文件更新
                    return Ok(path, "," + Json.Field("path", path));
                }
        }
        return RunText(c);
    }

    // 文字命令（TextPlay.cs）：名字代替编号
    static string RunText(SelfPlayCmd c)
    {
        if (DataSys.Instan == null || MapSys.Instan == null || UiSys.Instan == null) return Fail("还没进存档");
        switch (c.cmd)
        {
            case "look": return Ok(TextPlay.Look());
            case "acts": return Ok(TextPlay.Acts());
            case "plan": return Ok(TextPlay.PlanText());
            case "wish": return Ok(TextPlay.WishText());
            case "planopen":
                {
                    var r = TextPlay.OpenPlan();
                    return r == null ? Ok(TextPlay.PlanText()) : Fail(r);
                }
            case "find":
                {
                    string fields;
                    var err = TextPlay.Find(c.text, out fields);
                    return err != null ? Fail(err) : Ok("找到了", "," + fields);
                }
            case "go":
                {
                    bool again;
                    var r = TextPlay.Go(c.text, out again);
                    bool ok = r.StartsWith("去") || r.StartsWith("见女儿") || r.StartsWith("已经在");
                    return ok ? Ok(r, "," + Json.Field("again", again)) : Fail(r);
                }
            case "touch":
                {
                    var r = TextPlay.Touch(c.text);
                    return r.StartsWith("点了") || r.StartsWith("走到") ? Ok(r) : Fail(r);
                }
            case "pick":
                {
                    var r = TextPlay.Pick(c.text);
                    return r.StartsWith("选了") ? Ok(r) : Fail(r);
                }
            case "course":
                {
                    var r = TextPlay.CourseCmd(c.text);
                    var tail = "," + Json.Field("sure", TextPlay.SureOpen()) + "," + Json.Field("count", TextPlay.ScheduledCount());
                    return r.StartsWith("排了") ? Ok(r, tail) : Json.Field("ok", false) + "," + Json.Field("text", r) + tail;
                }
            case "uncourse":
                {
                    var r = TextPlay.Uncourse(c.n != 0);
                    return r.StartsWith("撤掉") || r.StartsWith("清空") ? Ok(r) : Fail(r);
                }
            case "yes":
            case "no":
                {
                    var r = TextPlay.Answer(c.cmd == "yes");
                    return r.StartsWith("是") || r.StartsWith("否") ? Ok(r) : Fail(r);
                }
        }
        return Fail("不认识的命令：" + c.cmd);
    }

    static Item Find(List<Item> list, int n)
    {
        foreach (var it in list) if (it.n == n) return it;
        return null;
    }

    static string Click(Item it, string how)
    {
        if (it.blocked) return Fail("「" + (it.label != "" ? it.label : it.kind) + "」（" + it.kind + "）是结束游戏或打开网页，插件不点");
        if (it.go == null) return Fail("这个按钮已经不在了，重新 screen");
        if (!it.go.activeInHierarchy) return Fail("这个按钮现在藏起来了，重新 screen");
        string what = (it.label != "" ? "「" + it.label + "」" : "") + it.kind;
        Event(how, Json.Field("n", it.n) + "," + Json.Field("label", what));
        var mb = it.go.GetComponent<mapBtn>();
        if (mb != null && TextPlay.Far(mb))
        {
            TextPlay.WalkThenPress(mb);
            return Ok("点了 " + it.n + " 号 " + what + "：离得远，人先走过去，到了自动点开");
        }
        Press(it.go, it.pt);
        Acted("刚点完");
        return Ok("点了 " + it.n + " 号 " + what + (it.off ? "（它是灰的，可能没反应）" : ""));
    }

    // 和鼠标点一样：移入（不出声）、点击、移出（不出声）
    internal static void Press(GameObject go, Vector2? at = null)
    {
        var ped = Ped(at ?? Center(go));
        Muted = true;
        try { ExecuteEvents.Execute(go, ped, ExecuteEvents.pointerEnterHandler); } catch { }
        Muted = false;
        Pressing++;  // 钩子看到举着，就知道这一下不是人点的
        try { ExecuteEvents.Execute(go, ped, ExecuteEvents.pointerClickHandler); }
        finally { Pressing--; }
        if (go != null && go.activeInHierarchy)
        {
            Muted = true;
            try { ExecuteEvents.Execute(go, ped, ExecuteEvents.pointerExitHandler); } catch { }
            Muted = false;
        }
    }

    // 点完先算忙，等画面重新稳定
    internal static void Acted(string what)
    {
        lastSig = null;
        published = false;
        SetPhase("busy", what);
        WriteState(true, Time.unscaledTime);
    }

    // 画面上找一个能点的东西（插件不点的不算）
    internal static GameObject FindClickable(System.Func<GameObject, bool> match)
    {
        int hidden;
        foreach (var it in Scan(out hidden))
            if (!it.blocked && match(it.go)) return it.go;
        return null;
    }

    // type：往画面上的输入框里打字（建角色时的名字）。游戏按「决定」时才读 InputField.text
    static string TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return Fail("要打什么字？");
        InputField target = null;
        foreach (var f in UnityEngine.Object.FindObjectsByType<InputField>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            Vector2 pt;
            if (!f.isActiveAndEnabled || !f.interactable || !Reachable(f.gameObject, out pt)) continue;
            target = f;
            break;
        }
        if (target == null) return Fail("画面上没有能打字的输入框");
        var before = target.text;
        target.text = text;
        try { target.onEndEdit.Invoke(target.text); } catch { }
        Event("type", Json.Field("text", target.text));
        Acted("打了字");
        return Ok("输入框：「" + before + "」→「" + target.text + "」" + (target.text != text ? "（输入框有字数限制）" : ""));
    }

    // ---- 写文件 ----
    static string ItemsJson()
    {
        return "\"snap\":" + snapId + "," + Json.Field("hidden", hiddenCount) + ",\"items\":[" + ItemList(snapshot) + "]";
    }

    static string ItemList(List<Item> items)
    {
        var parts = new List<string>();
        foreach (var it in items)
        {
            var sb = new StringBuilder("{");
            sb.Append(Json.Field("n", it.n)).Append(',').Append(Json.Field("label", it.label)).Append(',').Append(Json.Field("kind", it.kind));
            if (it.tip != "") sb.Append(',').Append(Json.Field("tip", it.tip));
            if (it.off) sb.Append(',').Append(Json.Field("off", true));
            if (it.note != "") sb.Append(',').Append(Json.Field("note", it.note));
            parts.Add(sb.Append('}').ToString());
        }
        return string.Join(",", parts.ToArray());
    }

    static void WriteState(bool force, float now)
    {
        var key = phase + "|" + why + "|" + snapId;
        if (!force && key == lastWritten && now < nextHeartbeat) return;
        lastWritten = key;
        nextHeartbeat = now + HeartbeatEvery;
        var sb = new StringBuilder("{");
        sb.Append(Json.Field("ts", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"))).Append(',')
          .Append(Json.Field("phase", phase)).Append(',').Append(Json.Field("why", why));
        if (phase != "off")
        {
            sb.Append(",\"status\":").Append(LudusLunaePlugin.StateJson(true));
            if (phase == "choose") sb.Append(",\"menu\":[").Append(ItemList(menu)).Append(']');
            if (phase == "click") sb.Append(',').Append(ItemsJson());
            if (phase == "click" && view != "") sb.Append(',').Append(Json.Field("view", view)).Append(',').Append(Json.Field("viewKind", TextPlay.ViewKind));
        }
        sb.Append("}");
        WriteAtomic(Path.Combine(Dir, "state.json"), sb.ToString());
    }

    // 先写临时文件再换过去，工具读到的总是完整的一份；换不过去（文件正被读）就直接覆盖
    static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch
        {
            try { File.WriteAllText(path, text, new UTF8Encoding(false)); }
            catch (Exception e) { LudusLunaePlugin.Log.LogWarning("写 " + Path.GetFileName(path) + " 失败：" + e.Message); }
        }
    }

    static void Reply(string id, string fields)
    {
        Append("outbox.jsonl", "{" + Json.Field("id", id) + "," + Json.Field("kind", "reply") + "," + fields + "}");
    }

    // 给工具看的事件流：只在 selfplay 开着时写
    internal static void Event(string type, string fields)
    {
        if (!On) return;
        Append("events.jsonl", "{" + Json.Field("ts", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")) + "," + Json.Field("type", type)
            + (fields.Length > 0 ? "," + fields : "") + "}");
    }

    static void Append(string file, string line)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, file), line + "\n", new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            LudusLunaePlugin.Log.LogWarning("写 " + file + " 失败：" + e.Message);
        }
    }
}

// 探悬停提示、模拟移入移出时不出声
[HarmonyPatch]
static class MutePatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(AudioSys), "PlaySound", new[] { typeof(int) });
        yield return AccessTools.Method(typeof(AudioSys), "PlayVoice", new[] { typeof(int) });
    }

    static bool Prefix() { return !SelfPlay.Muted; }
}

// 人点了哪个按钮：游戏里能点的东西都在这几个 OnPointerClick 上接鼠标（SelfPlay.IsClicker 认的也是这几种）。
// 插件自己点时举着 SelfPlay.Pressing，HumanClick 不记
[HarmonyPatch]
static class HumanClickPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(UiBtn), "OnPointerClick");
        yield return AccessTools.Method(typeof(Button), "OnPointerClick");
        yield return AccessTools.Method(typeof(mapBtn), "OnPointerClick");
        yield return AccessTools.Method(typeof(FightEnemy), "OnPointerClick");
        yield return AccessTools.Method(typeof(sceneMove), "OnPointerClick");
    }

    static void Prefix(MonoBehaviour __instance)
    {
        try { SelfPlay.HumanClick(__instance.gameObject); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记人点的按钮出错：" + e.Message); }
    }
}

// 人按空格翻台词、按住 Ctrl 快进（ChatSys.Update 里游戏自己读键盘，翻页走这两个方法）
[HarmonyPatch(typeof(ChatSys), "ClickToNextChat")]
static class HumanLinePatch
{
    static void Prefix()
    {
        try { SelfPlay.HumanLine(false); } catch { }
    }
}

[HarmonyPatch(typeof(ChatSys), "SkipToNextChat")]
static class HumanSkipPatch
{
    static void Prefix()
    {
        try { SelfPlay.HumanLine(true); } catch { }
    }
}
