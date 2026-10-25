// Ludus Lunae · 火山的女儿插件：战斗当策略游戏玩、交给队友打、探险里一直走
//
// 游戏作者做的是炉石传说那种战斗（规则在游戏的 FightSys、FightNpc、FightEnemy 里）：
//   每回合能量 +2，最多攒 6，用不完留到下回合；每个队友一回合出手一次（出过手头上写「[回合结束]」）；技能花能量，
//   刚用过的技能在冷却的几回合里贵 addCost 点；防御 1 能量一层，全队这回合受的伤 -25%，最多两层；
//   武器分物理、魔法，对上敌人的弱点伤害 ×1.2；飞行的敌人，不是弓打的伤害减半；敌人先打护盾再掉血；
//   敌人头顶写着下一步：攻击谁、多少（攻击 / 攻击+防御 / 攻击全体），防御、愈疗写数，施咒、强化只有字。
//   能量用完而且大家都出过手（或者都出过手、防御满了），游戏自己进敌方回合。
// 这里做的事：
//   View        战斗写成文字。只写人类玩家屏幕上看得到的（包括悬停能看到的伤害预览、状态说明），不写看不到的（敌人下一招具体是什么咒）
//   skill / defend / weapon / endturn   一步出手：选技能、点敌人一起做完，回话写打了多少、还剩多少、谁还能出手、剩几点能量；
//               用不了直接说为什么，不去点
//   autofight   交给队友：插件照规则自己打（看能量、挑弱点、血少了防御），有输有赢；随时收回；每场记一条（autofight.jsonl）
//   Tick        打赢了自动「拿取全部」（金币记成 [得到]，物品照旧由 ItemPatch 记），输了自动点「结束」；
//               探险里 walk：一直走，到岔路、随机事件、泉水宝箱、战斗、首领前一格停下
// 点东西照样走 SelfPlay.Press（Unity 的事件派发，和鼠标点一样）。游戏自己不拦的（出过手的人再点技能），这里按画面上的规矩拦下。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

static class FightPlay
{
    // ---- 游戏的私有字段 ----
    static readonly FieldInfo fTurn = AccessTools.Field(typeof(FightSys), "turn");
    static readonly FieldInfo fMaxEnergy = AccessTools.Field(typeof(FightSys), "maxEnergy");
    static readonly FieldInfo fTurnEnergy = AccessTools.Field(typeof(FightSys), "turnEnergy");
    static readonly FieldInfo fDefense = AccessTools.Field(typeof(FightSys), "defenseLevel");
    static readonly FieldInfo fEnemies = AccessTools.Field(typeof(FightSys), "tempEnemies");
    static readonly FieldInfo fNextAction = AccessTools.Field(typeof(FightEnemy), "nextAction");
    static readonly FieldInfo fPlaceTs = AccessTools.Field(typeof(ExploSys), "placeTs");
    static readonly FieldInfo fLevel = AccessTools.Field(typeof(ExploSys), "level");
    static readonly FieldInfo fStage = AccessTools.Field(typeof(ExploSys), "stage");
    static readonly FieldInfo fPathType = AccessTools.Field(typeof(ExploSys), "pathType");
    static readonly FieldInfo fPaths = AccessTools.Field(typeof(ExploSys), "paths");
    static readonly FieldInfo fMapType = AccessTools.Field(typeof(ExploSys), "mapType");

    static DataConfig Cfg { get { return DataSys.Instan.dataCfg; } }
    static FightSys Fs { get { return FightSys.Instan; } }
    static Transform Ts { get { return FightSys.fightTs; } }

    static int Turn { get { try { return (int)fTurn.GetValue(null); } catch { return 0; } } }
    static int MaxEnergy { get { try { return (int)fMaxEnergy.GetValue(null); } catch { return Constant.defaultStorageFightEnergy; } } }
    static int TurnEnergy { get { try { return (int)fTurnEnergy.GetValue(Fs); } catch { return Constant.defaultFightEnergy; } } }
    static int DefenseLevel { get { try { return (int)fDefense.GetValue(null); } catch { return 0; } } }
    static List<FightEnemy> Alive { get { return (fEnemies.GetValue(null) as List<FightEnemy>) ?? new List<FightEnemy>(); } }

    static string Txt(string s, int max = 60) { return SelfPlay.Clean(s, max); }

    // ================= 画面在哪一步 =================
    static bool Child(string name)
    {
        try { var t = Ts.Find(name); return t != null && t.gameObject.activeSelf; }
        catch { return false; }
    }

    internal static bool InFight { get { try { return Fs != null && Ts != null && Ts.gameObject.activeInHierarchy; } catch { return false; } } }
    static bool ResultOpen { get { return InFight && Child("result"); } }
    static bool LoseOpen { get { return InFight && Child("lose"); } }
    static bool EnemyTurn { get { return InFight && Child("topMask"); } }
    static bool Slogan { get { return InFight && Child("slogan"); } }
    internal static bool SloganOn { get { return Slogan; } }  // 第一次见的怪物等「回合开始」的大字收起来再截（Sights.cs）
    static bool PlayerTurn { get { return InFight && !ResultOpen && !LoseOpen && !EnemyTurn && FightSys.fightPhase != FightSys.FightPhase.endFight; } }

    static Transform LearnTs { get { try { return UiSys.Instan.uiTrans[24].Find("getSkill"); } catch { return null; } } }
    static bool LearnOpen { get { var t = LearnTs; return t != null && t.gameObject.activeInHierarchy; } }

    // OpenWindowText 用：开着战斗，或者打完要学新技能
    internal static bool Showing() { return InFight || LearnOpen; }

    // ================= 人和敌人 =================
    static List<FightNpc> Members()
    {
        var list = new List<FightNpc>();
        try
        {
            foreach (var n in Fs.choosedNpc)
            {
                FightNpc f;
                if (Fs.fightNpcs.TryGetValue(n, out f)) list.Add(f);
            }
        }
        catch { }
        return list;
    }

    internal static string Who(FightNpc n)
    {
        try { return n.enName == "dau" ? DauSys.Instan.dauName : Txt(n.npc.chName, 20); }
        catch { return n.enName; }
    }

    static int MaxHp(FightNpc n) { return n.FightNum(FightAttri.hp); }
    static int Atk(FightNpc n) { return n.atk > 0 ? n.atk : n.FightNum(FightAttri.atk); }

    static Item WeaponOf(FightNpc n, int slot)
    {
        try
        {
            int i = n.npc.equips[slot];
            return i < 0 ? null : Cfg.items[i];
        }
        catch { return null; }
    }

    // 武器的属性图标（explo/weapon0、weapon1）和敌人的弱点图标（Ui/fight/nature0、nature1）：文字表里是「物理弱项」「魔法弱项」
    static string Nature(int armorType) { return armorType == 1 ? "魔法" : "物理"; }

    static string WeaponName(FightNpc n, int slot)
    {
        var w = WeaponOf(n, slot);
        if (w == null) return "空手";
        return Txt(w.chName, 20) + "（" + Nature(w.armorType) + (w.type == 26 ? "·弓" : "") + "）";
    }

    static int Weak(FightEnemy e) { return e.data.defType[0] >= e.data.defType[1] ? 1 : 0; }
    static bool Flying(FightEnemy e) { return e.data.motionMode == 1; }

    static string EnemyName(FightEnemy e)
    {
        string tag = e.data.type == 2 ? "（首领）" : e.data.type == 1 ? "（精英）" : "";
        return "敌" + (e.transform.GetSiblingIndex() + 1) + " " + Txt(e.data.chName, 20) + tag;
    }

    static List<int> Slots(FightNpc n) { try { return n.EnableWeaponType(); } catch { return new List<int> { n.weapon }; } }

    static List<Skill> SkillsOf(FightNpc n, int slot)
    {
        var list = new List<Skill>();
        for (int i = 0; i < Constant.maxSkillNum; i++)
        {
            int k = slot * Constant.maxSkillNum + i;
            if (k < n.skills.Length && n.skills[k] >= 0) list.Add(Cfg.skills[n.skills[k]]);
        }
        return list;
    }

    // 只有女儿能换武器（FightNpc.InitializeFightUi：武器格的 UiBtn 只对 dau 开着）
    static bool CanSwitch(FightNpc n) { return n.enName == "dau" && Slots(n).Count > 1; }

    // 头上的「[回合结束]」、石化、行动紊乱（FightSys.TransTurnStateData 盖上的 stop），倒下
    static string CantAct(FightNpc n)
    {
        if (n.state == RoleState.die || n.hp <= 0) return "倒下了";
        if (n.state == RoleState.endTurn) return "这回合出过手了";
        if (n.states.ContainsKey(IntentionEnu.fossil.ToString())) return "石化了，这回合动不了";
        if (n.states.ContainsKey(IntentionEnu.chaos.ToString())) return "行动紊乱，这回合动不了";
        return null;
    }

    static bool NeedsTarget(Skill s) { return s.actionType == "atk" || s.actionType == "aoe" || s.actionType == "defAtk"; }

    // 和 FightNpc.Atk 一样，只是武器可以指定：看换了武器打多少（暴击另算；敌人的护盾另算，先打护盾再掉血）
    static int Damage(FightNpc n, Skill s, FightEnemy e, int slot)
    {
        int d = Fs.CountAtk(Atk(n) * s.power, n.states, e == null ? null : e.states);
        if (e == null) return d;
        var w = WeaponOf(n, slot);
        if (w == null) return d;
        if (w.armorType == Weak(e)) d = (int)(d * Constant.weakNatureAtk);
        if (Flying(e) && w.type != 26) d /= 2;
        return d;
    }

