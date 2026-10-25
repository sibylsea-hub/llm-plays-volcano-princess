// Ludus Lunae · 火山的女儿插件：自己玩时跳过小游戏
//
// 只在 modes.json 的 skipGames 开着时动作（面板上的「跳过小游戏」；user 自己玩时也能开）。没有这个键时算开着（聊天服务新建 modes.json 时也写开着：限时小游戏 agent 一步一步点来不及）。
// 每个小游戏都在「开始玩」的那一刻截住，直接给出完美玩家能拿到的结果：
//   骰子赢（押默认的一半）、赛马第一、舞会把舞伴聊满、演戏三句全对、鸟节画画第一名、钓鱼鱼全点到垃圾不碰、鸟节算术全对。
// 然后接回游戏自己的结算：结算对话、奖励、结果窗口照常出现，结果窗口上的按钮留给 agent 点。
// 不跳的：骏马选美（没有操作，只有一次对话选项）、鸟节转盘（纯运气）、探险里的战斗、抓马（回合制选项），这些让 agent 自己点。
// 跳过出错就退回游戏原来的玩法（prefix 返回 true），不会卡住。
//
// Steam 成就：CollectSys.DoneAchievement 会调 GameManager.SetSteamAchievement 点亮 user 的成就。
// 从跳过开始到这个小游戏结算完（再加 2 秒），所有新成就一律挡下，连本地记录也不写，和游戏里「已经拿过」的分支一样；
// 这期间排进庆祝队列的状态成就（DauSys.AddState）记下编号，之后轮到它显示时照样挡下。
// 鸟节三项（画画、钓鱼、算术）后面还连着领礼物的步骤，挡到换月为止。SetSteamAchievement 本身也加了一道闸。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

static class MiniGames
{
    static float cheatUntil;  // 这个时刻之前挡成就
    static bool untilMonthEnd;
    internal static readonly HashSet<int> Tainted = new HashSet<int>();

    internal static bool CheatActive { get { return Time.unscaledTime < cheatUntil; } }

    internal static bool On { get { return LudusLunaePlugin.SkipGames; } }

    internal static void Begin(bool tillMonthEnd = false)
    {
        cheatUntil = float.MaxValue;
        untilMonthEnd = untilMonthEnd || tillMonthEnd;
    }

    // 这个小游戏结算完了：再挡 2 秒，等结算的协程跑完
    internal static void End()
    {
        if (cheatUntil == float.MaxValue && !untilMonthEnd) cheatUntil = Time.unscaledTime + 2f;
    }

    internal static void MonthEnd()
    {
        if (cheatUntil == float.MaxValue) cheatUntil = Time.unscaledTime + 2f;
        untilMonthEnd = false;
    }

    internal static void Skipped(string game, string result)
    {
        LudusLunaePlugin.Log.LogInfo("跳过小游戏：" + game + "，" + result);
        SelfPlay.Event("skip", Json.Field("game", game) + "," + Json.Field("result", result));
        LudusLunaePlugin.AddNote("跳过小游戏：" + game + "，" + result);  // 陪玩同步时月底的包里也看得到
    }

    // 跳过失败：退回原来的玩法
    internal static bool Fail(string game, Exception e)
    {
        LudusLunaePlugin.Log.LogWarning("跳过" + game + "失败，照原样玩：" + e);
        SelfPlay.Event("skip-failed", Json.Field("game", game) + "," + Json.Field("error", e.Message));
        End();
        return true;
    }

    internal static IEnumerator Nothing() { yield break; }

    internal static void Call(object target, string method, params object[] args)
    {
        var m = AccessTools.Method(target.GetType(), method);
        if (m == null) throw new MissingMethodException(target.GetType().Name, method);
        try { m.Invoke(target, args); }
        catch (TargetInvocationException e) { throw e.InnerException ?? e; }
    }
}

// ---- 成就保护 ----
[HarmonyPatch(typeof(CollectSys), "DoneAchievement", new[] { typeof(Achievement), typeof(string[]) })]
static class AchievementGuard
{
    static readonly AccessTools.FieldRef<CollectSys, bool[]> done = AccessTools.FieldRefAccess<CollectSys, bool[]>("doneAchievements");

