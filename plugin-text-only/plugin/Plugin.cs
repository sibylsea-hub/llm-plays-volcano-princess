// Ludus Lunae · 火山的女儿插件（BepInEx 5，Unity 6 Mono）
//
// 陪玩同步：攒下这个月出现的每句台词和每次选项，换月（DataSys.TransTurn）时连同状态打成一行 JSON，
// 追加到聊天服务 data 文件夹里的 game-events.jsonl；agent 那边盯着这个文件，月底一次读完。
// 开关在同一个文件夹的 modes.json（面板上切）：selfplay = agent 自己玩（SelfPlay.cs、TextPlay.cs），关着就是 user 在玩、开陪玩同步；
// skipGames = 跳过小游戏（MiniGames.cs，谁玩都能开；没写时算开着）。陪玩同步没有自己的开关，selfplay 关着就开。
// 不走 ChatMenu 的选择（开局爸爸的身份、后来爸爸的目标和工作）也在这里记，开场动画的旁白（ShowSys.ShowLine）记成旁白。
// 所有挂钩都包在 try/catch 里：插件出错只写日志，不影响游戏。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

[BepInPlugin("ludus-lunae.volcano-princess", "Ludus Lunae", Version)]
public class LudusLunaePlugin : BaseUnityPlugin
{
    public const string Version = "0.3.7";
    internal static ManualLogSource Log;
    static ConfigEntry<string> dataDir;
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    // 这个月攒下的台词和选项，每条已经是一段 JSON
    static readonly List<string> month = new List<string>();
    // 上一次弹出的选项：按钮名 → 显示文字（点选时游戏只传回按钮名）
    static readonly Dictionary<string, string> menu = new Dictionary<string, string>();

    void Awake()
    {
        Log = Logger;
        dataDir = Config.Bind("General", "DataDir", BuildInfo.DataDir,
            "聊天服务的 data 文件夹：读 modes.json，写 game-events.jsonl（默认值是 build.bat 编进来的，仓库里的 chat-service/data）");
        new Harmony("ludus-lunae.volcano-princess").PatchAll(typeof(LudusLunaePlugin).Assembly);
        SelfPlay.Init(Config);
        Emit("plugin-loaded", Json.Field("version", Version));
        Log.LogInfo("Ludus Lunae 已加载，数据文件夹：" + dataDir.Value);
    }

    internal static string DataDir { get { return dataDir.Value; } }

    // ---- 开关：modes.json 由聊天服务写，改了才重读；没有这个文件时默认开陪玩同步、关自己玩 ----
    static DateTime modesStamp;
    static string modesText = "";

    // 陪玩同步：user 在玩（自己玩关着）时就开。agent 自己玩时它从 vp_player 看到同样的东西，不用再打月底包
    internal static bool Companion { get { return !Selfplay; } }
    internal static bool Selfplay { get { return Mode("selfplay", false); } }
    // 跳过小游戏：没写这个键时算开着，和聊天服务新建 modes.json 时写的默认一样（限时小游戏 agent 一步一步点来不及；面板上的开关、skip off 可以关）
    internal static bool SkipGames { get { return Mode("skipGames", true); } }

    // 看剧情的速度（面板上「看剧情」那根滑条）：自己玩时每秒读几个字，插件按这个翻台词、翻页；
    // 0 是自己翻（插件不翻）。没写时 12，和聊天服务新建 modes.json 时写的一样
    internal static float ReadCps { get { return Number("readCps", 12f); } }

    static string ModesText()
    {
        var path = Path.Combine(dataDir.Value, "modes.json");
        if (!File.Exists(path)) return "";
        var stamp = File.GetLastWriteTimeUtc(path);
        if (stamp != modesStamp)
        {
            modesText = File.ReadAllText(path, Utf8).Replace(" ", "");
            modesStamp = stamp;
        }
        return modesText;
    }

    static bool Mode(string key, bool fallback)
    {
        try
        {
            var text = ModesText();
            if (text.Contains("\"" + key + "\":true")) return true;
            if (text.Contains("\"" + key + "\":false")) return false;
        }
        catch (Exception e)
        {
            Log.LogWarning("读 modes.json 失败：" + e.Message);
        }
        return fallback;
    }

