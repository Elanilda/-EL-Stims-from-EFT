using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;
using RimWorld;
using HarmonyLib;

namespace RimWorld
{

    // ============================================================
    // HediffComp Properties: 随机恢复身体部位HP
    // ============================================================
    public class EL_HediffCompProperties_RandomBodyPartHeal : HediffCompProperties
    {
        public float healAmount = 1f;
        public int ticksInterval = 60;
        public bool onlyDamagedParts = true;

        /// <summary>
        /// 是否可治疗疤痕/永久伤(第三档=true, 第一/二档=false)
        /// </summary>
        public bool canHealPermanent = false;

        /// <summary>
        /// 是否绕过永久化检查(第二/三档=true直接改Severity, 第一档=false调用Heal())
        /// </summary>
        public bool bypassPermanentCheck = false;

        /// <summary>
        /// 是否包含solid部位(骨骼)治疗, 默认false
        /// true=全部位(Propital/eTG-change), false=仅非solid(Adrenaline/PNB/Perfotoran)
        /// </summary>
        public bool includeSolidParts = false;

        /// <summary>
        /// severity阶段列表(可选, 不填则全程生效)
        /// severity从1.0递减, 通过startSeverity/endSeverity划分阶段
        /// </summary>
        public List<EL_StageRandomBodyPartHeal> stages = new List<EL_StageRandomBodyPartHeal>();

        public EL_HediffCompProperties_RandomBodyPartHeal()
        {
            compClass = typeof(EL_HediffComp_RandomBodyPartHeal);
        }
    }

    /// <summary>
    /// RandomBodyPartHeal的阶段定义
    /// severity从1.0递减, startSeverity>=当前severity>endSeverity时该阶段生效
    /// </summary>
    public class EL_StageRandomBodyPartHeal
    {
        public float startSeverity = 1.0f;
        public float endSeverity = 0.0f;
    }

    // ============================================================
    // HediffComp: 每隔固定间隔随机恢复一个身体部位的HP
    // ============================================================
    public class EL_HediffComp_RandomBodyPartHeal : HediffComp
    {
        public EL_HediffCompProperties_RandomBodyPartHeal Props => (EL_HediffCompProperties_RandomBodyPartHeal)props;

        private int tickCounter = 0;

        public override void CompPostTick(ref float severityAdjustment)
        {
            base.CompPostTick(ref severityAdjustment);

            tickCounter++;
            if (tickCounter >= Props.ticksInterval)
            {
                tickCounter = 0;

                // 若配置了stages, 检查当前severity是否在任一阶段范围内
                if (Props.stages != null && Props.stages.Count > 0)
                {
                    float currentSeverity = parent.Severity;
                    bool inRange = false;
                    foreach (var stage in Props.stages)
                    {
                        // severity从1.0递减, startSeverity>=当前severity>endSeverity时生效
                        if (currentSeverity <= stage.startSeverity &&
                            currentSeverity > stage.endSeverity)
                        {
                            inRange = true;
                            break;
                        }
                    }
                    if (!inRange) return;
                }

                TryHealRandomBodyPart();
            }
        }