    static bool Prefix(CollectSys __instance, Achievement achiv)
    {
        if (achiv == null) return true;
        bool cheat = MiniGames.CheatActive;
        try
        {
            if (done(__instance)[achiv.index]) return true;  // 已经拿过，游戏自己什么都不做
            bool tainted = MiniGames.Tainted.Remove(achiv.index);
            if (cheat || tainted)
            {
                LudusLunaePlugin.Log.LogInfo("挡下成就 " + achiv.index + "（" + achiv.chName + "）：跳小游戏时得到的");
                SelfPlay.Event("achievement-blocked", Json.Field("id", achiv.index) + "," + Json.Field("name", achiv.chName));
                return false;
            }
            SelfPlay.Event("achievement", Json.Field("id", achiv.index) + "," + Json.Field("name", achiv.chName));
        }
        catch (Exception e)
        {
            LudusLunaePlugin.Log.LogWarning("成就保护出错：" + e.Message);
        }
        return !cheat;
    }
}

[HarmonyPatch(typeof(GameManager), "SetSteamAchievement")]
static class SteamAchievementGuard
{
    static bool Prefix(int index)
    {
        if (!MiniGames.CheatActive) return true;
        LudusLunaePlugin.Log.LogInfo("挡下 Steam 成就 achi_" + index);
        return false;
    }
}

// 作弊期间排进庆祝队列的状态成就，之后显示时也要挡
[HarmonyPatch(typeof(DauSys), "AddState")]
static class StateTaint
{
    static void Prefix(Achievement state)
    {
        try
        {
            if (MiniGames.CheatActive && state != null) MiniGames.Tainted.Add(state.index);
        }
        catch { }
    }
}

[HarmonyPatch(typeof(DataSys), "TransTurn")]
static class CheatMonthEnd
{
    static void Postfix()
    {
        try { MiniGames.MonthEnd(); } catch { }
    }
}

// ---- 骰子：押注条和挑骰子都跳过，直接赢 ----
[HarmonyPatch(typeof(DiceSys), "ShowDiceBet")]
static class DiceBetSkip
{
    static bool Prefix(DiceSys __instance)
    {
        if (!MiniGames.On) return true;
        try
        {
            // 押注条默认在正中间，就押这么多
            int bet = Constant.diceCost[0] + (int)(0.5f * (Constant.diceCost[1] - Constant.diceCost[0]));
            Traverse.Create(__instance).Field("betCoin").SetValue(bet);
            Traverse.Create(__instance).Field("isBeting").SetValue(false);
            return DiceSkip.Win(__instance, "押 " + bet + " 金币，赢了");
        }
        catch (Exception e) { return MiniGames.Fail("骰子", e); }
    }
}

[HarmonyPatch(typeof(DiceSys), "ReadyForBet")]
static class DiceSkip
{
    static bool Prefix(DiceSys __instance)
    {
        if (!MiniGames.On) return true;
        return Win(__instance, "赢了");
    }

    // 返回 false = 跳过成功，不跑原方法
    internal static bool Win(DiceSys dice, string result)
    {
        try
        {
            var t = Traverse.Create(dice);
            bool bird = t.Field<bool>("isBirdCompete").Value;
            var received = t.Field<List<int>>("receivedItems").Value;
            MiniGames.Begin();
            received.Clear();
            if (bird)
            {
                // 鸟节三局两胜：记成已经赢了一局，这一局再赢就结束
                t.Field("winTime").SetValue(1);
                t.Field("totalTime").SetValue(1);
            }
            MiniGames.Call(dice, "ShowResult", false);
            MiniGames.Skipped(bird ? "鸟节骰子" : "骰子", result);
            return false;
        }
        catch (Exception e) { return MiniGames.Fail("骰子", e); }
    }
}

[HarmonyPatch(typeof(DiceSys), "EndDice")]
static class DiceEnd
{
    static void Postfix() { MiniGames.End(); }
}

// ---- 赛马（速度赛）：不跑，直接按自己的马第一个到终点结算 ----
[HarmonyPatch(typeof(HorseSys), "RacinoEnu")]
static class RaceSkip
{
    static bool Prefix(HorseSys __instance, ref IEnumerator __result)
    {
        if (!MiniGames.On) return true;
        try
        {
            var t = Traverse.Create(__instance);
            var all = t.Field<List<Horse>>("racinoHorses").Value;
            var mine = t.Field<Horse>("racinoHorse").Value;
            var order = t.Field<List<Horse>>("toEndHorse").Value;
            int type = t.Field<int>("competeType").Value;
            if (mine == null || all == null || order == null) return true;
            MiniGames.Begin();
            order.Clear();
            order.Add(mine);
            foreach (var h in all) if (h != mine) order.Add(h);
            t.Field("isRacing").SetValue(false);
            __result = Finish(type);
            MiniGames.Skipped("赛马", "「" + mine.name + "」第一名");
            return false;
        }
        catch (Exception e) { return MiniGames.Fail("赛马", e); }
    }