    static float Number(string key, float fallback)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(ModesText(), "\"" + key + "\":(-?[0-9]+(?:\\.[0-9]+)?)");
            if (m.Success) return float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        catch (Exception e)
        {
            Log.LogWarning("读 modes.json 失败：" + e.Message);
        }
        return fallback;
    }

    // ---- 攒这个月的内容（自己玩时同时写进 selfplay/events.jsonl）----
    internal static void AddLine(string speaker, string text)
    {
        var fields = Json.Field("who", speaker) + "," + Json.Field("say", text);
        if (Companion) { month.Add("{" + fields + "}"); packLines++; chainLines++; }
        SelfPlay.Event("line", fields);
    }

    // what：不是对话选项时写这是在选什么（比如「爸爸的身份」）
    internal static void AddMenu(List<string> names, List<string> texts, string what = null)
    {
        menu.Clear();
        for (int i = 0; i < names.Count; i++) menu[names[i]] = texts[i];
        var fields = (what == null ? "" : Json.Field("what", what) + ",") + Json.Field("options", texts.ToArray());
        if (Companion) { month.Add("{" + fields + "}"); packChoice = true; }
        SelfPlay.Event("options", fields);
    }

    // 插件自己的说明（比如跳过了小游戏）：只进陪玩同步的月底包，自己玩时另有事件
    internal static void AddNote(string text)
    {
        if (Companion) month.Add("{" + Json.Field("note", text) + "}");
    }

    // 最后一个经过 ChatSys.EnterMenuChoice 的选项按钮：点选项时游戏先调它再调 ClickMenuChoice(按钮名)。
    // 好几个选项常常共用一个按钮名（都通向同一段剧情），按名字查字会查成最后一个，所以优先用真点的那个按钮上的字
    internal static UnityEngine.Transform lastMenuTs;

    internal static void AddChoice(string name)
    {
        string shown = null;
        try
        {
            if (lastMenuTs != null && lastMenuTs.name == name)
            {
                var t = lastMenuTs.Find("txt");
                var text = t == null ? null : t.GetComponent<UnityEngine.UI.Text>();
                if (text != null && !string.IsNullOrEmpty(text.text)) shown = SelfPlay.Clean(text.text, 120);
            }
        }
        catch { }
        if (shown == null && !menu.TryGetValue(name, out shown)) shown = name;
        if (Companion) month.Add("{" + Json.Field("chose", shown) + "}");
        SelfPlay.Event("chose", Json.Field("text", shown));
    }

    // ---- 陪玩同步怎么分包：modes.json 的 packByScene（面板上「边玩边聊」，默认开）----
    // 开着：一段一段地发，agent 能跟着说感想，不用等到月底。
    //   · 一段对话（包括中间的选项）连着播完、有 8 句以上（村民的好感剧情、长剧情）：这一段单独一包，剧情中途换了地方也不拆开。
    //   · 离开一个地方：攒的有 6 句以上或者有选项，而且离上一包至少 1 分钟，就发一包；不够就留着，跟下一包一起发。
    //   · 换月照旧发月底的包（状态 + 剩下的台词）。
    // 关着：只在换月时发一包。
    internal static bool PackByScene { get { return Mode("packByScene", true); } }
    const int StoryLines = 8, PlaceLines = 6;
    const float PackGap = 60f;
    static int packLines, chainLines;
    static bool packChoice, chainOn, placePending;
    static float lastPack = -999f, lastTalk;
    static string pendingFrom, pendingTo;

    internal static void OnSceneChange(string from, string to)
    {
        if (!Companion || !PackByScene) return;
        if (!placePending) pendingFrom = from;
        pendingTo = to;
        placePending = true;  // 在 CompanionTick 里、对话不在播的时候再决定发不发
    }

    // 每 0.25 秒（自己玩开不开都跑）：talking = 对话在播、排着队或者选项开着
    internal static void CompanionTick(bool talking, float now)
    {
        if (!Companion || !PackByScene) { chainOn = false; placePending = false; return; }
        if (talking) { chainOn = true; lastTalk = now; return; }
        if (chainOn && now - lastTalk < 2f) return;  // 剧情中途换场景、拉幕时对话会停一下，停够 2 秒才算这段完了
        if (chainOn)
        {
            chainOn = false;
            bool story = chainLines >= StoryLines;
            chainLines = 0;
            if (story)
            {
                SendPack(true, now);
                return;
            }
        }
        if (placePending && (packLines >= PlaceLines || packChoice) && now - lastPack >= PackGap)
            SendPack(false, now);
    }

    static void SendPack(bool story, float now)
    {
        if (month.Count == 0) return;
        string here = "";
        try { here = TextPlay.PlaceName((int)MapSys.tempPlace); } catch { }
        var head = story
            ? Json.Field("story", true) + "," + Json.Field("at", here)
            : Json.Field("from", pendingFrom ?? here) + "," + Json.Field("to", pendingTo ?? here);
        Emit("scene", head + ",\"lines\":[" + string.Join(",", month.ToArray()) + "]");
        month.Clear();
        packLines = 0;
        packChoice = false;
        placePending = false;
        pendingFrom = pendingTo = null;
        lastPack = now;
    }

    // 换月或读档：把这个月攒的连同当前状态发出去，清空重来
    internal static void OnTransTurn(bool isLoading)
    {
        if (Companion)
            Emit(isLoading ? "load" : "month", "\"state\":" + StateJson() + ",\"lines\":[" + string.Join(",", month.ToArray()) + "]");
        month.Clear();
        packLines = chainLines = 0;
        packChoice = placePending = false;
        SelfPlay.Event(isLoading ? "load" : "month", "\"state\":" + StateJson(true));
    }

    // quiet：游戏还没进存档（标题画面）时读不到状态，自己玩每 5 秒问一次，不必每次都写日志
    internal static string StateJson(bool quiet = false)
    {
        var sb = new StringBuilder("{");
        try
        {
            var data = Traverse.Create(DataSys.Instan);
            var dau = Traverse.Create(DauSys.Instan);
            sb.Append(Json.Field("turn", data.Field("turn").GetValue<int>())).Append(',')
              .Append(Json.Field("year", DataSys.Year())).Append(',')
              .Append(Json.Field("month", DataSys.Month())).Append(',')
              .Append(Json.Field("coin", data.Field("coin").GetValue<int>())).Append(',')
              .Append(Json.Field("energy", DataSys.energy)).Append(',')
              .Append(Json.Field("maxEnergy", data.Field("maxEnergy").GetValue<int>())).Append(',')
              .Append(Json.Field("favor", DauSys.Instan.favor)).Append(',')
              .Append(Json.Field("darkness", dau.Field("darkness").GetValue<int>())).Append(',')
              .Append(Json.Field("inspiration", dau.Field("inspiration").GetValue<int>())).Append(',')
              .Append(Json.Field("dauName", DauSys.Instan.dauName)).Append(',')
              .Append(Json.Field("faName", DauSys.Instan.faName)).Append(',')
              .Append(Json.Field("height", Math.Round(DauSys.Instan.height, 1))).Append(',')
              .Append(Json.Field("fame", dau.Field("fame").GetValue<int>())).Append(',')
              .Append(Json.Field("mood", dau.Field("mood").GetValue<int>())).Append(',')
              .Append(Json.Field("lover", dau.Field("lover").GetValue<int>())).Append(',')
              .Append(Named("nature", DataSys.Instan.dataCfg.natureCh, dau.Field("nature").GetValue<int[]>())).Append(',')
              .Append(Named("inAttri", Constant.inAttriCh, dau.Field("inAttri").GetValue<int[]>()));
            try
            {
                sb.Append(',').Append(Json.Field("talentPoint", dau.Field("talentPoint").GetValue<int>()));
                sb.Append(',').Append(Json.Field("yearCourse", Traverse.Create(CourseSys.Instan).Field("yearFinishCourse").GetValue<int>()))
                  .Append(',').Append(Json.Field("yearCourseGoal", Constant.courseTarget[DataSys.Stage()]));
                var states = new List<string>();
                foreach (var i in dau.Field("ownState").GetValue<List<int>>())
                {
                    var a = DataSys.Instan.dataCfg.achievements[i];
                    states.Add(ChatSys.Txt(a.chName));
                }
                sb.Append(',').Append(Json.Field("states", states.ToArray()));
            }
            catch { }
        }
        catch (Exception e)
        {
            if (!quiet) Log.LogWarning("读状态失败：" + e.Message);
            sb.Length = 1;
            sb.Append(Json.Field("error", e.Message));
        }
        return sb.Append('}').ToString();
    }

    // "nature":{"体魄":2857,"智力":3496,…}
    static string Named(string key, string[] names, int[] values)
    {
        var parts = new List<string>();
        for (int i = 0; i < values.Length; i++)
            parts.Add(Json.Field(names != null && i < names.Length ? names[i] : i.ToString(), values[i]));
        return Json.Str(key) + ":{" + string.Join(",", parts.ToArray()) + "}";
    }

    static void Emit(string type, string fields)
    {
        try
        {
            var line = "{" + Json.Field("ts", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)) + ","
                + Json.Field("game", "volcano-princess") + "," + Json.Field("type", type)
                + (fields.Length > 0 ? "," + fields : "") + "}\n";
            File.AppendAllText(Path.Combine(dataDir.Value, "game-events.jsonl"), line, Utf8);
        }
        catch (Exception e)
        {
            Log.LogWarning("写 game-events.jsonl 失败：" + e.Message);
        }
    }
}