        private void TryHealRandomBodyPart()
        {
            if (Pawn == null || Pawn.health?.hediffSet == null) return;

            List<BodyPartRecord> candidateParts = new List<BodyPartRecord>();

            // 收集候选部位: 未缺失 + (按includeSolidParts过滤) + 有可治疗的伤
            foreach (BodyPartRecord part in Pawn.health.hediffSet.GetNotMissingParts())
            {
                if (part.def.hitPoints <= 0) continue;

                // includeSolidParts=false: 跳过solid部位(骨骼)
                // includeSolidParts=true: 全部位候选(Propital/eTG-change)
                if (!Props.includeSolidParts &&
                    part.def.IsSolid(part, Pawn.health.hediffSet.hediffs)) continue;

                if (Props.onlyDamagedParts)
                {
                    // 检查是否有可治疗的伤
                    bool hasHealableInjury = false;
                    foreach (Hediff hd in Pawn.health.hediffSet.hediffs)
                    {
                        if (hd is Hediff_Injury injury && hd.Part == part)
                        {
                            // canHealPermanent=true: 可治疤痕; false: 只治普通伤
                            if (Props.canHealPermanent || !injury.IsPermanent())
                            {
                                hasHealableInjury = true;
                                break;
                            }
                        }
                    }
                    if (!hasHealableInjury) continue;
                }

                candidateParts.Add(part);
            }

            if (candidateParts.Count == 0) return;

            BodyPartRecord selectedPart = candidateParts.RandomElement();

            // 在该部位找 Severity 最高的可治疗伤口
            Hediff_Injury targetInjury = null;
            foreach (Hediff hediff in Pawn.health.hediffSet.hediffs)
            {
                if (hediff is Hediff_Injury injury && hediff.Part == selectedPart)
                {
                    // canHealPermanent=false: 跳过永久伤(疤痕)
                    if (!Props.canHealPermanent && injury.IsPermanent()) continue;

                    if (targetInjury == null || injury.Severity > targetInjury.Severity)
                        targetInjury = injury;
                }
            }

            if (targetInjury == null) return;

            // 治疗: 根据 bypassPermanentCheck 决定调用 Heal() 还是直接改 Severity
            if (Props.bypassPermanentCheck)
            {
                // 第二/三档: 直接改 Severity, 不触发 CompPostInjuryHeal
                targetInjury.Severity = Mathf.Max(0f, targetInjury.Severity - Props.healAmount);
                Pawn.health.Notify_HediffChanged(targetInjury);
                if (targetInjury.Severity <= 0.001f)
                    Pawn.health.RemoveHediff(targetInjury);
            }
            else
            {
                // 第一档: 调用 Heal(), 触发 CompPostInjuryHeal(可能留疤)
                targetInjury.Heal(Props.healAmount);
                if (targetInjury.Severity <= 0.001f)
                    Pawn.health.RemoveHediff(targetInjury);
            }
        }

        public override string CompDescriptionExtra => "EL_Stim_CompHealDesc".Translate(Props.healAmount, Props.ticksInterval / 60f);
    }

    // ============================================================
    // 分阶段饮食消耗组件
    // 按hediff的severity阶段以不同速率消耗饮食值
    // 用于AHF1-M(全程120秒消耗0.36)和Zagustin(后40秒消耗0.56)
    // severity从1.0递减, 通过startSeverity/endSeverity划分阶段
    // ============================================================
    public class EL_HediffCompProperties_StageHungerDrain : HediffCompProperties
    {
        public List<EL_StageHungerDrain> stages = new List<EL_StageHungerDrain>();

        public EL_HediffCompProperties_StageHungerDrain()
        {
            compClass = typeof(EL_HediffComp_StageHungerDrain);
        }
    }

    public class EL_StageHungerDrain
    {
        public float startSeverity = 1.0f;
        public float endSeverity = 0.0f;
        public float hungerPerSecond = 0f;
        public string description = "";
    }

    public class EL_HediffComp_StageHungerDrain : HediffComp
    {
        private int _tickAccumulator = 0;
        private const int TicksPerSecond = 60;

        public EL_HediffCompProperties_StageHungerDrain Props =>
            (EL_HediffCompProperties_StageHungerDrain)props;

        public override void CompPostTick(ref float severityAdjustment)
        {
            // 每秒执行一次(60 ticks)
            if (++_tickAccumulator < TicksPerSecond) return;
            _tickAccumulator = 0;

            if (Pawn?.needs?.food == null || Pawn.Dead) return;

            float currentSeverity = parent.Severity;
            foreach (var stage in Props.stages)
            {
                // severity从1.0递减, 检查当前severity是否在该阶段范围内
                if (currentSeverity <= stage.startSeverity &&
                    currentSeverity > stage.endSeverity)
                {
                    Pawn.needs.food.CurLevel -= stage.hungerPerSecond;
                    break;
                }
            }
        }