    // 和 HorseSys.RacinoToEnd 里所有马到终点后的那两句一样
    static IEnumerator Finish(int type)
    {
        yield return new WaitForSeconds(0.5f);
        ChatSys.StartChat(348 + type, ChatDic.personChat);
        ChatSys.StartChat("command", "endRacino");
    }
}

[HarmonyPatch(typeof(HorseSys), "EndRacinoBtn")]
static class RaceEnd
{
    static void Postfix() { MiniGames.End(); }
}

// ---- 舞会：选了舞伴就直接把对方聊满，弹结果 ----
[HarmonyPatch(typeof(FeastSys), "ChooseDancingPer")]
static class DanceSkip
{
    static bool Prefix(FeastSys __instance, int branch)
    {
        if (!MiniGames.On) return true;
        try
        {
            var t = Traverse.Create(__instance);
            var cfg = DataSys.Instan.dataCfg;
            int place = t.Field<int>("chooseDacingPlace").Value;
            var pers = t.Field<int[,]>("tempDancingPers").Value;
            var per = cfg.dancingPers[pers[place, branch]];
            // 第一次见 14 号舞伴有专门的剧情，这次照常玩
            if (place == 2 && branch == 0 && cfg.dancingPers[14].talkCount == 0)
            {
                SelfPlay.Event("skip-not", Json.Field("game", "舞会") + "," + Json.Field("why", "第一次见这位舞伴有剧情，照常玩"));
                return true;
            }
            var ts = UiSys.Instan.uiTrans[53].Find("panel").Find("start");
            MiniGames.Begin();
            DataSys.AddEnergy(-Constant.dancingCost);
            t.Field("tempDancingPer").SetValue(per);
            int vigour = Func.Sum(DauSys.InAttriNums()) + 2 * DauSys.InAttriNum(2);
            t.Field("dauVigour").SetValue(vigour);
            t.Field("dauMaxVigour").SetValue(vigour);
            t.Field("perMaxVigour").SetValue(per.vigour);
            t.Field("perVigour").SetValue(0);
            // 照 StartDancingChatEnu 摆好画面（舞伴立绘、名字），但不进对话
            Func.BtnInter(ts.parent.parent.Find("bg"), inter: false);
            string portrait = "portrait/" + per.character;
            if (new List<string> { "gwn", "ken", "45" }.Contains(per.character) && DataSys.Stage() == 3) portrait += "Winter";
            Func.SetImg(ts.Find("bg").Find("per"), portrait, nativeSize: true);
            Func.Texts(ts.Find("ui"), "name#level", new string[2] { per.chName, Func.Strs("★", per.level + 1) });
            ts.Find("ui").gameObject.SetActive(false);
            ts.Find("card").gameObject.SetActive(false);
            ts.Find("bg").Find("title").gameObject.SetActive(false);
            Func.OpenOnly(ts.parent, ts.name);
            __instance.ShowDancingResult();
            MiniGames.Skipped("舞会", "和" + per.chName + "聊满了");
            return false;
        }
        catch (Exception e) { return MiniGames.Fail("舞会", e); }
    }
}

[HarmonyPatch(typeof(FeastSys), "CloseDancingResultBtn")]
static class DanceEnd
{
    static void Postfix() { MiniGames.End(); }
}

// ---- 演戏：开场动画放完，直接按三句台词全对结算 ----
[HarmonyPatch(typeof(DauSys), "DramaPerform")]
static class DramaSkip
{
    static void Postfix(DauSys __instance, ref IEnumerator __result)
    {
        if (!MiniGames.On) return;
        __result = Wrap(__instance, __result);
    }

    static IEnumerator Wrap(DauSys dau, IEnumerator inner)
    {
        while (true)
        {
            object current;
            try
            {
                if (!inner.MoveNext()) break;
                current = inner.Current;
            }
            catch (Exception e)
            {
                LudusLunaePlugin.Log.LogWarning("演戏开场出错：" + e.Message);
                yield break;
            }
            yield return current;
        }
        if (!MiniGames.On) yield break;
        try
        {
            var t = Traverse.Create(dau);
            MiniGames.Begin();
            t.Field("gameRate").SetValue(1.3f);  // 起始 1，每答对一句 +0.1
            t.Field("lineIndex").SetValue(3);
            t.Field("isClickingLine").SetValue(true);
            dau.StartCoroutine(dau.ShowDramaResult());
            MiniGames.Skipped("演戏", "三句台词全对");
        }
        catch (Exception e) { MiniGames.Fail("演戏", e); }
    }
}

[HarmonyPatch(typeof(DauSys), "EndDramaPerformBtn")]
static class DramaEnd
{
    static void Postfix() { MiniGames.End(); }
}