// 每句台词开头都会经过 CheckControl；普通台词（LogType.normal）才是要显示的话
[HarmonyPatch(typeof(ChatSys), "CheckControl")]
static class ChatLinePatch
{
    static void Postfix(Chat chat, LogType __result)
    {
        if (chat == null) return;
        try
        {
            if (__result == LogType.normal)
            {
                var text = Func.ChatRe(ChatSys.Txt(chat.content));
                LudusLunaePlugin.AddLine(Speaker(chat.name), text);
                SelfPlay.OnChatStep(__result, text);
            }
            else SelfPlay.OnChatStep(__result, "");
        }
        catch (Exception e)
        {
            LudusLunaePlugin.Log.LogWarning("记台词失败：" + e.Message);
        }
    }

    // 和 ChatSys.StartChatEnu 里定说话人的逻辑一样
    static string Speaker(string name)
    {
        switch (name)
        {
            case "dau": return DauSys.Instan.dauName;
            case "fa": return DauSys.Instan.faName;
            case "aside": return "旁白";
        }
        var cfg = DataSys.Instan.dataCfg;
        if (cfg.npcDic.ContainsKey(name)) return NpcSys.GetNpc(name).chName;
        int index;
        if (int.TryParse(name, out index) && index >= 0 && index < cfg.people.Length) return cfg.people[index].chName;
        if (name.Length == 5) return ChatSys.Txt(name);
        return name;
    }
}