        public override string CompDescriptionExtra
        {
            get
            {
                // 只在当前severity处于消耗阶段时显示对应文本
                float currentSeverity = parent?.Severity ?? 0f;
                foreach (var stage in Props.stages)
                {
                    if (currentSeverity <= stage.startSeverity &&
                        currentSeverity > stage.endSeverity)
                    {
                        return "\n" + stage.description;
                    }
                }
                return null;
            }
        }
    }

    // ============================================================
    // 分阶段休息恢复组件
    // 按hediff的severity阶段以不同速率恢复NeedDef Rest的CurLevel
    // 用于SJ6(前200秒每秒恢复0.02休息值, 共4.0)
    // severity从1.0递减, 通过startSeverity/endSeverity划分阶段
    // ============================================================
    public class EL_HediffCompProperties_StageRestGain : HediffCompProperties
    {
        public List<EL_StageRestGain> stages = new List<EL_StageRestGain>();

        public EL_HediffCompProperties_StageRestGain()
        {
            compClass = typeof(EL_HediffComp_StageRestGain);
        }
    }

    public class EL_StageRestGain
    {
        public float startSeverity = 1.0f;
        public float endSeverity = 0.0f;
        public float restPerSecond = 0f;
        public string description = "";
    }

    public class EL_HediffComp_StageRestGain : HediffComp
    {
        private int _tickAccumulator = 0;
        private const int TicksPerSecond = 60;

        public EL_HediffCompProperties_StageRestGain Props =>
            (EL_HediffCompProperties_StageRestGain)props;

        public override void CompPostTick(ref float severityAdjustment)
        {
            // 每秒执行一次(60 ticks)
            if (++_tickAccumulator < TicksPerSecond) return;
            _tickAccumulator = 0;

            if (Pawn?.needs?.rest == null || Pawn.Dead) return;

            float currentSeverity = parent.Severity;
            foreach (var stage in Props.stages)
            {
                // severity从1.0递减, 检查当前severity是否在该阶段范围内
                if (currentSeverity <= stage.startSeverity &&
                    currentSeverity > stage.endSeverity)
                {
                    // 直接增加Rest need的CurLevel, 限制在0~1范围
                    Pawn.needs.rest.CurLevel += stage.restPerSecond;
                    break;
                }
            }
        }

        public override string CompDescriptionExtra
        {
            get
            {
                float currentSeverity = parent?.Severity ?? 0f;
                foreach (var stage in Props.stages)
                {
                    if (currentSeverity <= stage.startSeverity &&
                        currentSeverity > stage.endSeverity)
                    {
                        return "\n" + stage.description;
                    }
                }
                return null;
            }
        }
    }

    // ============================================================
    // 分阶段饮食恢复组件
    // 按hediff的severity阶段以不同速率恢复饮食值
    // 用于SJ12(前600秒每秒恢复0.01饮食值, 共6.0)
    // severity从1.0递减, 通过startSeverity/endSeverity划分阶段
    // ============================================================
    public class EL_HediffCompProperties_StageHungerGain : HediffCompProperties
    {
        public List<EL_StageHungerGain> stages = new List<EL_StageHungerGain>();

        public EL_HediffCompProperties_StageHungerGain()
        {
            compClass = typeof(EL_HediffComp_StageHungerGain);
        }
    }

    public class EL_StageHungerGain
    {
        public float startSeverity = 1.0f;
        public float endSeverity = 0.0f;
        public float hungerPerSecond = 0f;
        public string description = "";
    }

    public class EL_HediffComp_StageHungerGain : HediffComp
    {
        private int _tickAccumulator = 0;
        private const int TicksPerSecond = 60;

        public EL_HediffCompProperties_StageHungerGain Props =>
            (EL_HediffCompProperties_StageHungerGain)props;

