// Ludus Lunae · 火山的女儿插件：谁在玩、东西写进谁的文件夹
//
// 设计文档 docs/设计理念.md 第十一节「一个玩家一个文件夹」。为什么分：inbox、outbox、events、印象簿、截图要是只有一份，
// 换一个 agent 新开一局时，印象簿会把上一个 agent 养女儿时说的话当成它自己说的带回来。
// 存档还是游戏自己管，插件不替谁存、读；这里只管插件写的东西归谁。
//
// 和服务端、hub、vp_player、vp_memory 对齐的约定（都在 data/selfplay/ 下）：
//   players/<文件夹>/  每个玩家自己的 inbox.jsonl、outbox.jsonl、events.jsonl、impressions.json、scenes/、autofight.jsonl
//                      （工具那边还有 memory.json、feedback.jsonl、cursor.json）
//   players.json       搬家脚本建，服务端维护：{"players": {"名字": {"dir": "players/…", "first": …}}}。这里只看它在不在：
//                      在，就按玩家分文件夹；不在，就都写在 data/selfplay/ 下，不分玩家。这是和工具一起换的开关：
//                      关了游戏、装上插件、跑搬家，players.json 一出现，插件和工具同一刻换，不会一边新一边旧。
//   active.json        聊天服务交班时写：{"player","dir","since","inbox"}，player 为空就是没人在玩；inbox 是交班那一刻
//                      这个人 inbox.jsonl 的字节数（可以没有）。像 modes.json 那样，改了才重读。
//   state.json、modes.json 照旧共用（现在的画面只有一个）。陪玩同步的 game-events.jsonl 在 data/ 下，也不动。
//
// 读命令：每个玩家的 inbox 都一直看着（插件加载时已经在里面的不管）。
//   现在在玩的那一个：照常执行。
//   别的玩家、没人在玩时：只回答只看不动的命令（look、acts、plan……，和 vp_player「只看的命令谁都能跑」对上）和 hello，
//   动游戏的回一句现在在玩的是谁。回话写进发命令的那个人的 outbox。
//   所以换人的时候，新玩家 inbox 里换人以前的命令早都回过了，不会被执行；换人之后马上写进来的 hello 也不会被当成旧的跳过
//   （聊天服务写 active.json 和工具写 hello 之间只差几毫秒，插件 0.25 秒才看一次，只「从插件看到换人那一刻的末尾读」会漏掉它）。
//   active.json 带着 inbox（交班那一刻的字节数）时，换人以后从它和插件已经读到的位置里靠后的那个读起，照约定不执行交班前的。
// 写东西：事件、回话以外的都写进现在在玩的那个人的文件夹（事件、第一次见的截图和印象簿条目、换场景的截图、托管记录）。
//   交班的空当（没人在玩）接着写上一个在玩的人的；插件加载以来还没人玩过就不写，不往共用的地方混。
// 换了一个人：Sights 清空，当新会话。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

[Serializable]
public class LudusActiveFile
{
    public string player = "";
    public string dir = "";
    public string since = "";
    public long inbox = -1;  // 交班那一刻 inbox 的字节数：换人以后从这里（或者插件已经读到的更后面）读起
}

static class Players
{
    internal class Box
    {
        public string dir;      // 完整路径
        public long offset;     // inbox.jsonl 读到哪了
        public bool active;     // 现在在玩的是它（没分文件夹时，唯一的那个总是 true）
    }

    static string Root { get { return SelfPlay.Dir; } }
    static string PlayersRoot { get { return Path.Combine(Root, "players"); } }

    static bool split, splitKnown;
    static string activeName = "", activeDir, lastDir;
    static long pendingInbox = -1;  // 刚换到的人，交班时 inbox 读到哪（Scan 里用上）
    static DateTime activeStamp;
    static bool activeRead;
    static readonly Dictionary<string, Box> boxes = new Dictionary<string, Box>(StringComparer.OrdinalIgnoreCase);
    static bool firstScan = true;
    static string lastWarn;

    // 读命令时「只看不动」的：别的玩家、没人在玩时也回答。shot 不在这里：不在玩的人 shot 只截图，不发清单（SelfPlay.LookShot）
    static readonly HashSet<string> lookOnly = new HashSet<string> { "ping", "look", "lookname", "acts", "plan", "wish", "home", "read", "fight" };

    internal static bool Split { get { return split; } }
    internal static string Active { get { return activeName; } }

    // 事件、截图、印象簿写到哪：没分文件夹就是 data/selfplay/；分了就是在玩的（或者上一个在玩的）那个人的文件夹；都没有是 null
    internal static string WriteDir { get { return !split ? Root : (activeDir ?? lastDir); } }