// 弹出选项：显示文字是 ChatRe(selectName)，按钮名是 tsName；open 为 false 或文字为空的不显示
[HarmonyPatch(typeof(ChatSys), "ChatMenu", new[] { typeof(string[]), typeof(string[]), typeof(ChatSys.MenuState), typeof(bool[]) })]
static class MenuPatch
{
    static void Postfix(string[] tsName, string[] selectName, bool[] open)
    {
        try
        {
            var names = new List<string>();
            var texts = new List<string>();
            for (int i = 0; i < tsName.Length && i < selectName.Length; i++)
            {
                if ((open != null && i < open.Length && !open[i]) || string.IsNullOrEmpty(selectName[i])) continue;
                names.Add(tsName[i]);
                texts.Add(Func.ChatRe(selectName[i]));
            }
            LudusLunaePlugin.AddMenu(names, texts);
        }
        catch (Exception e)
        {
            LudusLunaePlugin.Log.LogWarning("记选项失败：" + e.Message);
        }
    }
}

[HarmonyPatch(typeof(ChatSys), "EnterMenuChoice")]
static class MenuEnterPatch
{
    static void Prefix(UnityEngine.Transform ts) { LudusLunaePlugin.lastMenuTs = ts; }
}

[HarmonyPatch(typeof(ChatSys), "ClickMenuChoice")]
static class ChoicePatch
{
    static void Prefix(string name)
    {
        try { LudusLunaePlugin.AddChoice(name); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记选择失败：" + e.Message); }
    }
}

// 开局「你要成为什么样的爸爸」：TaskSys 自己的窗口，不走 ChatMenu。选项文字在 Constant.dauWishCh
[HarmonyPatch(typeof(TaskSys), "ShowDauWishChoice")]
static class DauWishMenuPatch
{
    static void Postfix(bool open)
    {
        if (!open) return;
        try
        {
            var names = new List<string>();
            var texts = new List<string>();
            for (int i = 0; i < Constant.dauWishCh.Length; i++)
            {
                names.Add("dauWish#" + i);
                // 每种爸爸看重的两项品质，和游戏里愿望窗口的写法一样
                texts.Add(Constant.dauWishCh[i] + "（" + Constant.dauWishNatureCh[1 + i] + " + " + Constant.dauWishNatureCh[0] + "）");
            }
            LudusLunaePlugin.AddMenu(names, texts, "爸爸的身份");
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记爸爸身份选项失败：" + e.Message); }
    }
}

[HarmonyPatch(typeof(TaskSys), "ChooseDauWish")]
static class DauWishChoicePatch
{
    static void Prefix(int index)
    {
        try { LudusLunaePlugin.AddChoice("dauWish#" + index); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记爸爸身份失败：" + e.Message); }
    }
}

// 后来的「爸爸的目标」（剧情命令 showChooseFaWishStage2），同样不走 ChatMenu
[HarmonyPatch(typeof(TaskSys), "ShowChooseFaWish")]
static class FaWishMenuPatch
{
    static void Postfix()
    {
        try
        {
            var names = new List<string>();
            for (int i = 0; i < Constant.faTargetCh.Length; i++) names.Add("faWish#" + i);
            LudusLunaePlugin.AddMenu(names, new List<string>(Constant.faTargetCh), "爸爸的目标");
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记爸爸目标选项失败：" + e.Message); }
    }
}

[HarmonyPatch(typeof(TaskSys), "ChooseFaWish")]
static class FaWishChoicePatch
{
    static void Prefix(int index)
    {
        try { LudusLunaePlugin.AddChoice("faWish#" + index); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记爸爸目标失败：" + e.Message); }
    }
}

// 爸爸的工作：打开窗口时随机出几份，确认后才算选定（SureFaChooseWork）
[HarmonyPatch(typeof(DauSys), "OpenFaWorkWnd")]
static class FaWorkMenuPatch
{
    static void Postfix(DauSys __instance)
    {
        try
        {
            var shown = Traverse.Create(__instance).Field<List<int>>("showingWork").Value;
            var names = new List<string>();
            var texts = new List<string>();
            foreach (var w in shown)
            {
                names.Add("faWork#" + w);
                texts.Add(DataSys.Instan.dataCfg.faWorks[w].chName);
            }
            LudusLunaePlugin.AddMenu(names, texts, "爸爸的工作");
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记爸爸工作选项失败：" + e.Message); }
    }
}

[HarmonyPatch(typeof(DauSys), "SureFaChooseWork")]
static class FaWorkChoicePatch
{
    static void Prefix(DauSys __instance)
    {
        try
        {
            var t = Traverse.Create(__instance);
            var shown = t.Field<List<int>>("showingWork").Value;
            LudusLunaePlugin.AddChoice("faWork#" + shown[t.Field<int>("tempChooseWorkIndex").Value]);
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记爸爸工作失败：" + e.Message); }
    }
}

// 开场动画（和结局之类的过场）的旁白不走 ChatSys，走 ShowSys.ShowLine；同一句会连着取两次，去重
[HarmonyPatch(typeof(ShowSys), "ShowLine")]
static class ShowLinePatch
{
    static string last;