        public override void CompPostTick(ref float severityAdjustment)
        {
            if (++_tickAccumulator < TicksPerSecond) return;
            _tickAccumulator = 0;

            if (Pawn?.needs?.food == null || Pawn.Dead) return;

            float currentSeverity = parent.Severity;
            foreach (var stage in Props.stages)
            {
                if (currentSeverity <= stage.startSeverity &&
                    currentSeverity > stage.endSeverity)
                {
                    Pawn.needs.food.CurLevel += stage.hungerPerSecond;
                    break;
                }
            }
        }

        public override string CompDescriptionExtra
        {
            get
            {
                float currentSeverity = parent?.Severity ?? 0f;
                foreach (var stage in Props.stages)
                {
                    if (currentSeverity <= stage.startSeverity &&
                        currentSeverity > stage.endSeverity)
                    {
                        return "\n" + stage.description;
                    }
                }
                return null;
            }
        }
    }

    // ============================================================
    // 分阶段休息消耗组件
    // 按hediff的severity阶段以不同速率消耗NeedDef Rest的CurLevel
    // 用于P22(后65秒每秒消耗0.008休息值, 共0.52)
    // severity从1.0递减, 通过startSeverity/endSeverity划分阶段
    // ============================================================
    public class EL_HediffCompProperties_StageRestDrain : HediffCompProperties
    {
        public List<EL_StageRestDrain> stages = new List<EL_StageRestDrain>();

        public EL_HediffCompProperties_StageRestDrain()
        {
            compClass = typeof(EL_HediffComp_StageRestDrain);
        }
    }

    public class EL_StageRestDrain
    {
        public float startSeverity = 1.0f;
        public float endSeverity = 0.0f;
        public float restPerSecond = 0f;
        public string description = "";
    }

    public class EL_HediffComp_StageRestDrain : HediffComp
    {
        private int _tickAccumulator = 0;
        private const int TicksPerSecond = 60;

        public EL_HediffCompProperties_StageRestDrain Props =>
            (EL_HediffCompProperties_StageRestDrain)props;

        public override void CompPostTick(ref float severityAdjustment)
        {
            if (++_tickAccumulator < TicksPerSecond) return;
            _tickAccumulator = 0;

            if (Pawn?.needs?.rest == null || Pawn.Dead) return;

            float currentSeverity = parent.Severity;
            foreach (var stage in Props.stages)
            {
                if (currentSeverity <= stage.startSeverity &&
                    currentSeverity > stage.endSeverity)
                {
                    Pawn.needs.rest.CurLevel -= stage.restPerSecond;
                    break;
                }
            }
        }

        public override string CompDescriptionExtra
        {
            get
            {
                float currentSeverity = parent?.Severity ?? 0f;
                foreach (var stage in Props.stages)
                {
                    if (currentSeverity <= stage.startSeverity &&
                        currentSeverity > stage.endSeverity)
                    {
                        return "\n" + stage.description;
                    }
                }
                return null;
            }
        }
    }

    // ============================================================
    // 随机部位损伤Comp的Properties: 每N tick对随机部位造成损伤
    // 用于M.U.L.E(每10秒扣1点)和SJ9(每10秒扣1点)
    // 复用EL_Injury_MetabolicDamage HediffDef, 通过HediffMaker创建Hediff_Injury
    // ============================================================
    public class EL_HediffCompProperties_RandomBodyPartDamage : HediffCompProperties
    {
        /// <summary>
        /// 损伤HediffDef(EL_Injury_MetabolicDamage)
        /// </summary>
        public HediffDef damageHediff;

        /// <summary>
        /// 每次损伤量(默认1)
        /// </summary>
        public float damageAmount = 1f;

        /// <summary>
        /// 损伤间隔ticks(默认600=10秒)
        /// </summary>
        public int ticksInterval = 600;

        /// <summary>
        /// 是否忽略currentHP>damageAmount限制, 允许部位耐久归零
        /// true=允许归零(Obd2), false=保留归零保护(M.U.L.E/SJ9)
        /// </summary>
        public bool ignoreHPLimit = false;

        public EL_HediffCompProperties_RandomBodyPartDamage()
        {
            compClass = typeof(EL_HediffComp_RandomBodyPartDamage);
        }
    }

