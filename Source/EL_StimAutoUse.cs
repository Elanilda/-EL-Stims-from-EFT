using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimWorld
{
    // ============================================================
    // 针剂自动使用系统
    // 开关链路: 管理用药方案"自主治疗"复选框(EL_AutoTreatState)
    //          → 思考树节点EL_JobGiver_AutoInjectStim(优先于征召指令分支)
    //          → 复用EL_Job_InjectStimSelf原地注射
    // 条件链路: 针剂ThingDef挂EL_DefModExtension_StimAutoUse声明分类/优先级/条件组
    //          → pawn身上hediff满足任一条件组 → 选优先级最高者使用
    // 全局规则R0: 针剂主hediff已在pawn身上 → 跳过该针剂全部检测
    // 冷却规则: 注射后对其全部分类写入冷却 = 效果时长(均值)/3
    // ============================================================

    /// <summary>
    /// 针剂hediff的耗竭标记扩展: 声明该hediff是否消耗休息值/饮食值/随机部位伤害,
    /// 供其他针剂的条件类型(StimDrainActive/RandomDamageStimActive)检测
    /// (避免C#按comp类型硬编码, 声明式标记随hediffDef配置)
    /// </summary>
    public class EL_DefModExtension_StimDrainMarker : DefModExtension
    {
        public bool drainsRest;

        public bool drainsHunger;

        public bool dealsRandomPartDamage;
    }

    /// <summary>
    /// 针剂自动使用主扩展: 挂在针剂ThingDef上
    /// </summary>
    public class EL_DefModExtension_StimAutoUse : DefModExtension
    {
        // 自动使用分类(Perfotoran/Propital为双分类, 冷却与互斥均按分类判定)
        public List<string> autoUseCategories;

        // 优先等级: 越大越先被选中; 同优先级保持先扫到的
        public float autoUsePriority;

        // 注射施加的主hediff(R0规则与冷却时长的依据)
        public HediffDef injectedHediff;

        // 条件组: 组内AND, 组间OR; null或空 = 永不自动使用
        public List<EL_StimConditionGroup> conditionGroups;
    }

    public class EL_StimConditionGroup
    {
        public List<EL_StimCondition> conditions;
    }

    public enum EL_StimCondType
    {
        Drafted,                   // 征召状态
        RestBelow, RestAbove,      // 休息值 < / > threshold
        FoodAbove, FoodBelow,      // 饮食值 > / <= threshold
        PainAtLeast,               // 疼痛 >= threshold (0~1)
        BleedDeathWithinHours,     // 失血预计死亡时间 <= threshold小时
        InjuryDamageSumMin,        // 损伤严重度合计 >= threshold (nonSolidOnly时仅计非solid部位)
        InjuryDamageSumMax,        // 损伤严重度合计 <= threshold
        HasNoInjury,               // 身上无任何损伤hediff
        BandagedInjuryDamageMin,   // 已包扎损伤合计 >= threshold
        BandagedInjuryCountMin,    // 已包扎损伤条数 >= threshold
        OverEncumbered,            // 超重(MassUtility)
        NeedsImmunity,             // 存在可免疫且免疫<1的hediff
        StimDrainActive,           // 存在带耗竭标记的针剂hediff(drainRest/drainHunger指定类型)
        RandomDamageStimActive,    // 存在带随机部位伤害标记的针剂hediff
        TemperatureUncomfortable,  // 环境温度在舒适区间外
        HasAnyHediffOf             // 身上存在指定hediff任一
    }

    public class EL_StimCondition
    {
        public EL_StimCondType type;

        public float threshold;

        // 仅计非solid部位(InjuryDamageSumMin用)
        public bool nonSolidOnly;

        // 耗竭标记检测类型(StimDrainActive用)
        public bool drainRest;

        public bool drainHunger;

        // 指定hediff清单(HasAnyHediffOf用)
        public List<HediffDef> hediffs;

        public bool Satisfied(Pawn pawn)
        {
            switch (type)
            {
                case EL_StimCondType.Drafted:
                    return pawn.Drafted;
                case EL_StimCondType.RestBelow:
                    return pawn.needs?.rest?.CurLevel < threshold;
                case EL_StimCondType.RestAbove:
                    return pawn.needs?.rest?.CurLevel > threshold;
                case EL_StimCondType.FoodAbove:
                    return pawn.needs?.food?.CurLevel > threshold;
                case EL_StimCondType.FoodBelow:
                    return pawn.needs?.food?.CurLevel <= threshold;
                case EL_StimCondType.PainAtLeast:
                    return pawn.health.hediffSet.PainTotal >= threshold;
                case EL_StimCondType.BleedDeathWithinHours:
                    return HealthUtility.TicksUntilDeathDueToBloodLoss(pawn) <= threshold * 2500f;
                case EL_StimCondType.InjuryDamageSumMin:
                {
                    float sum = 0f;
                    var hediffs = pawn.health.hediffSet.hediffs;
                    for (int i = 0; i < hediffs.Count; i++)
                    {
                        if (!(hediffs[i] is Hediff_Injury inj)) continue;
                        if (nonSolidOnly && inj.Part != null
                            && inj.Part.def.IsSolid(inj.Part, pawn.health.hediffSet.hediffs)) continue;
                        sum += inj.Severity;
                    }
                    return sum >= threshold;
                }
                case EL_StimCondType.InjuryDamageSumMax:
                {
                    float sum = 0f;
                    var hediffs = pawn.health.hediffSet.hediffs;
                    for (int i = 0; i < hediffs.Count; i++)
                    {
                        if (hediffs[i] is Hediff_Injury inj) sum += inj.Severity;
                    }
                    return sum <= threshold;
                }
                case EL_StimCondType.HasNoInjury:
                {
                    var hediffs = pawn.health.hediffSet.hediffs;
                    for (int i = 0; i < hediffs.Count; i++)
                    {
                        if (hediffs[i] is Hediff_Injury) return false;
                    }
                    return true;
                }
                case EL_StimCondType.BandagedInjuryDamageMin:
                {
                    float sum = 0f;
                    var hediffs = pawn.health.hediffSet.hediffs;
                    for (int i = 0; i < hediffs.Count; i++)
                    {
                        if (hediffs[i] is Hediff_Injury inj && inj.IsTended()) sum += inj.Severity;
                    }
                    return sum >= threshold;
                }
                case EL_StimCondType.BandagedInjuryCountMin:
                {
                    int n = 0;
                    var hediffs = pawn.health.hediffSet.hediffs;
                    for (int i = 0; i < hediffs.Count; i++)
                    {
                        if (hediffs[i] is Hediff_Injury inj && inj.IsTended()) n++;
                    }
                    return n >= (int)threshold;
                }
                case EL_StimCondType.OverEncumbered:
                    return MassUtility.IsOverEncumbered(pawn);
                case EL_StimCondType.NeedsImmunity:
                {
                    var hediffs = pawn.health.hediffSet.hediffs;
                    for (int i = 0; i < hediffs.Count; i++)
                    {
                        if (hediffs[i].def.CompProps<HediffCompProperties_Immunizable>() == null) continue;
                        var record = pawn.health.immunity.GetImmunityRecord(hediffs[i].def);
                        if (record == null || record.immunity < 1f) return true;
                    }
                    return false;
                }
                case EL_StimCondType.StimDrainActive:
                {
                    var hediffs = pawn.health.hediffSet.hediffs;
                    for (int i = 0; i < hediffs.Count; i++)
                    {
                        var marker = hediffs[i].def.GetModExtension<EL_DefModExtension_StimDrainMarker>();
                        if (marker == null) continue;
                        if (drainRest && marker.drainsRest) return true;
                        if (drainHunger && marker.drainsHunger) return true;
                    }
                    return false;
                }
                case EL_StimCondType.RandomDamageStimActive:
                {
                    var hediffs = pawn.health.hediffSet.hediffs;
                    for (int i = 0; i < hediffs.Count; i++)
                    {
                        var marker = hediffs[i].def.GetModExtension<EL_DefModExtension_StimDrainMarker>();
                        if (marker?.dealsRandomPartDamage == true) return true;
                    }
                    return false;
                }
                case EL_StimCondType.TemperatureUncomfortable:
                {
                    // 1.6/1.7通用: 舒适温度区间扩展方法GenTemperature.ComfortableTemperatureRange
                    FloatRange range = pawn.ComfortableTemperatureRange();
                    return !range.Includes(pawn.AmbientTemperature);
                }
                case EL_StimCondType.HasAnyHediffOf:
                    if (hediffs == null) return false;
                    for (int i = 0; i < hediffs.Count; i++)
                    {
                        if (pawn.health.hediffSet.GetFirstHediffOfDef(hediffs[i]) != null) return true;
                    }
                    return false;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// 自动使用思考树节点: 门控复用原版JobGiver_SelfTend条件(去除selfTend开关),
    /// 改由"管理用药方案"中的自主治疗开关驱动; 插入位置在排队job之后、征召指令分支之前
    /// </summary>
    public class EL_JobGiver_AutoInjectStim : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            // 征召待机的pawn停在永续Wait_Combat job(ToilCompleteMode.Never), 思考树不会被咨询,
            // 本节点实际仅服务未征召状态; 征召路径由EL_AutoInjectTicker下发ordered job
            return EL_AutoInject.TryFindJob(pawn);
        }
    }

    /// <summary>
    /// 自动注射选针逻辑(公开静态, 双路径复用):
    /// 未征召 → 思考树节点EL_JobGiver_AutoInjectStim
    /// 征召待机 → EL_AutoInjectTicker定时扫描后TryTakeOrderedJob下发
    /// </summary>
    public static class EL_AutoInject
    {
        private static JobDef cachedInjectJob;

        private static JobDef InjectJob => cachedInjectJob
            ?? (cachedInjectJob = DefDatabase<JobDef>.GetNamedSilentFail("EL_Job_InjectStimSelf"));

        // 征召待机判定: 处于征召且当前job为永续Wait/Wait_Combat(即无玩家指令)
        public static bool IsDraftedIdle(Pawn pawn)
        {
            if (pawn.drafter?.Drafted != true) return false;
            JobDef def = pawn.CurJob?.def;
            return def == JobDefOf.Wait || def == JobDefOf.Wait_Combat;
        }

        // verbose诊断: 每pawn缓存上一次扫描摘要, 仅在内容变化时输出(需开启"详细日志"选项)
        private static readonly Dictionary<int, string> lastDebugSummary = new Dictionary<int, string>();

        private static void DebugLog(Pawn pawn, string summary)
        {
            if (summary == null || !Prefs.LogVerbose) return;
            if (lastDebugSummary.TryGetValue(pawn.thingIDNumber, out var prev) && prev == summary) return;
            lastDebugSummary[pawn.thingIDNumber] = summary;
            Log.Message("[EL]Stims auto-inject scan " + pawn.LabelShortCap + ": " + summary);
        }

        public static Job TryFindJob(Pawn pawn)
        {
            // —— 门控层: 复用JobGiver_SelfTend条件(去除selfTend开关), 追加玩家阵营约束 ——
            if (!pawn.RaceProps.Humanlike
                || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                || pawn.InAggroMentalState)
            {
                DebugLog(pawn, "blocked by gate (humanlike/manipulation/mentalState)");
                return null;
            }
            if (pawn.IsColonist && pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))
            {
                DebugLog(pawn, "blocked by gate (doctor work disabled)");
                return null;
            }
            // 自动使用是玩家管理功能: 仅玩家阵营非囚犯pawn参与(奴隶/殖民者允许)
            if (pawn.Faction != Faction.OfPlayer || pawn.IsPrisonerOfColony)
            {
                DebugLog(pawn, "blocked by gate (faction/prisoner)");
                return null;
            }
            if (!pawn.Spawned || !pawn.Map.reservationManager.CanReserve(pawn, pawn))
            {
                DebugLog(pawn, "blocked by gate (spawned/reserve)");
                return null;
            }
            DrugPolicy policy = pawn.drugs?.CurrentPolicy;
            if (policy == null || InjectJob == null)
            {
                DebugLog(pawn, "blocked by gate (policy/jobdef null)");
                return null;
            }

            // —— 扫描层: 背包针剂按扩展声明筛选; verbose模式记录各针剂拦截原因 ——
            bool verbose = Prefs.LogVerbose;
            System.Text.StringBuilder sb = verbose ? new System.Text.StringBuilder() : null;
            Thing best = null;
            float bestPrio = float.MinValue;
            var innerContainer = pawn.inventory.innerContainer;
            for (int i = 0; i < innerContainer.Count; i++)
            {
                string reason;
                var ext = innerContainer[i].def.GetModExtension<EL_DefModExtension_StimAutoUse>();
                if (ext?.autoUseCategories == null || ext.conditionGroups == null
                    || ext.autoUseCategories.Count == 0 || ext.conditionGroups.Count == 0)
                {
                    reason = "(no-ext)"; // 未配置扩展 = 不参与自动使用(Obdolbos类)
                }
                else if (!EL_AutoTreatState.IsEnabled(policy, innerContainer[i].def))
                {
                    reason = "(switch-off)";
                }
                else if (ext.injectedHediff != null
                    && pawn.health.hediffSet.GetFirstHediffOfDef(ext.injectedHediff) != null)
                {
                    reason = "(hediff-active)"; // R0全局规则: 主hediff已存在则跳过该针剂全部检测
                }
                else if (EL_StimAutoUseTracker.IsAnyCategoryCooling(pawn, ext.autoUseCategories))
                {
                    reason = "(cooldown)";
                }
                else
                {
                    // 条件组: 任一组全部满足即可
                    reason = "(cond-fail)";
                    for (int g = 0; g < ext.conditionGroups.Count; g++)
                    {
                        var conds = ext.conditionGroups[g]?.conditions;
                        if (conds == null || conds.Count == 0) continue;
                        bool all = true;
                        for (int c = 0; c < conds.Count; c++)
                        {
                            if (!conds[c].Satisfied(pawn))
                            {
                                all = false;
                                break;
                            }
                        }
                        if (all)
                        {
                            reason = null;
                            break;
                        }
                    }
                }
                if (reason == null)
                {
                    if (ext.autoUsePriority > bestPrio)
                    {
                        best = innerContainer[i];
                        bestPrio = ext.autoUsePriority;
                    }
                    reason = "(pass)";
                }
                if (sb != null)
                {
                    sb.Append(innerContainer[i].def.defName).Append(reason).Append(' ');
                }
            }
            if (best == null)
            {
                DebugLog(pawn, "policy=" + policy.id + " no candidate: " + (sb?.ToString() ?? ""));
                return null;
            }
            DebugLog(pawn, "policy=" + policy.id + " selected " + best.def.defName
                + " prio=" + bestPrio + ": " + (sb?.ToString() ?? ""));
            return JobMaker.MakeJob(InjectJob, best);
        }
    }

    /// <summary>
    /// 征召自动注射Tick: 征召待机pawn不扫描思考树(永续Wait_Combat + CanPawnTakeOpportunisticJob禁用),
    /// 每150tick扫描玩家阵营的征召空闲pawn, 命中即经TryTakeOrderedJob下发注射job;
    /// 注射job结束后由思考树自然回到征召待机(冷却期内本类不再命中, 无循环)
    /// </summary>
    public class EL_AutoInjectTicker : GameComponent
    {
        private const int ScanIntervalTicks = 150;

        public EL_AutoInjectTicker()
        {
        }

        public EL_AutoInjectTicker(Game game)
        {
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            if (Find.TickManager.TicksGame % ScanIntervalTicks != 0) return;
            List<Map> maps = Current.Game?.Maps;
            if (maps == null) return;
            for (int m = 0; m < maps.Count; m++)
            {
                var pawns = maps[m].mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn pawn = pawns[i];
                    if (!EL_AutoInject.IsDraftedIdle(pawn)) continue;
                    Job job = EL_AutoInject.TryFindJob(pawn);
                    if (job != null)
                    {
                        pawn.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 分类冷却条目(IExposable供嵌套持久化)
    /// </summary>
    public class EL_CategoryCooldown : IExposable
    {
        public string category;

        public int untilTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref category, "category");
            Scribe_Values.Look(ref untilTick, "untilTick");
        }
    }

    /// <summary>
    /// 分类冷却追踪: pawn.thingIDNumber -> (分类 -> 冷却截止tick)
    /// 注射结算时由EL_StimUtility.Inject/InjectToTarget调用NotifyInjected写入
    /// </summary>
    public class EL_StimAutoUseTracker : GameComponent
    {
        private Dictionary<int, List<EL_CategoryCooldown>> cooldowns
            = new Dictionary<int, List<EL_CategoryCooldown>>();

        public EL_StimAutoUseTracker()
        {
        }

        public EL_StimAutoUseTracker(Game game)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref cooldowns, "cooldowns", LookMode.Value, LookMode.Deep);
            cooldowns ??= new Dictionary<int, List<EL_CategoryCooldown>>();
        }

        public static bool IsAnyCategoryCooling(Pawn pawn, List<string> categories)
        {
            var tracker = Current.Game?.GetComponent<EL_StimAutoUseTracker>();
            if (tracker == null
                || !tracker.cooldowns.TryGetValue(pawn.thingIDNumber, out var list))
            {
                return false;
            }
            int now = Find.TickManager.TicksGame;
            for (int i = 0; i < list.Count; i++)
            {
                if (categories.Contains(list[i].category) && now < list[i].untilTick)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 注射结算: 对针剂全部分类写入冷却 = 主hediff效果时长(区间均值)/3
        /// hediff无Disappears comp(永久效果)时不写冷却, 由R0规则(hediff在身)天然防重复
        /// </summary>
        public static void NotifyInjected(Pawn pawn, ThingDef stimDef)
        {
            if (pawn == null || stimDef == null) return;
            var ext = stimDef.GetModExtension<EL_DefModExtension_StimAutoUse>();
            if (ext?.autoUseCategories == null || ext.autoUseCategories.Count == 0) return;

            float durationTicks = 0f;
            var disappearProps = ext.injectedHediff?.CompProps<HediffCompProperties_Disappears>();
            if (disappearProps != null)
            {
                // 1.6/1.7通用: IntRange字段disappearsAfterTicks
                var range = disappearProps.disappearsAfterTicks;
                durationTicks = (range.min + range.max) * 0.5f;
            }
            if (durationTicks <= 0f) return; // 永久/无时长hediff: 不设分类冷却

            int until = Find.TickManager.TicksGame + (int)(durationTicks / 3f);
            var tracker = Current.Game?.GetComponent<EL_StimAutoUseTracker>();
            if (tracker == null) return;
            if (!tracker.cooldowns.TryGetValue(pawn.thingIDNumber, out var list))
            {
                tracker.cooldowns[pawn.thingIDNumber] = list = new List<EL_CategoryCooldown>();
            }
            for (int i = 0; i < ext.autoUseCategories.Count; i++)
            {
                string cat = ext.autoUseCategories[i];
                var entry = list.Find(e => e.category == cat);
                if (entry == null)
                {
                    list.Add(new EL_CategoryCooldown { category = cat, untilTick = until });
                }
                else
                {
                    entry.untilTick = until;
                }
            }
        }
    }
}