    static int Int(string s) { int v; return int.TryParse(s, out v) ? v : 0; }

    static string StatesText(Dictionary<string, FightState> states, Dictionary<string, string> legend)
    {
        var parts = new List<string>();
        foreach (var st in states.Values)
        {
            int num = st.ability.layerType == "layer" ? st.layer : st.turnLeft;
            parts.Add(Txt(st.ability.chName, 12) + " " + num);
            if (legend != null && !legend.ContainsKey(st.ability.chName)) legend[st.ability.chName] = StateDes(st.ability, num);
        }
        return string.Join("、", parts.ToArray());
    }

    // 悬停在状态上看到的说明（FightSys.ShowStateDes）
    static string StateDes(Ability ab, int num)
    {
        try
        {
            var text = ChatSys.Txt(ab.des).Replace("&", num.ToString());
            if (ab.enName == IntentionEnu.weaken.ToString()) text = text.Replace("*", (int)(Constant.weakRatio * 100f) + "%");
            else if (ab.enName == IntentionEnu.hurt.ToString()) text = text.Replace("*", (int)((Constant.hurtRatio - 1f) * 100f) + "%");
            return Txt(text, 80);
        }
        catch { return ""; }
    }

    static string AbilityName(IntentionEnu i) { return Txt(AbilityRaw(i), 12); }

    // Look.states 用的键：游戏数据里的原名
    static string AbilityRaw(IntentionEnu i)
    {
        Ability ab;
        return Cfg.abilityDic.TryGetValue(i.ToString(), out ab) ? ab.chName : i.ToString();
    }

    // ================= 敌人头顶的图标 =================
    // 画面上写什么：攻击 / 攻击+防御写目标和数，攻击全体、防御、愈疗写数，施咒、强化只有字（FightEnemy.IntentionUi）
    static string Intent(FightEnemy e, out int dmg, out FightNpc target, out bool all)
    {
        dmg = 0;
        target = null;
        all = false;
        if (e.states.ContainsKey(IntentionEnu.fossil.ToString())) return "石化了，下回合不动";
        var act = fNextAction.GetValue(e) as string[];
        if (act == null || act.Length == 0) return "？";
        Intention it;
        if (!Cfg.intentions.TryGetValue(act[0], out it)) return act[0];
        string word = it.uiType >= 0 && it.uiType < Constant.intentionCh.Length ? Constant.intentionCh[it.uiType] : Txt(it.chName, 12);
        IntentionEnu kind;
        try { kind = (IntentionEnu)Enum.Parse(typeof(IntentionEnu), act[0]); }
        catch { return word; }
        try
        {
            switch (kind)
            {
                case IntentionEnu.attack:
                case IntentionEnu.defAttack:
                    target = e.atkObjtct;
                    dmg = e.Atk(act[1]);
                    return word + " → " + (target == null ? "?" : Who(target)) + " " + dmg;
                case IntentionEnu.allAttack:
                    all = true;
                    dmg = e.Atk(act[1]);
                    return word + " " + dmg;
                case IntentionEnu.defen:
                    return word + " " + (int)(e.data.def * Int(act[1]) * 0.01f);
                case IntentionEnu.heal:
                    return word + " " + (int)(e.maxHp * 0.01f * Int(act[1]));
            }
        }
        catch { }
        return word;
    }