    // ============================================================
    // 随机部位损伤Comp: 对随机非solid部位造成损伤, 避免归零重选
    // ============================================================
    public class EL_HediffComp_RandomBodyPartDamage : HediffComp
    {
        public EL_HediffCompProperties_RandomBodyPartDamage Props => (EL_HediffCompProperties_RandomBodyPartDamage)props;

        private int tickCounter = 0;

        public override void CompPostTick(ref float severityAdjustment)
        {
            tickCounter++;
            if (tickCounter < Props.ticksInterval) return;
            tickCounter = 0;

            if (Pawn == null || Pawn.health?.hediffSet == null) return;
            if (Props.damageHediff == null) return;

            // 收集候选部位: 未缺失 + 非Solid + 非conceptual挂件 + (可选)HP>damageAmount避免归零
            List<BodyPartRecord> candidateParts = new List<BodyPartRecord>();
            foreach (BodyPartRecord part in Pawn.health.hediffSet.GetNotMissingParts())
            {
                if (part.def.hitPoints <= 0) continue;

                // 非Solid部位(solid骨骼不损伤)
                if (part.def.IsSolid(part, Pawn.health.hediffSet.hediffs)) continue;

                // 排除conceptual挂件部位(如Waist), 否则Hediff_Injury.PostAdd会报错
                // 参照原版 HealthUtility.GiveRandomSurgeryInjuries 的过滤方式
                if (part.def.conceptual) continue;

                // 排除手指脚趾(数量众多, 损伤会撑爆健康列表)
                // Finger含ManipulationLimbDigit tag, Toe含MovingLimbDigit tag
                if (part.def.tags.Contains(BodyPartTagDefOf.ManipulationLimbDigit) ||
                    part.def.tags.Contains(BodyPartTagDefOf.MovingLimbDigit)) continue;

                // 当前HP限制: ignoreHPLimit=false时保留归零保护(M.U.L.E/SJ9)
                //              ignoreHPLimit=true时跳过此检查, 允许部位耐久归零(Obd2)
                if (!Props.ignoreHPLimit)
                {
                    float currentHP = Pawn.health.hediffSet.GetPartHealth(part);
                    if (currentHP <= Props.damageAmount) continue;
                }

                candidateParts.Add(part);
            }

            if (candidateParts.Count == 0) return;

            // 随机选一个部位
            BodyPartRecord selectedPart = candidateParts.RandomElement();

            // 创建Hediff_Injury实例
            Hediff_Injury injury = (Hediff_Injury)HediffMaker.MakeHediff(Props.damageHediff, Pawn, selectedPart);
            injury.Severity = Props.damageAmount;
            Pawn.health.AddHediff(injury);
        }

        public override void CompExposeData()
        {
            Scribe_Values.Look(ref tickCounter, "tickCounter");
        }
    }

    // ============================================================
    // 随机部位损伤组件: EL_HediffComp_RandomBodyPartDamage 结束
    // ============================================================


    // ============================================================
    // Obdolbos鸡尾酒随机效果选择器
    // 施加主Hediff时, 对配置的15条子Hediff每条独立按chance(默认25%)判定
    //   激活的子Hediff通过HediffMaker.MakeHediff施加到Pawn
    //   子Hediff自带计时(Disappears)与自身效果, 主Hediff仅做计时与调度
    // 主Hediff移除时, 遍历清除所有可能残留的子Hediff(含Pawn手动移除子Hediff的情况)
    // 注: 子Hediff独立计时, 可能比主Hediff(600秒)先消失, 这是预期行为
    // ============================================================
    public class EL_HediffCompProperties_ObdolbosEffectPicker : HediffCompProperties
    {
        /// <summary>
        /// 候选子Hediff列表(对应15条效果)
        /// </summary>
        public List<HediffDef> effectHediffs = new List<HediffDef>();

        /// <summary>
        /// 每条效果独立激活几率(默认0.25=25%)
        /// </summary>
        public float chance = 0.25f;