    static bool Same(string a, string b) { return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

    static void Warn(string text)
    {
        if (text == lastWarn) return;
        lastWarn = text;
        LudusLunaePlugin.Log.LogWarning(text);
    }

    // ---- 每 0.25 秒（SelfPlay.Tick 读命令之前） ----
    internal static void Tick()
    {
        bool s = File.Exists(Path.Combine(Root, "players.json"));
        if (!splitKnown || s != split)
        {
            splitKnown = true;
            split = s;
            boxes.Clear();
            firstScan = true;
            activeRead = false;
            activeName = "";
            activeDir = lastDir = null;
            LudusLunaePlugin.Log.LogInfo(split ? "按玩家分文件夹（data/selfplay/players.json 在）" : "没分文件夹，都写在 data/selfplay/ 下（没有 players.json）");
        }
        if (split) ReadActive();
        Scan();
        firstScan = false;
    }

    static void ReadActive()
    {
        var path = Path.Combine(Root, "active.json");
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (activeRead && stamp == activeStamp) return;
        string name = "", dir = null;
        long inboxAt = -1;
        if (stamp != DateTime.MinValue)
        {
            try
            {
                var a = JsonUtility.FromJson<LudusActiveFile>(File.ReadAllText(path, Encoding.UTF8));
                name = a == null ? "" : (a.player ?? "").Trim();
                if (name != "")
                {
                    dir = Resolve(a.dir);
                    inboxAt = a.inbox;
                    if (dir == null) { Warn("active.json 里的 dir 不对（要是 players/ 下面的文件夹）：" + a.dir); name = ""; }
                }
            }
            catch (Exception e)
            {
                Warn("读 active.json 失败（下一轮再读）：" + e.Message);
                return;  // 可能正写到一半：不记时间戳，下一轮重读
            }
        }
        activeRead = true;
        activeStamp = stamp;
        if (!Same(dir, activeDir) && !(dir == null && activeDir == null))
        {
            if (dir != null && !Same(dir, lastDir)) Sights.NewPlayer();  // 换了一个人：当新会话。同一个人交班又回来不算
            activeDir = dir;
            if (dir != null) lastDir = dir;
            pendingInbox = dir == null ? -1 : inboxAt;
            LudusLunaePlugin.Log.LogInfo("现在在玩：" + (name == "" ? "没人" : name + "（" + dir + "）"));
        }
        activeName = name;
    }

    // active.json 的 dir 只认 players/ 下面的文件夹
    static string Resolve(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        var d = dir.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (d == "" || Path.IsPathRooted(d) || d.Contains("..")) return null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(Root, d)).TrimEnd(Path.DirectorySeparatorChar);
            var under = Path.GetFullPath(PlayersRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(under, StringComparison.OrdinalIgnoreCase) && full.Length > under.Length ? full : null;
        }
        catch { return null; }
    }

    // 看看有哪些 inbox：没分文件夹时只有 data/selfplay/inbox.jsonl；分了就是 players/ 下每个文件夹的
    static void Scan()
    {
        var dirs = new List<string>();
        if (!split) dirs.Add(Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar));
        else
        {
            try
            {
                if (Directory.Exists(PlayersRoot))
                    foreach (var d in Directory.GetDirectories(PlayersRoot)) dirs.Add(Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar));
            }
            catch (Exception e) { Warn("看 players/ 失败：" + e.Message); }
            if (activeDir != null && !dirs.Exists(d => Same(d, activeDir))) dirs.Add(activeDir);  // 文件夹还没建
        }
        var gone = new List<string>();
        foreach (var k in boxes.Keys) if (!dirs.Exists(d => Same(d, k))) gone.Add(k);
        foreach (var k in gone) boxes.Remove(k);  // 清空玩家时整个文件夹挪去档案了
        foreach (var d in dirs)
        {
            Box b;
            if (!boxes.TryGetValue(d, out b))
            {
                b = new Box { dir = d, offset = StartAt(d) };
                boxes[d] = b;
            }
            b.active = !split || Same(d, activeDir);
            // 刚换到它：交班那一刻以前写进来的不执行（一直在读，一般早就读过了；插件刚加载、文件夹刚出现时靠这个）
            if (b.active && split && pendingInbox >= 0)
            {
                long size = 0;
                try { var f = new FileInfo(Path.Combine(d, "inbox.jsonl")); if (f.Exists) size = f.Length; } catch { }
                b.offset = Math.Max(b.offset, Math.Min(pendingInbox, size));
                pendingInbox = -1;
            }
        }
    }

    // 第一次看到一个 inbox 从哪读起：插件刚加载时，里面已有的都是旧命令，不管；
    // 之后才出现的文件夹（新玩家第一次来），10 秒内写的算新的，更早的（搬进来的旧文件）不管
    static long StartAt(string dir)
    {
        try
        {
            var f = new FileInfo(Path.Combine(dir, "inbox.jsonl"));
            if (!f.Exists) return 0;
            if (firstScan) return f.Length;
            return (DateTime.UtcNow - f.LastWriteTimeUtc).TotalSeconds < 10 ? 0 : f.Length;
        }
        catch { return 0; }
    }

    internal static List<Box> Boxes() { return new List<Box>(boxes.Values); }

    // 这条命令这个人现在能不能跑：能跑返回 null，不能就是回给它的话
    internal static string Refuse(Box box, SelfPlayCmd c)
    {
        if (box.active) return null;
        if (lookOnly.Contains(c.cmd) || (c.cmd == "talent" && (c.text ?? "").Trim() == "")) return null;
        return activeName == ""
            ? "现在没人在玩：要动游戏先 hello"
            : "现在在玩的是「" + activeName + "」：这个只能看，要动游戏先 hello 接手";
    }
}