    // ================= 写成文字 =================
    internal static string View()
    {
        try
        {
            if (!InFight) return LearnOpen ? LearnText() : "";
            string where = Where();
            if (ResultOpen)
                return "战斗赢了" + where + "。战利品：" + LootText(ReadLoot()) + "（插件会自己「拿取全部」）" + (LearnOpen ? "\n" + LearnText() : "");
            if (LoseOpen) return "战斗输了" + where + "（插件会自己点「结束」，这趟探险到此为止）";
            var s = new StringBuilder("== 战斗 · 第 " + Turn + " 回合 · " + (EnemyTurn ? "敌方回合（等一下）" : "我方回合") + " ==" + where);
            if (AutoOn) s.Append(" 【交给队友在打，auto off 收回】");
            s.Append('\n').Append(EnergyLine());
            var legend = new Dictionary<string, string>();
            s.Append("\n我方：");
            foreach (var m in Members()) s.Append('\n').Append(MemberText(m, legend));
            s.Append("\n敌人：");
            foreach (var e in Alive) s.Append("\n  ").Append(EnemyText(e, legend));
            if (legend.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in legend) parts.Add(kv.Key + "：" + kv.Value);
                s.Append("\n状态说明：").Append(string.Join("；", parts.ToArray()));
            }
            s.Append("\n（skill 技能 敌N 出手；defend 防御；weapon 武器 换武器；endturn 结束回合，剩下的能量留到下回合；auto on 交给队友打）");
            return s.ToString();
        }
        catch (Exception e) { return "（读战斗出错：" + e.Message + "）"; }
    }

    static string Where()
    {
        try
        {
            if (ExploSys.isExploring && ExploSys.tempMap != null) return "（" + Txt(ExploSys.tempMap.chName, 20) + "）";
        }
        catch { }
        return "";
    }

    static string EnergyLine()
    {
        int L = DefenseLevel;
        return "能量 " + FightSys.energy + "/" + MaxEnergy + "（每回合 +" + TurnEnergy + "，最多攒 " + MaxEnergy + "，用不完留到下回合）  防御 "
            + (25 * L) + "%" + (L < Constant.maxDefenseLevel ? "（defend：1 能量，全队这回合受的伤再 -25%，最多 " + 25 * Constant.maxDefenseLevel + "%）" : "（满了）");
    }

    static string MemberText(FightNpc n, Dictionary<string, string> legend)
    {
        var s = new StringBuilder("  " + Who(n) + " Lv." + (n.level + 1) + "  HP " + n.hp + "/" + MaxHp(n));
        if (n.shield > 0) s.Append("  护盾 ").Append(n.shield);
        s.Append("  攻击 ").Append(Atk(n)).Append("  拿着 ").Append(WeaponName(n, n.weapon));
        var st = StatesText(n.states, legend);
        if (st != "") s.Append("  状态：").Append(st);
        var why = CantAct(n);
        s.Append("  —— ").Append(why == null ? "能出手" : why == "这回合出过手了" ? "[回合结束]" : why);
        if (n.hp <= 0) return s.ToString();
        foreach (var sk in SkillsOf(n, n.weapon)) s.Append("\n    · ").Append(SkillLine(n, sk, n.weapon));
        if (CanSwitch(n))
            foreach (var slot in Slots(n))
            {
                if (slot == n.weapon) continue;
                s.Append("\n    另一把 ").Append(WeaponName(n, slot)).Append("，换上（weapon，不花能量，不算出手）能用：");
                foreach (var sk in SkillsOf(n, slot)) s.Append("\n    · ").Append(SkillLine(n, sk, slot));
            }
        return s.ToString();
    }

    static string SkillLine(FightNpc n, Skill s, int slot)
    {
        int cost = s.Cost();
        var line = new StringBuilder(Txt(s.chName, 20)).Append(' ').Append(cost).Append(" 能量");
        if (s.coldTurn > 0 && s.addCost > 0) line.Append("（刚用过，贵 ").Append(s.addCost).Append("，").Append(s.coldTurn).Append(" 回合后恢复）");
        if (cost > FightSys.energy) line.Append("（能量不够）");
        line.Append("：").Append(Effect(n, s, slot));
        return line.ToString();
    }

    // 技能做什么：和悬停时的说明一样（FightSys.SkillDesTxt），伤害按每个敌人写（悬停在敌人身上时血条上显示的那个数，没减护盾）
    static string Effect(FightNpc n, Skill s, int slot)
    {
        string power = "×" + s.power.ToString("0.#");
        string text;
        switch (s.actionType)
        {
            case "atk": text = "打一个 " + power + " → " + PerEnemy(n, s, slot); break;
            case "defAtk": text = "打一个 " + power + " → " + PerEnemy(n, s, slot) + "，自己护盾 +" + (int)(Atk(n) * 0.1f * Int(s.data)); break;
            case "aoe": text = "打全体 " + power + " → " + PerEnemy(n, s, slot); break;
            case "randAtk": text = "随机打 3 下 " + power + "，每下 → " + PerEnemy(n, s, slot); break;
            case "allDef": text = "全队护盾 +" + (int)(Atk(n) * s.power); break;
            case "healing": text = "HP 最低的人回 " + (int)(Atk(n) * s.power); break;
            case "allHealing": text = "全队回 " + (int)(Atk(n) * s.power) + " HP"; break;
            case "addStrength": text = "全队这场力量 +" + (int)s.power; break;
            case "addEnergy": text = "能量 +" + (int)s.power; break;
            default:
                string d;
                text = Constant.skillDesDicCh.TryGetValue(s.actionType, out d) ? d.Replace("&", power) : s.actionType;
                break;
        }
        var extra = new List<string>();
        if (s.weaken > 0) extra.Add(AbilityName(IntentionEnu.weaken) + " " + s.weaken);
        if (s.hurt > 0) extra.Add(AbilityName(IntentionEnu.hurt) + " " + s.hurt);
        if (s.burn > 0) extra.Add(AbilityName(IntentionEnu.burn) + " " + s.burn);
        if (s.fossil > 0) extra.Add(AbilityName(IntentionEnu.fossil) + " " + s.fossil);
        if (extra.Count > 0) text += "，附带 " + string.Join("、", extra.ToArray());
        try
        {
            var des = Txt(ChatSys.Txt(s.des), 50);
            if (des != "" && s.actionType != "instantAtk") text += "（" + des + "）";
        }
        catch { }
        return text;
    }

    static string PerEnemy(FightNpc n, Skill s, int slot)
    {
        var parts = new List<string>();
        foreach (var e in Alive) parts.Add("敌" + (e.transform.GetSiblingIndex() + 1) + " " + Damage(n, s, e, slot));
        return parts.Count == 0 ? "（没有敌人）" : string.Join("、", parts.ToArray());
    }

    static string EnemyText(FightEnemy e, Dictionary<string, string> legend)
    {
        var s = new StringBuilder(EnemyName(e)).Append("  HP ").Append(e.hp).Append('/').Append((int)e.maxHp);
        if (e.shield > 0) s.Append("  护盾 ").Append(e.shield);
        s.Append("  弱").Append(Nature(Weak(e)));
        if (Flying(e)) s.Append("  飞（不是弓打的伤害减半）");
        var st = StatesText(e.states, legend);
        if (st != "") s.Append("  状态：").Append(st);
        int dmg;
        FightNpc t;
        bool all;
        s.Append("  —— 下一步：").Append(Intent(e, out dmg, out t, out all));
        return s.ToString();
    }

    // 打赢以后学新技能（二选一，FightSys.ShowLevelUpUi 的 getSkill 窗口，格子名是技能编号）
    static string LearnText()
    {
        var s = new StringBuilder("学新技能，二选一（click 技能名）：");
        try
        {
            var cells = LearnTs.Find("panel").Find("skill");
            for (int i = 0; i < cells.childCount; i++)
            {
                int idx;
                if (!int.TryParse(cells.GetChild(i).name, out idx) || idx < 0 || idx >= Cfg.skills.Length) continue;
                var sk = Cfg.skills[idx];
                string d;
                var text = Constant.skillDesDicCh.TryGetValue(sk.actionType, out d) ? d.Replace("&", "×" + sk.power.ToString("0.#")) : sk.actionType;
                s.Append("\n  · ").Append(Txt(sk.chName, 20)).Append("：").Append(sk.cost).Append(" 能量，").Append(text.TrimEnd('。'));
                var des = Txt(ChatSys.Txt(sk.des), 50);
                if (des != "") s.Append("（").Append(des).Append('）');
                s.Append("，用的武器：").Append(sk.weaponType >= 0 && sk.weaponType < Constant.weaponCh.Length ? Constant.weaponCh[sk.weaponType] : "?");
            }
        }
        catch (Exception e) { s.Append("（读不出：" + e.Message + "）"); }
        return s.ToString();
    }

    // ================= 战利品 =================
    // 结果窗口里的格子名是「物品编号#数量」，金币是 -1（FightSys.ItemCellUi）
    static List<int[]> ReadLoot()
    {
        var list = new List<int[]>();
        try
        {
            var content = Ts.Find("result").Find("item").Find("view").Find("content");
            for (int i = 0; i < content.childCount; i++)
            {
                var parts = content.GetChild(i).name.Split('#');
                int a, b;
                if (parts.Length == 2 && int.TryParse(parts[0], out a) && int.TryParse(parts[1], out b)) list.Add(new[] { a, b });
            }
        }
        catch { }
        return list;
    }

    static string LootName(int[] it)
    {
        if (it[0] == -1) return "金币 " + it[1];
        try { return Txt(Cfg.items[it[0]].chName, 20) + (it[1] > 1 ? " ×" + it[1] : ""); }
        catch { return "物品" + it[0]; }
    }

    static string LootText(List<int[]> loot)
    {
        var parts = new List<string>();
        foreach (var it in loot) parts.Add(LootName(it));
        return parts.Count == 0 ? "没有" : string.Join("、", parts.ToArray());
    }

    // ================= 出手 =================
    class Look
    {
        public int hp, shield;
        public bool alive;
        public Dictionary<string, int> states = new Dictionary<string, int>();
    }

    static Look LookOf(int hp, int shield, bool alive, Dictionary<string, FightState> states)
    {
        var l = new Look { hp = hp, shield = shield, alive = alive };
        foreach (var st in states.Values) l.states[st.ability.chName] = st.ability.layerType == "layer" ? st.layer : st.turnLeft;
        return l;
    }

    static List<FightEnemy> roster = new List<FightEnemy>();  // 这一场所有的敌人（打倒的也留着，编号不变）

    static List<FightEnemy> Roster()
    {
        if (roster.Count == 0) roster.AddRange(Alive);
        return roster;
    }

    static Dictionary<object, Look> Snap()
    {
        var d = new Dictionary<object, Look>();
        foreach (var m in Members()) d[m] = LookOf(m.hp, m.shield, m.hp > 0, m.states);
        var alive = Alive;
        foreach (var e in Roster()) d[e] = LookOf(e.hp, e.shield, alive.Contains(e) && e.hp > 0, e.states);
        return d;
    }

    // FightNpc.UsingSkillDamage 的结果（含暴击）：判断这一下暴击没有
    internal static readonly List<KeyValuePair<FightEnemy, int>> hits = new List<KeyValuePair<FightEnemy, int>>();

    static Transform SkillCell(FightNpc n, Skill s)
    {
        try
        {
            var root = n.ts.Find("skill");
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c.gameObject.activeSelf && c.name == s.index.ToString()) return c;
            }
        }
        catch { }
        return null;
    }

    // 出一招：要换武器先换，点技能格子，要选目标的再点敌人。出成了返回 true，text 是结果
    static bool Use(FightNpc n, Skill s, int slot, FightEnemy target, out string text)
    {
        text = "";
        try
        {
            if (FightSys.fightPhase == FightSys.FightPhase.chooseSkillEnemy) Fs.ClickCancelMask();
            var before = Snap();
            int e0 = FightSys.energy, cost = s.Cost();
            string pre = "";
            if (slot != n.weapon)
            {
                var w = n.ts.Find("weapon").Find(slot.ToString());
                if (w == null || !w.gameObject.activeSelf) { text = Who(n) + " 换不了武器"; return false; }
                SelfPlay.Press(w.gameObject);
                if (n.weapon != slot) { text = Who(n) + " 换不了武器（游戏没接）"; return false; }
                pre = Who(n) + " 换上 " + WeaponName(n, slot) + "。";
            }
            var cell = SkillCell(n, s);
            if (cell == null) { text = pre + "画面上找不到「" + Txt(s.chName, 20) + "」这一格"; return false; }
            var expect = new Dictionary<FightEnemy, int>();  // 出手前算好不暴击打多少，附上的状态会改后面的数
            foreach (var e in Alive) expect[e] = Damage(n, s, e, slot);
            hits.Clear();
            SelfPlay.Press(cell.gameObject);
            if (NeedsTarget(s))
            {
                if (FightSys.fightPhase != FightSys.FightPhase.chooseSkillEnemy) { text = pre + "技能没选上（游戏没接这一下）"; return false; }
                SelfPlay.Press(target.gameObject);
                if (FightSys.fightPhase == FightSys.FightPhase.chooseSkillEnemy)
                {
                    Fs.ClickCancelMask();
                    text = pre + "点敌人没点上，取消了";
                    return false;
                }
            }
            // 这一招用掉了这回合最后的出手（能量用完或防御满了、大家都出过手）时，游戏同一下就进敌方回合（FightSys.CheckPlayerTurnEnd），
            // EndPlayerTurn 把每个人的状态清回去了，看不到 endTurn。所以还要看敌方回合的幕拉没拉上：拉上了就算出去了，不然最后一手会被误报成「没出去」
            if (n.state != RoleState.endTurn && n.hp > 0 && !EnemyTurn) { text = pre + Who(n) + " 的「" + Txt(s.chName, 20) + "」没出去（游戏没接）"; return false; }
            text = pre + Result(n, s, cost, before, e0, expect);
            return true;
        }
        catch (Exception e)
        {
            text = "出手时出错：" + e.Message;
            return false;
        }
    }

    static string Result(FightNpc n, Skill s, int cost, Dictionary<object, Look> before, int e0, Dictionary<FightEnemy, int> expect)
    {
        var parts = new List<string>();
        foreach (var e in Roster())
        {
            Look b;
            if (!before.TryGetValue(e, out b) || !b.alive) continue;
            bool dead = e.hp <= 0 || !Alive.Contains(e);
            int lost = b.hp + b.shield - Math.Max(0, e.hp) - (dead ? 0 : e.shield);
            bool crit = false;
            int plain;
            foreach (var h in hits)
                if (h.Key == e && expect.TryGetValue(e, out plain) && h.Value > plain && h.Value >= plain * 1.3f) crit = true;
            if (dead) { parts.Add(EnemyName(e) + " 打倒了" + (crit ? "（暴击）" : "")); continue; }
            if (lost == 0 && b.states.Count == e.states.Count) continue;
            var line = EnemyName(e) + (lost != 0 ? " -" + lost : "") + (crit ? "（暴击）" : "");
            if (b.shield > 0 && e.shield < b.shield) line += "，护盾 " + b.shield + "→" + e.shield;
            line += "，HP " + b.hp + "→" + e.hp + "/" + (int)e.maxHp;
            var added = NewStates(b.states, e.states);
            if (added != "") line += "，附上 " + added;
            parts.Add(line);
        }
        foreach (var m in Members())
        {
            Look b;
            if (!before.TryGetValue(m, out b)) continue;
            var line = new List<string>();
            if (m.hp != b.hp) line.Add("HP " + b.hp + "→" + m.hp + "/" + MaxHp(m));
            if (m.shield > b.shield) line.Add("护盾 +" + (m.shield - b.shield));
            var added = NewStates(b.states, m.states);
            if (added != "") line.Add(added);
            if (line.Count > 0) parts.Add(Who(m) + " " + string.Join("，", line.ToArray()));
        }
        var head = Who(n) + " 用 " + Txt(s.chName, 20) + "（" + cost + " 能量）";
        var text = head + (parts.Count == 0 ? "：没打出伤害" : "：" + string.Join("；", parts.ToArray())) + "。";
        if (FightSys.energy != e0 - cost) text += "能量 " + e0 + "→" + FightSys.energy + "。";
        return text;
    }

    static string NewStates(Dictionary<string, int> before, Dictionary<string, FightState> now)
    {
        var parts = new List<string>();
        foreach (var st in now.Values)
        {
            int num = st.ability.layerType == "layer" ? st.layer : st.turnLeft;
            int old;
            if (!before.TryGetValue(st.ability.chName, out old) || num > old) parts.Add(Txt(st.ability.chName, 12) + " " + num);
        }
        return string.Join("、", parts.ToArray());
    }

    // 出完一手以后：还剩几点能量、谁还能出手；轮到敌方 / 打赢了就说
    static string AfterText()
    {
        if (!InFight || ResultOpen || Alive.Count == 0) return "赢了！";
        if (LoseOpen) return "输了。";
        if (EnemyTurn) return "大家都出过手、能量也用完了：轮到敌方。";
        var parts = new List<string>();
        foreach (var m in Members())
        {
            if (CantAct(m) != null) continue;
            var sk = new List<string>();
            foreach (var s in SkillsOf(m, m.weapon)) sk.Add(Txt(s.chName, 20) + " " + s.Cost() + (s.Cost() > FightSys.energy ? "（不够）" : ""));
            parts.Add(Who(m) + "（" + string.Join("、", sk.ToArray()) + "）");
        }
        var text = "能量 " + FightSys.energy + "/" + MaxEnergy + "，防御 " + 25 * DefenseLevel + "%。";
        if (parts.Count > 0) return text + "还能出手：" + string.Join("；", parts.ToArray()) + "。";
        return text + "大家都出过手了：endturn 结束回合（能量留到下回合）"
            + (FightSys.energy > 0 && DefenseLevel < Constant.maxDefenseLevel ? "，或者 defend 再防一层" : "") + "。";
    }

    static bool Settle() { return !InFight || EnemyTurn || ResultOpen || LoseOpen; }

    static string NotNow()
    {
        if (!InFight) return LearnOpen ? "战斗打完了，在学新技能：" + LearnText() : "现在不在战斗里";
        if (ResultOpen || LoseOpen) return "这一场已经打完了（wait）";
        if (EnemyTurn || Slogan) return "敌方回合 / 回合开场，等一下（wait）";
        if (AutoOn && !aiStalled) return "这一场交给队友在打（auto off 收回）";
        if (FightSys.fightPhase == FightSys.FightPhase.endFight) return "战斗还没开始";
        return null;
    }

    // ---- 名字 ----
    static int Score(string have, string want)
    {
        if (string.IsNullOrEmpty(have) || string.IsNullOrEmpty(want)) return 0;
        if (have == want) return 3;
        if (have.Contains(want)) return 2;
        return 0;
    }

    static FightNpc FindMember(string want)
    {
        FightNpc best = null;
        int top = 0;
        foreach (var m in Members())
        {
            int sc = Math.Max(Score(Who(m), want), Score(m.enName, want));
            if (sc > top) { top = sc; best = m; }
        }
        return best;
    }

    // 「2」「敌2」「2号」或者敌人的名字（同名的要写编号）
    static FightEnemy FindEnemy(string want, out string err)
    {
        err = null;
        var alive = Alive;
        var digits = want.Replace("敌", "").Replace("号", "").Replace("#", "").Trim();
        int k;
        if (int.TryParse(digits, out k))
        {
            foreach (var e in alive) if (e.transform.GetSiblingIndex() + 1 == k) return e;
            err = "没有活着的敌" + k + "。" + EnemyList();
            return null;
        }
        var found = new List<FightEnemy>();
        foreach (var e in alive) if (Score(Txt(e.data.chName, 20), want) > 0) found.Add(e);
        if (found.Count == 1) return found[0];
        err = (found.Count == 0 ? "没有叫「" + want + "」的敌人。" : "「" + want + "」有好几个，写编号。") + EnemyList();
        return null;
    }

    static string EnemyList()
    {
        var parts = new List<string>();
        foreach (var e in Alive) parts.Add(EnemyName(e) + " " + e.hp + "/" + (int)e.maxHp);
        return "现在的敌人：" + string.Join("、", parts.ToArray());
    }

    // ================= 命令 =================
    // skill 技能 [目标]：技能可以写「人.技能」；目标写 敌N 或敌人的名字，不用选目标的技能不写
    internal static string SkillCmd(string text, out bool ok, out bool settle)
    {
        ok = false;
        settle = false;
        var no = NotNow();
        if (no != null) return no;
        var words = (text ?? "").Trim().Split(new[] { ' ', '　', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "skill 技能 敌N（look 看全场）";
        var spec = words[0];
        var targetSpec = string.Join(" ", words, 1, words.Length - 1);
        string who = null;
        int dot = spec.IndexOfAny(new[] { '.', '·', '．', ':', '：' });
        if (dot > 0) { who = spec.Substring(0, dot); spec = spec.Substring(dot + 1); }
        FightNpc only = null;
        if (who != null)
        {
            only = FindMember(who);
            if (only == null) return "队伍里没有「" + who + "」";
        }
        // 找技能：先手上这把武器的，女儿再找另一把的
        var cands = new List<KeyValuePair<FightNpc, KeyValuePair<Skill, int>>>();
        int top = 0;
        foreach (var m in Members())
        {
            if (only != null && m != only) continue;
            if (m.hp <= 0) continue;
            foreach (var slot in Slots(m))
            {
                if (slot != m.weapon && !CanSwitch(m)) continue;
                foreach (var sk in SkillsOf(m, slot))
                {
                    int sc = Score(Txt(sk.chName, 20), spec) * 2 + (slot == m.weapon ? 1 : 0);
                    if (Score(Txt(sk.chName, 20), spec) == 0) continue;
                    if (sc > top) { top = sc; cands.Clear(); }
                    if (sc == top) cands.Add(new KeyValuePair<FightNpc, KeyValuePair<Skill, int>>(m, new KeyValuePair<Skill, int>(sk, slot)));
                }
            }
        }
        if (cands.Count == 0) return "没有叫「" + spec + "」的技能。" + AfterText();
        if (cands.Count > 1)
        {
            var ready = cands.FindAll(c => CantAct(c.Key) == null);
            if (ready.Count >= 1) cands = ready;
            var people = new List<string>();
            foreach (var c in cands) if (!people.Contains(Who(c.Key))) people.Add(Who(c.Key));
            if (people.Count > 1) return "好几个人都有「" + spec + "」：写 " + people[0] + "." + spec + " 或 " + people[1] + "." + spec;
        }
        var n = cands[0].Key;
        var s = cands[0].Value.Key;
        int useSlot = cands[0].Value.Value;
        var why = CantAct(n);
        if (why != null) return Who(n) + why + "。" + AfterText();
        int cost = s.Cost();
        if (cost > FightSys.energy)
            return "能量不够：" + Txt(s.chName, 20) + " 要 " + cost + "，剩 " + FightSys.energy + (s.coldTurn > 0 && s.addCost > 0 ? "（刚用过贵 " + s.addCost + "）" : "") + "。" + AfterText();
        FightEnemy target = null;
        string note = "";
        if (NeedsTarget(s))
        {
            if (targetSpec == "")
            {
                if (Alive.Count == 1 || s.actionType == "aoe") target = Alive.Count > 0 ? Alive[0] : null;
                else return "打谁？skill " + Txt(s.chName, 20) + " 敌N。" + EnemyList();
            }
            else
            {
                string err;
                target = FindEnemy(targetSpec, out err);
                if (target == null) return err;
            }
            if (target == null) return "没有敌人可打";
        }
        else if (targetSpec != "") note = "（这一招不用选目标）";
        string result;
        bool done = Use(n, s, useSlot, target, out result);
        SelfPlay.Acted(done ? "出手" : "没出成");
        if (!done) return result;
        ok = true;
        settle = Settle();
        return result + note + AfterText();
    }

    internal static string DefendCmd(out bool ok, out bool settle)
    {
        ok = false;
        settle = false;
        var no = NotNow();
        if (no != null) return no;
        if (DefenseLevel >= Constant.maxDefenseLevel) return "防御已经满了（" + 25 * DefenseLevel + "%）。" + AfterText();
        if (FightSys.energy < 1) return "能量不够：防御要 1 点，剩 0。" + AfterText();
        string r;
        if (!DoDefend(out r)) return r;
        SelfPlay.Acted("防御");
        ok = true;
        settle = Settle();
        return r + AfterText();
    }

    static bool DoDefend(out string text)
    {
        int L = DefenseLevel, e0 = FightSys.energy;
        if (FightSys.fightPhase == FightSys.FightPhase.chooseSkillEnemy) Fs.ClickCancelMask();
        var b = FindButton(Ts, "DefenseBtn");
        if (b != null && b.IsInteractable()) SelfPlay.Press(b.gameObject);
        else Fs.DefenseBtn();  // 能量和层数上面查过，和按钮亮着的条件一样（FightSys.UpdateDefenseUi）
        if (DefenseLevel <= L) { text = "防御没点上"; return false; }
        text = "防御：全队这回合受的伤 -" + 25 * DefenseLevel + "%，能量 " + e0 + "→" + FightSys.energy + "。";
        return true;
    }

    internal static string WeaponCmd(string text, out bool ok)
    {
        ok = false;
        var no = NotNow();
        if (no != null) return no;
        var want = (text ?? "").Trim();
        FightNpc n = null;
        foreach (var m in Members()) if (m.enName == "dau") n = m;
        if (n == null || !CanSwitch(n)) return "只有" + (n == null ? "女儿" : Who(n)) + "能换武器，她现在没有第二把";
        int slot = -1;
        foreach (var s in Slots(n))
        {
            if (s == n.weapon) continue;
            var w = WeaponOf(n, s);
            if (want == "" || want == "换" || want == "另一把" || (w != null && (Score(Txt(w.chName, 20), want) > 0 || (want == "弓" && w.type == 26) || want == Nature(w.armorType))))
                slot = s;
        }
        if (slot < 0)
        {
            var cur = WeaponOf(n, n.weapon);
            if (cur != null && Score(Txt(cur.chName, 20), want) > 0) return Who(n) + " 拿着的就是 " + WeaponName(n, n.weapon);
            return "没有「" + want + "」。" + Who(n) + " 拿着 " + WeaponName(n, n.weapon) + "，另一把能换";
        }
        var cell = n.ts.Find("weapon").Find(slot.ToString());
        if (cell == null) return "画面上找不到武器格子";
        if (FightSys.fightPhase == FightSys.FightPhase.chooseSkillEnemy) Fs.ClickCancelMask();
        SelfPlay.Press(cell.gameObject);
        SelfPlay.Acted("换武器");
        if (n.weapon != slot) return "没换成（游戏没接）";
        ok = true;
        var s2 = new StringBuilder(Who(n) + " 换上 " + WeaponName(n, slot) + "（不花能量）" + (CantAct(n) != null ? "，不过她" + CantAct(n) : "") + "。能用：");
        foreach (var sk in SkillsOf(n, slot)) s2.Append("\n  · ").Append(SkillLine(n, sk, slot));
        return s2.ToString();
    }

    internal static string EndTurnCmd(out bool ok)
    {
        ok = false;
        var no = NotNow();
        if (no != null) return no;
        int left = FightSys.energy;
        if (!EndTurnNow()) return "结束回合没点上";
        SelfPlay.Acted("结束回合");
        ok = true;
        int next = Math.Min(MaxEnergy, left + TurnEnergy);
        return "结束回合：剩下 " + left + " 点能量留着，下回合 " + next + " 点。敌方回合……";
    }

    static bool EndTurnNow()
    {
        if (FightSys.fightPhase == FightSys.FightPhase.chooseSkillEnemy) Fs.ClickCancelMask();
        var b = FindButton(Ts, "EndPlayerTurnBtn");
        if (b != null && b.IsInteractable()) SelfPlay.Press(b.gameObject);
        if (!EnemyTurn && PlayerTurn) Fs.EndPlayerTurnBtn();
        return EnemyTurn || !PlayerTurn;
    }

    static Button FindButton(Transform root, string method)
    {
        if (root == null) return null;
        foreach (var b in root.GetComponentsInChildren<Button>(false))
            for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                if (b.onClick.GetPersistentMethodName(i) == method) return b;
        return null;
    }

    // ================= 交给队友（托管） =================
    static bool? autoCmd;  // vp_player 的 auto on / off；没设过就看 modes.json 的 autoFight（面板上要加开关就写这个键）
    static bool autoOnce;  // auto once：只交这一场（还没开打就是下一场），打完自己收回

    internal static bool AutoOn { get { return autoOnce || (autoCmd ?? LudusLunaePlugin.AutoFightMode); } }

    internal static string AutoCmd(string text, out bool settle)
    {
        settle = false;
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t == "on") { autoCmd = true; autoOnce = false; }
        else if (t == "off") { autoCmd = false; autoOnce = false; }
        else if (t == "once") autoOnce = true;
        else if (t != "")
            return "auto on（以后的战斗都交给队友）/ auto once（只交这一场）/ auto off（收回）";
        aiStalled = false;
        if (!AutoOn && handoff != null && InFight && !ended) FinishHandoff("收回（第 " + Turn + " 回合）");
        string state = autoOnce ? "这一场交给队友" : AutoOn ? "战斗都交给队友" : "战斗自己打";
        if (InFight && !ResultOpen && !LoseOpen)
        {
            settle = AutoOn;
            return state + (AutoOn ? "：队友接着打完这一场，打完一行交代结果。" : "：轮到你出手。");
        }
        return state + (AutoOn ? "：下一场开打时队友自己打，打完一行交代结果。" : "。");
    }

    class Plan
    {
        public FightNpc who;
        public Skill skill;
        public int slot;
        public FightEnemy target;
        public float value;
        public int cost;
        public bool defend;
    }

    static float aiNext;
    static int aiFails;
    static bool aiStalled;

    // 这回合敌人要打到每个人身上多少（不算闪避）：攻击打一个人；攻击全体打每个人，再按那个人身上的状态算一次（FightEnemy.ExcuseAction）
    static Dictionary<FightNpc, int> Incoming()
    {
        var inc = new Dictionary<FightNpc, int>();
        foreach (var m in Members()) inc[m] = 0;
        foreach (var e in Alive)
        {
            int dmg;
            FightNpc t;
            bool all;
            try { Intent(e, out dmg, out t, out all); }
            catch { continue; }
            if (dmg <= 0) continue;
            if (all) { foreach (var m in Members()) if (m.hp > 0) inc[m] += Fs.CountAtk(dmg, e.states, m.states, true); }
            else if (t != null && inc.ContainsKey(t)) inc[t] += dmg;
        }
        return inc;
    }

    static float HpLoss(FightNpc m, float incoming, float extraShield = 0f)
    {
        return Mathf.Clamp(incoming - m.shield - extraShield, 0f, m.hp);
    }

    // 掉血有多疼：女儿倒下就输了；会被打倒的、血少的更疼
    static float Weight(FightNpc m, int incoming)
    {
        float w = m.enName == "dau" ? 1.5f : 1f;
        if (incoming - m.shield >= m.hp) w *= 3f;
        else if (m.hp < MaxHp(m) * 0.4f) w *= 1.5f;
        return w;
    }

    static int Inc(Dictionary<FightNpc, int> inc, FightNpc m) { int v; return inc.TryGetValue(m, out v) ? v : 0; }

    // 打倒一个敌人：它这回合要打的那几下就没了
    static float KillValue(FightEnemy e, Dictionary<FightNpc, int> inc)
    {
        int dmg;
        FightNpc t;
        bool all;
        Intent(e, out dmg, out t, out all);
        float v = 10f;
        if (dmg <= 0) return v + 3f;
        foreach (var m in Members())
        {
            if (m.hp <= 0 || !(all || m == t)) continue;
            int i = Inc(inc, m);
            int d = all ? Fs.CountAtk(dmg, e.states, m.states, true) : dmg;
            v += (HpLoss(m, i) - HpLoss(m, i - d)) * Weight(m, i);
        }
        return v;
    }

    static float HitValue(FightNpc n, Skill s, FightEnemy e, int slot, Dictionary<FightNpc, int> inc, float times)
    {
        int d = Damage(n, s, e, slot);
        float crit = 1f + n.FightNum(FightAttri.cri) * 0.01f * (n.criRatio - 1f);
        float dealt = d * crit * times;
        float pool = e.hp + e.shield;
        float v = Mathf.Min(dealt, pool) - Mathf.Min(e.shield, dealt) * 0.5f;  // 打在护盾上的只算一半（护盾每回合清掉）
        if (d * times >= pool) v += KillValue(e, inc);
        else
        {
            int edmg;
            FightNpc t;
            bool all;
            Intent(e, out edmg, out t, out all);
            if (s.weaken > 0) v += 0.25f * Mathf.Max(edmg, 5) * Mathf.Min(s.weaken, 2);
            if (s.hurt > 0) v += 0.25f * Atk(n) * Mathf.Min(s.hurt, 2);
            if (s.burn > 0) v += 2f * s.burn;
            if (s.fossil > 0) v += KillValue(e, inc) - 10f;
        }
        return v;
    }

    static float Value(FightNpc n, Skill s, int slot, Dictionary<FightNpc, int> inc, out FightEnemy target)
    {
        target = null;
        var enemies = Alive;
        if (enemies.Count == 0) return 0f;
        switch (s.actionType)
        {
            case "atk":
            case "defAtk":
                {
                    float best = 0f;
                    foreach (var e in enemies)
                    {
                        float v = HitValue(n, s, e, slot, inc, 1f);
                        if (v > best) { best = v; target = e; }
                    }
                    if (s.actionType == "defAtk")
                    {
                        int sh = (int)(Atk(n) * 0.1f * Int(s.data)), i = Inc(inc, n);
                        best += (HpLoss(n, i) - HpLoss(n, i, sh)) * Weight(n, i) + sh * 0.1f;
                    }
                    return best;
                }
            case "aoe":
                {
                    float sum = 0f;
                    foreach (var e in enemies) sum += HitValue(n, s, e, slot, inc, 1f);
                    target = enemies[0];
                    return sum;
                }
            case "randAtk":
                {
                    float sum = 0f;
                    foreach (var e in enemies) sum += HitValue(n, s, e, slot, inc, 3f / enemies.Count);
                    return sum;
                }
            case "allDef":
                {
                    int sh = (int)(Atk(n) * s.power);
                    float v = 0f;
                    foreach (var m in Members())
                    {
                        if (m.hp <= 0) continue;
                        int i = Inc(inc, m);
                        v += (HpLoss(m, i) - HpLoss(m, i, sh)) * Weight(m, i) + sh * 0.05f;
                    }
                    return v;
                }
            case "healing":
                {
                    FightNpc low = null;
                    float ratio = 1f;
                    foreach (var m in Members())
                    {
                        float r = (float)m.hp / Mathf.Max(1, MaxHp(m));
                        if (m.hp > 0 && r < ratio) { ratio = r; low = m; }
                    }
                    if (low == null) return 0f;
                    int heal = (int)(Atk(n) * s.power), miss = MaxHp(low) - low.hp, i = Inc(inc, low);
                    return Mathf.Min(heal, miss) * (ratio < 0.5f ? 1.2f : 0.4f) * (low.enName == "dau" ? 1.3f : 1f) * (HpLoss(low, i) >= low.hp ? 2f : 1f);
                }
            case "allHealing":
                {
                    int heal = (int)(Atk(n) * s.power);
                    float v = 0f;
                    foreach (var m in Members())
                    {
                        if (m.hp <= 0) continue;
                        float r = (float)m.hp / Mathf.Max(1, MaxHp(m));
                        v += Mathf.Min(heal, MaxHp(m) - m.hp) * (r < 0.5f ? 1f : 0.4f);
                    }
                    return v;
                }
            case "addStrength":
                {
                    int alive = 0;
                    foreach (var m in Members()) if (m.hp > 0) alive++;
                    return s.power * alive * 2.5f;
                }
            case "addEnergy":
                return s.power * 6f;
        }
        return 0f;
    }

    // 再防 k 层能少掉多少血（按疼不疼加权）
    static float DefendValue(Dictionary<FightNpc, int> inc, int k)
    {
        int L = DefenseLevel;
        if (L + k > Constant.maxDefenseLevel) return 0f;
        float f = (1f - 0.25f * (L + k)) / (1f - 0.25f * L);
        float v = 0f;
        foreach (var kv in inc)
        {
            var m = kv.Key;
            if (m.hp <= 0) continue;
            v += (HpLoss(m, kv.Value) - HpLoss(m, (int)(kv.Value * f))) * Weight(m, kv.Value);
        }
        return v;
    }

    // 这一回合怎么打：每人至多一招、防御 0~2 层，在能量之内穷举，挑「这回合的得分 + 留下的能量」最大的那一组，先出其中最值的一招。
    // 出完一招重新算（打倒了谁、谁掉了血都会变）。留下的能量每点算 3 分，留到下回合会溢出的部分不算
    static Plan Decide()
    {
        var inc = Incoming();
        int energy = FightSys.energy;
        var options = new List<List<Plan>>();
        foreach (var n in Members())
        {
            if (CantAct(n) != null) continue;
            var list = new List<Plan>();
            foreach (var slot in Slots(n))
            {
                if (slot != n.weapon && !CanSwitch(n)) continue;
                foreach (var s in SkillsOf(n, slot))
                {
                    int cost = s.Cost();
                    if (cost > energy) continue;
                    FightEnemy t;
                    float v = Value(n, s, slot, inc, out t);
                    if (slot != n.weapon) v -= 0.5f;  // 换武器不花能量，只是同分时别来回换
                    if (v <= 0.5f || (NeedsTarget(s) && t == null)) continue;
                    list.Add(new Plan { who = n, skill = s, slot = slot, target = t, value = v, cost = cost });
                }
            }
            if (list.Count > 0) options.Add(list);
        }
        var defend = new[] { 0f, DefendValue(inc, 1), DefendValue(inc, 2) };
        int keepCap = Math.Max(0, MaxEnergy - TurnEnergy);
        const float lambda = 3f;
        float bestScore = lambda * Math.Min(energy, keepCap);
        List<Plan> bestSet = null;
        int bestDef = 0;
        var chosen = new List<Plan>();
        Action<int, int, float> search = null;
        search = (i, left, value) =>
        {
            if (i == options.Count)
            {
                for (int d = 0; d <= Math.Min(Constant.maxDefenseLevel - DefenseLevel, left); d++)
                {
                    float total = value + defend[Math.Min(d, 2)] + lambda * Math.Min(left - d, keepCap);
                    if (total > bestScore + 0.01f) { bestScore = total; bestSet = new List<Plan>(chosen); bestDef = d; }
                }
                return;
            }
            search(i + 1, left, value);
            foreach (var p in options[i])
            {
                if (p.cost > left) continue;
                chosen.Add(p);
                search(i + 1, left - p.cost, value + p.value);
                chosen.RemoveAt(chosen.Count - 1);
            }
        };
        search(0, energy, 0f);
        if (bestSet == null) return null;
        Plan first = null;
        foreach (var p in bestSet) if (first == null || p.value > first.value) first = p;
        if (first != null) return first;
        return bestDef > 0 ? new Plan { defend = true, cost = 1 } : null;
    }

    static void AiStep()
    {
        if (handoff == null) BeginHandoff("从第 " + Turn + " 回合起");
        var h = handoff;  // 这一下要是打赢了，OnEnd 只记下结果，记录等下一轮 Tick 再写（这一下也要写进去）
        Plan p;
        try { p = Decide(); }
        catch (Exception e)
        {
            LudusLunaePlugin.Log.LogWarning("队友想不出怎么打：" + e);
            p = null;
        }
        string text;
        bool ok;
        if (p == null)
        {
            ok = EndTurnNow();
            text = ok ? "结束回合（能量 " + FightSys.energy + " 留着）" : "结束回合没点上";
        }
        else if (p.defend) ok = DoDefend(out text);
        else ok = Use(p.who, p.skill, p.slot, p.target, out text);
        h.acts.Add("第 " + Turn + " 回合：" + text);
        SelfPlay.Acted("队友出手");
        if (ok) { aiFails = 0; return; }
        aiFails++;
        if (aiFails >= 2 && !EndTurnNow())
        {
            aiStalled = true;
            SelfPlay.Event("note", Json.Field("text", "队友卡住了（" + text + "），这一场交还给你：skill / defend / endturn"));
        }
    }

    // ---- 托管记录：每场一条，写进在玩的那个人文件夹里的 autofight.jsonl（没分玩家文件夹时在 data/selfplay/），打完在事件里一行交代 ----
    class Handoff
    {
        public DateTime start = DateTime.Now;
        public string how = "", place = "", mode = "";
        public int fromTurn;
        public List<string> enemies = new List<string>();
        public Dictionary<FightNpc, int> hp0 = new Dictionary<FightNpc, int>();
        public List<string> acts = new List<string>();
    }

    static Handoff handoff;

    static void BeginHandoff(string how)
    {
        handoff = new Handoff { how = how, place = Where().Trim('（', '）'), fromTurn = Math.Max(1, Turn), mode = autoOnce ? "这一场" : "一直交给队友" };
        foreach (var e in Roster()) handoff.enemies.Add(EnemyName(e));
        foreach (var m in Members()) handoff.hp0[m] = m.hp;
    }

    static string HpText(Dictionary<FightNpc, int> hp0)
    {
        var parts = new List<string>();
        foreach (var kv in hp0)
        {
            var m = kv.Key;
            if (m.hp <= 0 && kv.Value > 0) { parts.Add(Who(m) + " 倒下了"); continue; }
            int d = m.hp - kv.Value;
            parts.Add(Who(m) + " " + kv.Value + "→" + m.hp + "/" + MaxHp(m) + (d == 0 ? "" : "（" + (d > 0 ? "+" : "") + d + "）"));
        }
        return string.Join("，", parts.ToArray());
    }

    static void FinishHandoff(string result)
    {
        var h = handoff;
        handoff = null;
        if (h == null) return;
        try
        {
            int turns = Math.Max(1, Turn - h.fromTurn + 1);
            var loot = result.StartsWith("赢") ? LootText(lastLoot) : "";
            var line = result + "（" + (h.fromTurn > 1 ? "第 " + h.fromTurn + " 回合起，" : "") + turns + " 回合）：" + HpText(h.hp0)
                + (loot != "" && loot != "没有" ? "；拿到 " + loot : "") + "。";
            SelfPlay.Event("fight", Json.Field("what", "auto") + "," + Json.Field("text", line));
            var members = new List<string>();
            foreach (var kv in h.hp0) members.Add(Who(kv.Key) + " " + kv.Value + "→" + kv.Key.hp + "/" + MaxHp(kv.Key));
            var record = "{" + Json.Field("start", h.start.ToString("yyyy-MM-ddTHH:mm:ss")) + "," + Json.Field("end", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")) + ","
                + Json.Field("place", h.place) + "," + Json.Field("mode", h.mode) + "," + Json.Field("how", h.how) + ","
                + Json.Field("fromTurn", h.fromTurn) + "," + Json.Field("turns", turns) + "," + Json.Field("result", result) + ","
                + Json.Field("enemies", h.enemies.ToArray()) + "," + Json.Field("members", members.ToArray()) + ","
                + Json.Field("loot", loot) + "," + Json.Field("acts", h.acts.ToArray()) + "," + Json.Field("summary", line) + "}";
            var dir = Players.WriteDir;  // 交给队友的是在玩的那个人，记进它的文件夹（没分文件夹时还是 data/selfplay/）
            if (dir == null) return;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "autofight.jsonl"), record + "\n", new UTF8Encoding(false));
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记托管失败：" + e.Message); }
    }

    // ================= 战斗里的事（Harmony 挂钩调过来） =================
    static bool ended;
    static string pendingFinish;  // 交给队友的这一场打完了：结果先记下，下一轮 Tick 写记录（AiStep 还要把最后一下加进去）
    static List<int[]> lastLoot = new List<int[]>();
    static Dictionary<FightNpc, int> fightHp0 = new Dictionary<FightNpc, int>();
    static Dictionary<object, Look> turnSnap;
    static readonly List<string> enemyActs = new List<string>();
    static readonly HashSet<FightNpc> attacked = new HashSet<FightNpc>();

    internal static void OnStart()
    {
        // 这几样不管自己玩开没开都重置：中途打开自己玩时，敌人名单不能是上一场的
        ended = false;
        roster = new List<FightEnemy>(Alive);
        turnSnap = null;
        enemyActs.Clear();
        attacked.Clear();
        lastLoot = new List<int[]>();
        fightHp0 = new Dictionary<FightNpc, int>();
        foreach (var m in Members()) fightHp0[m] = m.hp;
        if (!SelfPlay.On) return;
        // 这一场里这个会话没见过的怪物、首领，截一张（Sights.cs）
        try { Sights.OnFight(roster); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记第一次遇到的怪物失败：" + e.Message); }
        aiFails = 0;
        aiStalled = false;
        if (handoff != null) FinishHandoff(pendingFinish ?? "没打完（上一场）");
        pendingFinish = null;
        var names = new List<string>();
        foreach (var e in roster) names.Add(EnemyName(e) + (Flying(e) ? "（飞）" : ""));
        SelfPlay.Event("fight", Json.Field("what", "start") + "," + Json.Field("text", string.Join("、", names.ToArray()) + Where()
            + (AutoOn ? "，交给队友打" : "")));
        if (AutoOn) BeginHandoff("开打就交给队友");
    }

    internal static void OnEnemyTurn()
    {
        if (!SelfPlay.On) return;
        turnSnap = Snap();
        enemyActs.Clear();
        attacked.Clear();
    }

    internal static void OnEnemyAct(FightEnemy e, IntentionEnu state, List<int> num)
    {
        if (!SelfPlay.On) return;
        try
        {
            string word = state.ToString();
            Intention it;
            if (Cfg.intentions.TryGetValue(state.ToString(), out it) && it.uiType >= 0 && it.uiType < Constant.intentionCh.Length) word = Constant.intentionCh[it.uiType];
            string what = word;
            if (state == IntentionEnu.attack || state == IntentionEnu.defAttack)
            {
                if (e.atkObjtct != null) { what += " " + Who(e.atkObjtct); attacked.Add(e.atkObjtct); }
                if (num != null && num.Count > 0) what += " " + num[0];
            }
            else if (num != null && num.Count > 0)  // 攻击全体、防御、愈疗：和头顶一样写数
            {
                if (state == IntentionEnu.allAttack) foreach (var m in Members()) if (m.hp > 0) attacked.Add(m);
                what += " " + num[0];
            }
            enemyActs.Add(EnemyName(e) + "：" + what);
        }
        catch { }
    }

    // 敌方回合打完、新回合开始（NextTurn 之后）：敌人干了什么、我方掉了多少血、身上多了什么
    internal static void OnPlayerTurn()
    {
        if (!SelfPlay.On) return;
        if (Turn <= 1 || turnSnap == null) { enemyActs.Clear(); return; }
        var text = TurnSummary(Turn - 1);
        if (handoff != null) handoff.acts.Add(text);
        else SelfPlay.Event("fight", Json.Field("what", "turn") + "," + Json.Field("text", text));
    }

    static string TurnSummary(int turn)
    {
        var parts = new List<string>(enemyActs);
        foreach (var e in Roster())
        {
            Look b;
            if (turnSnap.TryGetValue(e, out b) && b.alive && b.states.ContainsKey(AbilityRaw(IntentionEnu.fossil)))
                parts.Add(EnemyName(e) + "：石化着，没动");
        }
        var changes = new List<string>();
        foreach (var m in Members())
        {
            Look b;
            if (!turnSnap.TryGetValue(m, out b) || !b.alive) continue;
            var c = new List<string>();
            if (m.hp <= 0) c.Add("倒下了");
            else if (m.hp != b.hp) c.Add("HP " + b.hp + "→" + m.hp + "/" + MaxHp(m));
            else if (attacked.Contains(m)) c.Add("没掉血");
            var added = NewStates(b.states, m.states);
            if (added != "" && m.hp > 0) c.Add("身上多了 " + added);
            if (c.Count > 0) changes.Add(Who(m) + " " + string.Join("，", c.ToArray()));
        }
        var alive = Alive;
        foreach (var e in Roster())
        {
            Look b;
            if (!turnSnap.TryGetValue(e, out b) || !b.alive) continue;
            var c = new List<string>();
            if (!alive.Contains(e) || e.hp <= 0) c.Add("倒下了");
            else
            {
                if (e.hp != b.hp) c.Add("HP " + b.hp + "→" + e.hp + "/" + (int)e.maxHp);
                if (e.shield > b.shield) c.Add("护盾 " + e.shield);
                var added = NewStates(b.states, e.states);
                if (added != "") c.Add("身上多了 " + added);
            }
            if (c.Count > 0) changes.Add(EnemyName(e) + " " + string.Join("，", c.ToArray()));
        }
        turnSnap = null;
        enemyActs.Clear();
        attacked.Clear();
        return "第 " + turn + " 回合：" + (parts.Count == 0 ? "敌人没动" : string.Join("；", parts.ToArray()))
            + (changes.Count == 0 ? "。" : "。结果：" + string.Join("；", changes.ToArray()) + "。");
    }

    internal static void OnEnd(bool win)
    {
        if (!SelfPlay.On || ended) return;
        ended = true;
        try
        {
            if (turnSnap != null)  // 敌方回合里打完的（灼烧烧死最后一个，或者我方倒下）
            {
                var text = TurnSummary(Turn);
                if (handoff != null) handoff.acts.Add(text);
                else SelfPlay.Event("fight", Json.Field("what", "turn") + "," + Json.Field("text", text));
            }
            lastLoot = new List<int[]>();  // 赢了的战利品下一轮 Tick 再读（见 Tick）
            if (handoff != null) pendingFinish = win ? "赢了" : "输了";
            else  // 拿到的东西由「拿取全部」那一下记成 [得到]，这里不重复
                SelfPlay.Event("fight", Json.Field("what", "end") + "," + Json.Field("text", (win ? "赢了" : "输了") + "（" + Turn + " 回合）："
                    + HpText(fightHp0) + "。"));
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记战斗结果失败：" + e.Message); }
        autoOnce = false;
    }

    // ================= 每 0.25 秒 =================
    static float resultSeen = -1f, loseSeen = -1f, takeTried;
    static int takeTries;
    static bool resultGaveUp;

    internal static void Tick(float now)
    {
        if (!SelfPlay.On || DataSys.Instan == null || Fs == null || Ts == null) return;
        if (pendingFinish != null)
        {
            // 战利品在这里读，不在 OnEnd 读：WinFight 那一帧游戏刚 DestroyChilds 上一场的格子（帧末才真删），那时读会把上一场的也算进来
            if (pendingFinish == "赢了") lastLoot = ReadLoot();
            if (handoff != null) FinishHandoff(pendingFinish);
            pendingFinish = null;
        }
        bool talking = SelfPlay.ChatBusy() || SelfPlay.MenuOpen();
        // 打赢了：拿取全部（没有选择）。金币记成 [得到]，物品由 ItemPatch 记
        if (ResultOpen)
        {
            if (resultSeen < 0f) { resultSeen = now; takeTries = 0; resultGaveUp = false; }
            if (!resultGaveUp && !talking && now - resultSeen >= 1.2f && now - takeTried >= 3f) TakeAll(now);
        }
        else resultSeen = -1f;
        // 输了：结束（这趟探险到此为止）
        if (LoseOpen)
        {
            if (loseSeen < 0f) loseSeen = now;
            if (!talking && now - loseSeen >= 2f)
            {
                loseSeen = now + 3f;
                var b = FindButton(Ts.Find("lose"), "ClickLoseFight");
                if (b != null) SelfPlay.Press(b.gameObject);
                else Fs.ClickLoseFight();
                SelfPlay.Acted("输了，结束");
            }
        }
        else loseSeen = -1f;
        // 收回了（auto off 或者 modes.json 关了）
        if (handoff != null && !AutoOn && InFight && !ended) FinishHandoff("收回（第 " + Turn + " 回合）");
        if (AutoOn && !aiStalled && PlayerTurn && !Slogan && !talking && FightSys.fightPhase == FightSys.FightPhase.normal && now >= aiNext)
        {
            aiNext = now + 0.9f;
            AiStep();
        }
        if (!PlayerTurn) aiFails = 0;
        WalkTick(now, talking);
    }

    static void TakeAll(float now)
    {
        takeTried = now;
        if (++takeTries > 3) { resultGaveUp = true; SelfPlay.Event("note", Json.Field("text", "「拿取全部」点了没反应，自己 click 一下")); return; }
        var loot = ReadLoot();
        var b = FindButton(Ts.Find("result"), "ClickTakeAllItem");
        if (b != null) SelfPlay.Press(b.gameObject);
        else Fs.ClickTakeAllItem();
        if (!ResultOpen)
            foreach (var it in loot)
                if (it[0] == -1 && it[1] > 0) SelfPlay.Event("item", Json.Field("name", "金币") + "," + Json.Field("num", it[1]));
        SelfPlay.Acted("拿取全部");
    }

    // 不算轮到 agent 的时候（SelfPlay.UpdatePhase 经 TextPlay.Busy 问）
    internal static string Busy()
    {
        try
        {
            if (walking) return "在走（walk）";
            if (!InFight) return null;
            if (ResultOpen) return resultGaveUp ? null : "拿战利品";
            if (LoseOpen) return "输了，在收拾";
            if (Slogan) return "回合开场";
            if (EnemyTurn) return "敌方回合";
            if (AutoOn && !aiStalled) return "队友在打";
        }
        catch { }
        return null;
    }

    // ================= 探险里一直走（walk） =================
    static bool walking;
    static int walkSteps;
    static float walkNext, walkUntil;

    static Transform Ui(int i) { return UiSys.Instan.uiTrans[i]; }
    static bool Exploring { get { try { return Ui(23).gameObject.activeInHierarchy; } catch { return false; } } }
    static bool EventOpen { get { try { return Ui(23).Find("randomEvent").gameObject.activeSelf; } catch { return false; } } }

    static bool PlaceOpen
    {
        get
        {
            try { var t = fPlaceTs.GetValue(ExploSys.Instan) as Transform; return t != null && t.parent.gameObject.activeInHierarchy; }
            catch { return false; }
        }
    }

    static bool FinishedOpen { get { try { return Ui(24).Find("finished").gameObject.activeInHierarchy; } catch { return false; } } }

    static List<GameObject> PathButtons()
    {
        var list = new List<GameObject>();
        try
        {
            var path = Ui(23).Find("path");
            if (!path.gameObject.activeInHierarchy) return list;
            for (int i = 0; i < path.childCount; i++)
            {
                var ch = path.GetChild(i);
                if (ch.gameObject.activeSelf && ch.GetComponent<UiBtn>() != null) list.Add(ch.gameObject);
            }
        }
        catch { }
        return list;
    }

    // 只有一条路时，点「前进」会走到哪（ExploSys.TransPoint：每段 4 格，0 起点 → 1 普通敌人 → 分两条路，各 2 格）
    static string NextPoint(out bool boss)
    {
        boss = false;
        try
        {
            var es = ExploSys.Instan;
            int num = (int)fLevel.GetValue(es) % 4, stage = (int)fStage.GetValue(es), lane = (int)fPathType.GetValue(es);
            var mt = fMapType.GetValue(es) as MapType;
            if (num == 0) return "普通敌人";
            if (num == 2)
            {
                var paths = fPaths.GetValue(es) as System.Collections.IList;
                var list = paths[lane] as System.Collections.IList;
                var t = list[1].ToString();
                boss = t == PlaceType.boss.ToString();
                return TextPlay.PlaceTypeZh(t);
            }
            if (num == 3) return mt != null && stage + 1 >= mt.chooseNum ? "终点（这趟探险走完）" : "下一段的起点";
        }
        catch { }
        return "?";
    }

    internal static string WalkCmd(string text, out bool ok)
    {
        ok = false;
        if (!Exploring) return "不在探险里（walk 是探险地图上一直往前走）";
        if (InFight) return "在打仗：" + (AutoOn ? "交给队友在打，wait" : "skill / defend / endturn");
        if (FinishedOpen) return "这趟探险走完了：click 结束探险";
        if (EventOpen || PlaceOpen) return "这里有事要拿主意（看清单，click 选项）";
        var btns = PathButtons();
        if (btns.Count == 0) return "路还没出来，wait 一下";
        var want = (text ?? "").Trim();
        if (btns.Count >= 2)
        {
            if (want == "") return "岔路：walk 金色的路 / walk 蓝色的路（或 click）。" + TextPlay.ExploPathText();
            GameObject pick = null;
            foreach (var b in btns) if (SelfPlay.LabelOf(b).Contains(want) || want.Contains(SelfPlay.LabelOf(b).Replace("的路", ""))) pick = b;
            if (pick == null) return "没有「" + want + "」这条路。" + TextPlay.ExploPathText();
            var lane = SelfPlay.LabelOf(pick);
            SelfPlay.Event("chose", Json.Field("text", "探险路线：" + lane));
            SelfPlay.Press(pick);
            walkSteps = 1;
            want = lane;
        }
        else walkSteps = 0;
        walking = true;
        walkNext = Time.unscaledTime + (walkSteps > 0 ? 1.2f : 0f);
        walkUntil = Time.unscaledTime + 60f;
        SelfPlay.Acted("在走");
        ok = true;
        return (walkSteps > 0 ? "走" + want + "，" : "") + "往前走（到岔路、随机事件、泉水宝箱、战斗、首领前一格停下）";
    }

    static void StopWalk(string why)
    {
        walking = false;
        SelfPlay.Event("walk", Json.Field("text", (walkSteps > 0 ? "走了 " + walkSteps + " 步，" : "") + why));
        SelfPlay.Acted("停下");
    }

    static void WalkTick(float now, bool talking)
    {
        if (!walking) return;
        if (!SelfPlay.On) { walking = false; return; }
        if (SelfPlay.MenuOpen()) { StopWalk("路上有对话选项"); return; }
        if (talking) { walkNext = Mathf.Max(walkNext, now + 0.8f); walkUntil = Mathf.Max(walkUntil, now + 30f); return; }
        if (InFight)
        {
            if (AutoOn && !aiStalled) { walkUntil = now + 60f; return; }  // 交给队友打，打完接着走
            if (ResultOpen && !resultGaveUp) { walkUntil = now + 30f; return; }
            StopWalk("遇到战斗");
            return;
        }
        if (LearnOpen) { StopWalk("打完了，要学新技能"); return; }
        if (FinishedOpen) { StopWalk("这趟探险走完了（click 结束探险）"); return; }
        if (!Exploring) { StopWalk("不在探险里了"); return; }
        if (EventOpen) { StopWalk("遇到随机事件"); return; }
        if (PlaceOpen) { StopWalk("到了一个地方（泉水 / 宝箱），要拿主意"); return; }
        if (now > walkUntil) { StopWalk("走了一阵没等到路（screen 看看）"); return; }
        var btns = PathButtons();
        if (btns.Count >= 2) { StopWalk("到了岔路：" + TextPlay.ExploPathText()); return; }
        if (btns.Count == 0 || now < walkNext) return;
        bool boss;
        var next = NextPoint(out boss);
        if (boss && walkSteps > 0) { StopWalk("下一格是首领（准备好了再 walk）"); return; }
        SelfPlay.Press(btns[0]);
        walkSteps++;
        walkNext = now + 1.2f;
        walkUntil = now + 30f;
        LudusLunaePlugin.Log.LogInfo("walk：前进 → " + next);
    }
}

// ---- 挂钩 ----
[HarmonyPatch(typeof(FightSys), "StartFight")]
static class FightStartPatch
{
    static void Postfix()
    {
        try { FightPlay.OnStart(); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记开战失败：" + e.Message); }
    }
}

[HarmonyPatch(typeof(FightSys), "StartEnemyTurn")]
static class FightEnemyTurnPatch
{
    static void Prefix()
    {
        try { FightPlay.OnEnemyTurn(); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记敌方回合失败：" + e.Message); }
    }
}

[HarmonyPatch(typeof(FightSys), "NextTurn")]
static class FightNextTurnPatch
{
    static void Postfix()
    {
        try { FightPlay.OnPlayerTurn(); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记新回合失败：" + e.Message); }
    }
}

[HarmonyPatch(typeof(FightSys), "WinFight")]
static class FightWinPatch
{
    static void Postfix()
    {
        try { FightPlay.OnEnd(true); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记打赢失败：" + e.Message); }
    }
}

[HarmonyPatch(typeof(FightSys), "LoseFight")]
static class FightLosePatch
{
    static void Postfix()
    {
        try { FightPlay.OnEnd(false); }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记打输失败：" + e.Message); }
    }
}

[HarmonyPatch(typeof(FightEnemy), "ExcuseAction")]
static class FightEnemyActPatch
{
    static void Prefix(FightEnemy __instance, IntentionEnu state, List<int> num)
    {
        try { FightPlay.OnEnemyAct(__instance, state, num); }
        catch { }
    }
}

[HarmonyPatch(typeof(FightNpc), "UsingSkillDamage")]
static class FightHitPatch
{
    static void Postfix(FightEnemy enemy, int __result)
    {
        try
        {
            if (FightPlay.hits.Count > 50) FightPlay.hits.Clear();  // 手点技能时没人清
            FightPlay.hits.Add(new KeyValuePair<FightEnemy, int>(enemy, __result));
        }
        catch { }
    }
}

// 升级写成一条事件：升了级、自动学会了新技能，不写的话只会在下一场战斗那一屏里突然多出一招。
// FightNpc.LevelUp 最后调 AddLevelUpUi(npc, 加了什么, 第几次学技能)，这时等级已经加好。learnSkill 照 FightSys.ShowLevelUpUi 的规矩：
// 女儿第 0、1 次、队友第 0 次是直接学会（defaultSkill[learnSkill + 2 / + 1]），再往后是二选一（学技能的窗口停下来等 click）
[HarmonyPatch(typeof(FightSys), "AddLevelUpUi")]
static class LevelUpPatch
{
    static void Postfix(FightNpc npc, int learnSkill)
    {
        try
        {
            if (!SelfPlay.On || npc == null) return;
            bool dau = npc.enName == "dau";
            var text = FightPlay.Who(npc) + " 升到 Lv." + (npc.level + 1);  // level 从 0 数，画面上（和战斗那一屏）是 +1
            if (learnSkill >= 0)
            {
                if (dau ? learnSkill < 2 : learnSkill < 1)
                {
                    int idx = npc.npc.defaultSkill[learnSkill + (dau ? 2 : 1)];
                    text += "，学会了「" + SelfPlay.Clean(DataSys.Instan.dataCfg.skills[idx].chName, 20) + "」";
                }
                else text += "，可以学一招新技能（二选一，打完会停下来让你挑）";
            }
            SelfPlay.Event("levelup", Json.Field("text", text));
        }
        catch (Exception e) { LudusLunaePlugin.Log.LogWarning("记升级失败：" + e.Message); }
    }
}