        public EL_HediffCompProperties_ObdolbosEffectPicker()
        {
            compClass = typeof(EL_HediffComp_ObdolbosEffectPicker);
        }
    }

    public class EL_HediffComp_ObdolbosEffectPicker : HediffComp
    {
        public EL_HediffCompProperties_ObdolbosEffectPicker Props =>
            (EL_HediffCompProperties_ObdolbosEffectPicker)props;

        public override void CompPostPostAdd(DamageInfo? dinfo)
        {
            base.CompPostPostAdd(dinfo);
            if (Pawn == null || Pawn.health?.hediffSet == null) return;
            if (Props.effectHediffs == null || Props.effectHediffs.Count == 0) return;

            // 对每条子Hediff独立按chance判定
            foreach (HediffDef effectDef in Props.effectHediffs)
            {
                if (effectDef == null) continue;

                // 25%几率激活
                if (Rand.Value < Props.chance)
                {
                    // 已存在同def则跳过(防止重复注射时叠加)
                    Hediff existing = Pawn.health.hediffSet.GetFirstHediffOfDef(effectDef);
                    if (existing != null)
                    {
                        // 重置计时(覆盖)
                        var disappearsComp = existing.TryGetComp<HediffComp_Disappears>();
                        if (disappearsComp != null) disappearsComp.ResetElapsedTicks();
                        continue;
                    }

                    Hediff subHediff = HediffMaker.MakeHediff(effectDef, Pawn);
                    subHediff.Severity = effectDef.initialSeverity;
                    Pawn.health.AddHediff(subHediff);
                }
            }
        }

        public override void CompPostPostRemoved()
        {
            base.CompPostPostRemoved();
            if (Pawn == null || Pawn.health?.hediffSet == null) return;
            if (Props.effectHediffs == null) return;

            // 主Hediff移除时清除所有可能残留的子Hediff
            // 子Hediff可能已自行消失, GetFirstHediffOfDef返回null时跳过
            foreach (HediffDef effectDef in Props.effectHediffs)
            {
                if (effectDef == null) continue;
                Hediff subHediff = Pawn.health.hediffSet.GetFirstHediffOfDef(effectDef);
                if (subHediff != null)
                {
                    Pawn.health.RemoveHediff(subHediff);
                }
            }
        }
    }

    // ============================================================
    // Obdolbos随机效果选择器: EL_HediffComp_ObdolbosEffectPicker 结束
    // ============================================================


    // ============================================================
    // 通用多Hediff清除组件: EL_HediffCompProperties_ClearHediffs
    // 用于xTG-12解毒剂与Perfotoran人造血等需清除负面Hediff的针剂
    // 支持XML配置可清除的HediffDef列表, 施加时立即清除,
    // 并在immunityDurationTicks期间持续检测(每checkIntervalTicks检测一次)
    // 以清除新添加的目标Hediff
    // ============================================================
    public class EL_HediffCompProperties_ClearHediffs : HediffCompProperties
    {
        // 可清除的HediffDef列表, 由XML配置
        public List<HediffDef> hediffDefs;

        // 免疫持续时间(ticks), 默认240秒=14400 ticks
        public int immunityDurationTicks = 14400;

        // 检测间隔(ticks), 默认60tick检测一次以减小性能压力
        public int checkIntervalTicks = 60;

        public EL_HediffCompProperties_ClearHediffs()
        {
            compClass = typeof(EL_HediffComp_ClearHediffs);
        }
    }

    public class EL_HediffComp_ClearHediffs : HediffComp
    {
        public EL_HediffCompProperties_ClearHediffs Props => (EL_HediffCompProperties_ClearHediffs)props;

        // 剩余免疫时间
        private int remainingTicks;

        // 检测计数器
        private int tickCounter;

        public override void CompPostMake()
        {
            // 施加时立即清除现有目标Hediff
            ClearTargetHediffsNow();
            remainingTicks = Props.immunityDurationTicks;
        }