// ---- 鸟节画画比赛：直接第一名（7 张礼券），接回比赛结束 ----
[HarmonyPatch(typeof(DauSys), "StartBirdPaintingCompete")]
static class PaintSkip
{
    static bool Prefix(DauSys __instance)
    {
        if (!MiniGames.On) return true;
        try
        {
            if (AccessTools.Method(typeof(DauSys), "EndPaintCompete") == null) return true;
            MiniGames.Begin(tillMonthEnd: true);
            var t = Traverse.Create(__instance);
            t.Field("paintingStage").SetValue(0);
            t.Field("giftTicketNum").SetValue(7);
            MiniGames.Call(__instance, "EndPaintCompete");
            MiniGames.Skipped("鸟节画画比赛", "第一名，7 张礼券");
            return false;
        }
        catch (Exception e) { return MiniGames.Fail("画画比赛", e); }
    }
}

// ---- 钓鱼：鱼全点到、垃圾一个不碰；鸟节比赛同样算分 ----
[HarmonyPatch(typeof(MapSys), "FishingEnu")]
static class FishSkip
{
    static bool Prefix(MapSys __instance, ref IEnumerator __result)
    {
        if (!MiniGames.On) return true;
        try
        {
            var t = Traverse.Create(__instance);
            bool bird = t.Field<bool>("isBirdCompeteFishing").Value;
            int lucky = t.Field<int>("lucky").Value;
            int count = t.Field<int>("fishNum").Value;
            if (AccessTools.Method(typeof(MapSys), "FishingResult") == null) return true;
            // 每次冒出来的是鱼还是垃圾，和 GeneNewFish 的概率一样；队友得分和 FishingEnu 一样
            int fish = 0, rival = 0;
            string mate = FeastSys.Instan.birdDayTeammate;
            int mateProb = mate == "hac" ? 90 : (mate == "mona" ? 75 : 65);
            for (int i = 0; i < count; i++)
            {
                if (Func.Prob(52 + Mathf.Min(20, lucky))) fish++;
                if (bird && i % 2 == 1 && Func.Prob(mateProb)) rival += 120;
            }
            int score = fish > 0 ? 70 + 120 * (fish - 1) : 0;  // 第一条 70，之后连击每条 120
            MiniGames.Begin(tillMonthEnd: bird);
            t.Field("hitCount").SetValue(fish);
            t.Field("comboCount").SetValue(fish);
            t.Field("fishingScore").SetValue(bird ? score : 0);
            t.Field("rivalFishingScore").SetValue(bird ? rival : 0);
            __result = Finish(__instance);
            MiniGames.Skipped(bird ? "鸟节钓鱼比赛" : "钓鱼", "钓到 " + fish + " 条" + (bird ? "，" + score + " 比 " + rival : ""));
            return false;
        }
        catch (Exception e) { return MiniGames.Fail("钓鱼", e); }
    }

    static IEnumerator Finish(MapSys map)
    {
        yield return new WaitForSeconds(1f);
        try { MiniGames.Call(map, "FishingResult"); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("钓鱼结算出错：" + e); }
    }
}

[HarmonyPatch(typeof(MapSys), "ClickFinishFish")]
static class FishEnd
{
    static void Postfix() { MiniGames.End(); }
}

// ---- 鸟节算术：题全答对 ----
[HarmonyPatch(typeof(FeastSys), "BirdFeastEnu")]
static class MathSkip
{
    static bool Prefix(FeastSys __instance, ref IEnumerator __result)
    {
        if (!MiniGames.On) return true;
        try
        {
            var t = Traverse.Create(__instance);
            if (t.Field<int>("birdStage").Value != 2) return true;  // 4 是转盘，纯运气，不管
            int difficulty = t.Field<int>("mathDifficulty").Value;
            var names = t.Field<string[]>("birdTsName").Value;
            if (AccessTools.Method(typeof(FeastSys), "ShowMathQueResult") == null) return true;
            MiniGames.Begin(tillMonthEnd: true);
            t.Field("ts").SetValue(UiSys.Instan.uiTrans[58].Find(names[2]));
            t.Field("correctNum").SetValue(Constant.birdMathQues[difficulty]);
            t.Field("quesNum").SetValue(0);
            t.Field("startMathGame").SetValue(false);
            MiniGames.Call(__instance, "ShowMathQueResult");
            __result = MiniGames.Nothing();
            MiniGames.Skipped("鸟节算术", Constant.birdMathQues[difficulty] + " 题全对");
            return false;
        }
        catch (Exception e) { return MiniGames.Fail("算术", e); }
    }
}