    static void Postfix(string __result)
    {
        try
        {
            if (string.IsNullOrEmpty(__result) || __result == last) return;
            last = __result;
            LudusLunaePlugin.AddLine("旁白", __result);
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记过场旁白失败：" + e.Message); }
    }
}

// 得到物品（礼物、奖励、探险收获……）：自己玩时记一条事件，工具打印出来，不用去翻背包
[HarmonyPatch(typeof(ItemSys), "AddItem", new[] { typeof(int), typeof(int), typeof(bool), typeof(bool) })]
static class ItemPatch
{
    static void Postfix(int index, int num)
    {
        if (!SelfPlay.On || num == 0) return;
        try
        {
            var item = DataSys.Instan.dataCfg.items[index];
            SelfPlay.Event("item", Json.Field("name", ChatSys.Txt(item.chName)) + "," + Json.Field("num", num));
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记物品失败：" + e.Message); }
    }
}

// 选领主：确认那一刻游戏用的是 tempChooseLord（中间又点了别的就会变），实际效忠的是谁记一条，免得和确认框对不上
[HarmonyPatch(typeof(TaskSys), "SureChooseLord")]
static class LordChoicePatch
{
    static void Prefix(TaskSys __instance)
    {
        try
        {
            int i = Traverse.Create(__instance).Field<int>("tempChooseLord").Value;
            LudusLunaePlugin.AddChoice("效忠：" + DataSys.Instan.dataCfg.lords[i].chName);
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记领主失败：" + e.Message); }
    }
}

[HarmonyPatch(typeof(DataSys), "TransTurn")]
static class TurnPatch
{
    static void Postfix(bool isLoading)
    {
        try { LudusLunaePlugin.OnTransTurn(isLoading); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("换月打包失败：" + e.Message); }
    }
}

static class Json
{
    public static string Str(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    public static string Field(string key, string value) { return Str(key) + ":" + Str(value); }
    public static string Field(string key, long value) { return Str(key) + ":" + value.ToString(CultureInfo.InvariantCulture); }
    public static string Field(string key, double value) { return Str(key) + ":" + value.ToString(CultureInfo.InvariantCulture); }
    public static string Field(string key, bool value) { return Str(key) + ":" + (value ? "true" : "false"); }

    public static string Field(string key, string[] values)
    {
        var parts = new string[values == null ? 0 : values.Length];
        for (int i = 0; i < parts.Length; i++) parts[i] = Str(values[i]);
        return Str(key) + ":[" + string.Join(",", parts) + "]";
    }
}