        public override void CompPostTick(ref float severityAdjustment)
        {
            // 免疫期已结束, 不再检测
            if (remainingTicks <= 0) return;

            remainingTicks--;
            tickCounter++;

            // 每 checkIntervalTicks 检测一次, 减小性能压力
            if (tickCounter < Props.checkIntervalTicks) return;

            tickCounter = 0;
            ClearTargetHediffsNow();
        }

        // 清除Pawn身上所有匹配的目标Hediff
        private void ClearTargetHediffsNow()
        {
            // 无配置或Pawn无效则跳过
            if (Props.hediffDefs == null || Props.hediffDefs.Count == 0) return;
            if (Pawn == null || Pawn.health?.hediffSet == null) return;

            var hediffs = Pawn.health.hediffSet.hediffs;
            // 逆序遍历, 移除时避免索引越界
            for (int i = hediffs.Count - 1; i >= 0; i--)
            {
                if (Props.hediffDefs.Contains(hediffs[i].def))
                {
                    Pawn.health.RemoveHediff(hediffs[i]);
                }
            }
        }

        public override void CompExposeData()
        {
            // 存档兼容: 保存剩余时间与计数器
            Scribe_Values.Look(ref remainingTicks, "remainingTicks");
            Scribe_Values.Look(ref tickCounter, "tickCounter");
        }
    }

    // ============================================================
    // 通用多Hediff清除组件: EL_HediffComp_ClearHediffs 结束
    // ============================================================


    // ============================================================
    // BloodLoss Severity 递减组件: EL_HediffCompProperties_ReduceBloodLoss
    // 用于Perfotoran人造血等需持续减少失血量的针剂
    // 每 checkIntervalTicks 检测一次, 直接减少 BloodLoss Hediff 的 Severity
    // 注意: 直接修改Severity绕过自然恢复机制, 体现人造血替代血浆功能
    // ============================================================
    public class EL_HediffCompProperties_ReduceBloodLoss : HediffCompProperties
    {
        // 每秒减少的BloodLoss Severity值, 默认0.015
        public float reductionPerSecond = 0.015f;

        // 持续时间(ticks), 默认120秒=7200 ticks
        public int durationTicks = 7200;

        // 检测间隔(ticks), 默认60tick检测一次以减小性能压力
        public int checkIntervalTicks = 60;

        public EL_HediffCompProperties_ReduceBloodLoss()
        {
            compClass = typeof(EL_HediffComp_ReduceBloodLoss);
        }
    }

    public class EL_HediffComp_ReduceBloodLoss : HediffComp
    {
        public EL_HediffCompProperties_ReduceBloodLoss Props => (EL_HediffCompProperties_ReduceBloodLoss)props;

        // 剩余持续时间
        private int remainingTicks;

        // 检测计数器
        private int tickCounter;

        public override void CompPostMake()
        {
            remainingTicks = Props.durationTicks;
        }

        public override void CompPostTick(ref float severityAdjustment)
        {
            // 持续期已结束, 不再减少
            if (remainingTicks <= 0) return;

            remainingTicks--;
            tickCounter++;

            // 每 checkIntervalTicks 检测一次, 减小性能压力
            if (tickCounter < Props.checkIntervalTicks) return;

            tickCounter = 0;

            // Pawn无效则跳过
            if (Pawn == null || Pawn.health?.hediffSet == null) return;

            // 获取BloodLoss Hediff实例
            Hediff bloodLoss = Pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.BloodLoss);
            if (bloodLoss == null) return;

            // 计算本次检测应减少的Severity(按秒数累计)
            // checkIntervalTicks=60即1秒, reductionPerSecond即每次减少值
            // 若checkIntervalTicks非60则按比例换算
            float reduction = Props.reductionPerSecond * (Props.checkIntervalTicks / 60f);
            bloodLoss.Severity = Mathf.Max(0f, bloodLoss.Severity - reduction);
        }

        public override void CompExposeData()
        {
            // 存档兼容: 保存剩余时间与计数器
            Scribe_Values.Look(ref remainingTicks, "remainingTicks");
            Scribe_Values.Look(ref tickCounter, "tickCounter");
        }
    }
}
