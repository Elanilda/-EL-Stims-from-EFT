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
    // 自定义HediffClass: 标记需保持首字母小写的Hediff
    // 用于xTG-12/eTG-change等首字母需保持小写的针剂Hediff
    // Hediff.LabelCap非virtual, 无法直接override, 故仅作类型标记
    // 实际去大写由 EL_Patch_Hediff_LabelCap 的Harmony Postfix完成
    // ============================================================
    public class EL_Hediff_RawLabel : HediffWithComps
    {
    }

    // ============================================================
    // Harmony Postfix: 覆盖Hediff.LabelCap getter的返回值
    // 当实例为EL_Hediff_RawLabel时, 返回未大写的Label
    // 使xTG-12/eTG-change在UI中保持首字母小写
    // ============================================================
    [HarmonyPatch(typeof(Hediff), nameof(Hediff.LabelCap), MethodType.Getter)]
    public static class EL_Patch_Hediff_LabelCap
    {
        [HarmonyPostfix]
        public static void Postfix(Hediff __instance, ref string __result)
        {
            if (__instance is EL_Hediff_RawLabel && __result != null)
            {
                __result = __instance.Label;
            }
        }
    }

    // ============================================================
    // 使用效果Comp的Properties: 指定要施加的HediffDef和饮食消耗比例
    // ============================================================
    public class EL_CompProperties_UseEffect_ApplyHediff : CompProperties_UseEffect
    {
        /// <summary>
        /// 使用物品时要施加的HediffDef
        /// </summary>
        public HediffDef hediffDef;

        /// <summary>
        /// 使用时立刻扣除的饮食值比例（0~1，0.15=扣除15%）
        /// </summary>
        public float foodCostFactor = 0f;

        public EL_CompProperties_UseEffect_ApplyHediff()
        {
            compClass = typeof(EL_CompUseEffect_ApplyHediff);
        }
    }

    // ============================================================
    // 使用效果Comp: 对自身施加Hediff并扣除饮食值
    // 保留供"右键地上物品直接注射"使用, 统一菜单也复用ApplyStimEffect
    // ============================================================
    public class EL_CompUseEffect_ApplyHediff : CompUseEffect
    {
        public EL_CompProperties_UseEffect_ApplyHediff Props => (EL_CompProperties_UseEffect_ApplyHediff)props;

        /// <summary>
        /// 音效是否已播放(防止PrepareTick每tick重复播放)
        /// </summary>
        private bool _soundPlayed = false;

        public override void DoEffect(Pawn usedBy)
        {
            base.DoEffect(usedBy);
            ApplyStimEffect(usedBy);
            // 消耗1个物品
            if (parent != null && !parent.Destroyed)
            {
                if (parent.stackCount > 1)
                {
                    parent.SplitOff(1);
                }
                else
                {
                    parent.Destroy();
                }
            }
        }

        /// <summary>
        /// 在useDuration等待期间(warmup)每tick调用
        /// 首次调用时播放注射音效, 实现音效与动作同步开始
        /// </summary>
        public override void PrepareTick()
        {
            base.PrepareTick();
            if (!_soundPlayed && parent?.Map != null && parent.Map == Find.CurrentMap)
            {
                _soundPlayed = true;
                EL_StimUtility.IngestInjectSound?.PlayOneShot(SoundInfo.InMap(parent));
            }
        }

        /// <summary>
        /// 施加针剂效果：Hediff + 饮食消耗
        /// 供手动使用与统一菜单共用
        /// 重复注射同种针剂时, 重置已存在Hediff的severity和Disappears时间, 实现时间覆盖
        /// </summary>
        public void ApplyStimEffect(Pawn pawn)
        {
            if (pawn == null) return;

            // 施加Hediff
            if (Props.hediffDef != null)
            {
                // 检查是否已存在同def的Hediff
                Hediff existingHediff = pawn.health?.hediffSet?.GetFirstHediffOfDef(Props.hediffDef);
                if (existingHediff != null)
                {
                    // 方案B: 重置severity为initialSeverity, 覆盖剩余时间
                    existingHediff.Severity = Props.hediffDef.initialSeverity;

                    // 重置HediffComp_Disappears的计时, 确保持续时间从0开始
                    // ResetElapsedTicks()将ticksToDisappear重置为disappearsAfterTicks
                    var disappearsComp = existingHediff.TryGetComp<HediffComp_Disappears>();
                    if (disappearsComp != null)
                    {
                        disappearsComp.ResetElapsedTicks();
                    }
                }
                else
                {
                    // 不存在则添加新的
                    Hediff hediff = HediffMaker.MakeHediff(Props.hediffDef, pawn);
                    hediff.Severity = Props.hediffDef.initialSeverity;
                    pawn.health.AddHediff(hediff);
                }
            }

            // 立刻扣除饮食值
            if (Props.foodCostFactor > 0f && pawn.needs?.food != null)
            {
                pawn.needs.food.CurLevel -= Props.foodCostFactor;
            }
        }
    }

    // ============================================================
    // CompProperties: EFT针剂空标记类
    // 仅作为ThingDef的标识, 供GetCarriedStims识别可注射针剂
    // 后续可扩展分类、冷却等元数据
    // ============================================================
    public class EL_CompProperties_EFTStims : CompProperties
    {
        public EL_CompProperties_EFTStims()
        {
            compClass = typeof(EL_Comp_EFTStims);
        }
    }

    // ============================================================
    // Comp: EFT针剂标记Comp实例, 无逻辑仅承载类型识别
    // ============================================================
    public class EL_Comp_EFTStims : ThingComp
    {
    }

    // ============================================================
    // 自定义Gizmo: 继承Command_Action(基类本身不绘制百分比文本)
    // 冷却中: 设置disabled禁用点击 + 在图标上绘制纯进度条覆盖
    // 进度条由EL_StimUtility.GetCooldownPercent提供0~1数值
    // ============================================================
    public class EL_Command_InjectStim : Command_Action
    {
        /// <summary>
        /// 冷却进度回调, 返回0~1, 1=刚注射满进度, 0=冷却结束
        /// </summary>
        public System.Func<float> cooldownPercentGetter;

        /// <summary>
        /// 设置gizmo禁用状态(disabled为protected, 需通过继承类暴露public访问)
        /// </summary>
        public void SetDisabled()
        {
            disabled = true;
        }

        public override GizmoResult GizmoOnGUI(Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
        {
            // 调用基类绘制完整gizmo外观(图标/边框/label等), 基类Command_Action不绘制百分比文本
            GizmoResult result = base.GizmoOnGUI(topLeft, maxWidth, parms);

            // 冷却中: 在图标上绘制纯进度条(无文本)
            if (cooldownPercentGetter != null)
            {
                float percent = cooldownPercentGetter();
                if (percent > 0.001f)
                {
                    // 进度条从下向上消失: percent=1满条覆盖整个图标, percent=0无覆盖
                    // 顶部(1-percent)为半透明黑表示"已冷却消耗", 底部percent为透明表示"剩余可用"
                    GUI.color = new Color(0f, 0f, 0f, 0.55f);
                    Rect progressRect = new Rect(topLeft.x, topLeft.y, 75f, 75f * (1f - percent));
                    GUI.DrawTexture(progressRect, BaseContent.BlackTex);
                    GUI.color = Color.white;
                }
            }

            return result;
        }
    }

    // ============================================================
    // 自定义手术Worker: 消耗针剂原料并对目标pawn施加Hediff+扣饮食
    // 原料消耗由手术系统自动处理, 这里只负责施加效果
    // 复用针剂的EL_CompUseEffect_ApplyHediff.ApplyStimEffect
    // ============================================================
    public class EL_Recipe_InjectStim : Recipe_Surgery
    {
        public override void ApplyOnPawn(Pawn pawn, BodyPartRecord part, Pawn billDoer,
                                         List<Thing> ingredients, Bill bill)
        {
            // surgeryOutcomeEffect=null时基类无副作用, 这里手动施加针剂效果

            // 从ingredients中找带EL_Comp_EFTStims标记的针剂
            Thing stimThing = ingredients?.FirstOrDefault(t => t.TryGetComp<EL_Comp_EFTStims>() != null);
            if (stimThing == null) return;

            // 复用针剂的ApplyHediff comp: 对目标pawn施加Hediff+扣饮食
            var comp = stimThing.TryGetComp<EL_CompUseEffect_ApplyHediff>();
            if (comp != null)
            {
                comp.ApplyStimEffect(pawn);
            }
        }
    }

    // ============================================================
    // JobDefOf: 注册自定义JobDef
    // 通过DefDatabase.GetNamed在静态构造器中查找, 避免硬编码引用
    // ============================================================
    [StaticConstructorOnStartup]
    public static class EL_JobDefOf
    {
        public static JobDef InjectStim;
        public static JobDef InjectStimSelf;

        static EL_JobDefOf()
        {
            InjectStim = DefDatabase<JobDef>.GetNamed("EL_Job_InjectStim");
            InjectStimSelf = DefDatabase<JobDef>.GetNamed("EL_Job_InjectStimSelf");
        }
    }

    // ============================================================
    // 自定义JobDriver: 走向目标pawn → 双方等待2秒 → 执行注射
    // 参考JobDriver_TendPatient: Toils_Goto + Toils_General.WaitWith(maintainPosture)
    // WaitWith让目标pawn进入"保持姿势"等待状态, 显示"正在接受注射"
    // ============================================================
    public class EL_JobDriver_InjectStim : JobDriver
    {
        // 注射等待时长(2秒 = 120 ticks), 体现注射动作耗时
        private const int InjectionDurationTicks = 120;

        // 目标pawn(targetA)
        private Pawn Target => job.targetA.Pawn;
        // 针剂物品实例(targetB), 从使用者inventory取出
        private Thing StimThing => job.targetB.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // 预订目标pawn(防止其他pawn同时操作)
            return pawn.Reserve(Target, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // 失败条件: 目标消失/禁止/死亡/非殖民者
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => Target == null || Target.Dead || Target.Faction != Faction.OfPlayer);
            // 失败条件: 针剂物品失效
            this.FailOn(() => StimThing == null || StimThing.Destroyed);

            // 1. 走向目标(卧床pawn用InteractionCell, 站立pawn用ClosestTouch)
            PathEndMode pem = Target.InBed() ? PathEndMode.InteractionCell : PathEndMode.ClosestTouch;
            yield return Toils_Goto.GotoThing(TargetIndex.A, pem);

            // 2. 双方等待2秒, 目标保持姿势(进入"正在接受注射"状态)
            Toil wait = Toils_General.WaitWith(TargetIndex.A, InjectionDurationTicks,
                useProgressBar: false, maintainPosture: true);
            // 等待结束后执行注射
            wait.AddFinishAction(() =>
            {
                if (Target != null && !Target.Dead
                    && StimThing != null && !StimThing.Destroyed)
                {
                    EL_StimUtility.InjectToTarget(pawn, Target, StimThing.def);
                }
            });
            // 使用者持续面向目标
            wait.tickIntervalAction = (delta) =>
            {
                if (Target != null) pawn.rotationTracker.FaceTarget(Target);
            };
            // 显示进度条
            wait.WithProgressBarToilDelay(TargetIndex.A);
            // 注射音效
            wait.PlaySustainerOrSound(EL_StimUtility.IngestInjectSound);
            yield return wait;
        }
    }

    // ============================================================
    // 自用注射JobDriver: 原地等待2秒 → 执行注射
    // 点击"使用针剂"gizmo后分配, 无需走向目标
    // 等待期间播放Ingest_Inject音效, 完成后调用EL_StimUtility.Inject
    // ============================================================
    public class EL_JobDriver_InjectStimSelf : JobDriver
    {
        // 注射等待时长(2秒 = 120 ticks), 与EL_JobDriver_InjectStim保持一致
        private const int InjectionDurationTicks = 120;

        // 针剂物品实例(targetA)
        private Thing StimThing => job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // 失败条件: 针剂物品失效
            this.FailOn(() => StimThing == null || StimThing.Destroyed);

            // 原地等待2秒, 播放Ingest_Inject音效, 显示进度条
            Toil wait = Toils_General.Wait(InjectionDurationTicks);
            wait.WithProgressBarToilDelay(TargetIndex.A);
            wait.PlaySustainerOrSound(EL_StimUtility.IngestInjectSound);
            // 等待结束后执行注射
            wait.AddFinishAction(() =>
            {
                if (StimThing != null && !StimThing.Destroyed)
                {
                    EL_StimUtility.Inject(pawn, StimThing.def);
                }
            });
            yield return wait;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class EL_Pawn_GetGizmos_Patch
    {
        public static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
        {
            // 约束: 仅玩家殖民者 + 征召状态 + 携带针剂
            if (!__instance.IsColonistPlayerControlled) return;
            if (!__instance.Drafted) return;

            var gizmo = EL_StimUtility.TryGetInjectionGizmo(__instance);
            if (gizmo == null) return;

            // 追加到原列表末尾
            var list = __result.ToList();
            list.Add(gizmo);
            __result = list;
        }
    }

    // ============================================================
    // FloatMenuOptionProvider: 右键其他pawn注射针剂
    // 替代旧Harmony patch, RimWorld 1.6标准扩展方式
    // 选中1个携带针剂的pawn右键其他殖民者时, 添加"为目标注射"选项
    // 无需携带者征召, 征召与非征召状态均可触发
    // 点击后弹出子菜单列出携带的针剂, 选择后分配注射job(走向+等待+执行)
    // ============================================================
    public class EL_FloatMenuOptionProvider_InjectStim : FloatMenuOptionProvider
    {
        // 征召与非征召状态均允许(为他人注射无需携带者征召)
        protected override bool Drafted => true;
        protected override bool Undrafted => true;
        // 不允许多选
        protected override bool Multiselect => false;
        // 需要操作能力
        protected override bool RequiresManipulation => true;
        // 不允许对自己注射
        protected override bool CanSelfTarget => false;

        public override IEnumerable<FloatMenuOption> GetOptionsFor(Pawn clickedPawn, FloatMenuContext context)
        {
            Pawn user = context.FirstSelectedPawn;

            // 仅殖民者目标(根据审批决策)
            if (clickedPawn.Faction != Faction.OfPlayer) yield break;
            if (clickedPawn.Dead) yield break;

            // 携带针剂检查
            var stims = EL_StimUtility.GetCarriedStims(user);
            if (stims.NullOrEmpty()) yield break;

            bool onCooldown = EL_StimUtility.IsOnCooldown(user);
            string targetName = clickedPawn.Name?.ToStringShort ?? clickedPawn.LabelDefinite();

            if (onCooldown)
            {
                // 冷却中显示禁用项
                var disabledOpt = new FloatMenuOption(
                    "EL_Stim_InjectTargetCooldown".Translate(targetName), null)
                {
                    Disabled = true,
                    revalidateClickTarget = clickedPawn
                };
                yield return disabledOpt;
                yield break;
            }

            // 主选项: 点击后弹子菜单
            var opt = new FloatMenuOption(
                "EL_Stim_InjectTarget".Translate(targetName),
                () =>
                {
                    var subOpts = new List<FloatMenuOption>();
                    foreach (Thing stim in stims)
                    {
                        ThingDef stimDef = stim.def;
                        int total = EL_StimUtility.CountCarried(user, stimDef);
                        // 闭包变量捕获
                        ThingDef capturedDef = stimDef;
                        subOpts.Add(new FloatMenuOption(
                            $"{stimDef.LabelCap}[{total}]",
                            () => GiveInjectJob(user, clickedPawn, capturedDef),
                            stimDef, null, false, MenuOptionPriority.Default,
                            null, null, 0f, null, null, true, 0));
                    }
                    Find.WindowStack.Add(new FloatMenu(subOpts));
                })
            {
                revalidateClickTarget = clickedPawn  // 目标移动时菜单自动失效重生成
            };
            yield return opt;
        }

        /// <summary>
        /// 分配注射job: 从user inventory找具体针剂实例, 创建job走向+等待+执行
        /// </summary>
        private static void GiveInjectJob(Pawn user, Pawn target, ThingDef stimDef)
        {
            // 从user inventory找具体的针剂Thing实例
            Thing stimThing = null;
            foreach (Thing t in user.inventory.innerContainer)
            {
                if (t.def == stimDef && !t.Destroyed && t.stackCount > 0)
                {
                    stimThing = t;
                    break;
                }
            }
            if (stimThing == null) return;

            // 创建并分配注射job: targetA=target, targetB=stimThing
            Job job = JobMaker.MakeJob(EL_JobDefOf.InjectStim, target, stimThing);
            user.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }
    }

    // ============================================================
    // Harmony Patch: ThingDef.AllRecipes getter 后缀
    // 对所有人形pawn的ThingDef动态追加注射手术recipe
    // 解决手术不在手术清单显示的问题, 兼容所有race mod(ElanSupporian等)
    // ============================================================
    [HarmonyPatch(typeof(ThingDef), nameof(ThingDef.AllRecipes), MethodType.Getter)]
    public static class EL_ThingDef_AllRecipes_Patch
    {
        private static List<RecipeDef> _stimSurgeryRecipes;
        private static bool _initialized = false;

        private static void InitRecipes()
        {
            if (_initialized) return;
            _initialized = true;
            _stimSurgeryRecipes = DefDatabase<RecipeDef>.AllDefs
                .Where(r => r.workerClass == typeof(EL_Recipe_InjectStim))
                .ToList();
        }

        public static void Postfix(ThingDef __instance, ref List<RecipeDef> __result)
        {
            // 仅对人形pawn的ThingDef
            if (__instance.category != ThingCategory.Pawn) return;
            if (__instance.race?.Humanlike != true) return;

            InitRecipes();
            if (_stimSurgeryRecipes == null || _stimSurgeryRecipes.Count == 0) return;

            // 追加手术recipe(避免重复)
            foreach (var r in _stimSurgeryRecipes)
            {
                if (!__result.Contains(r))
                {
                    __result.Add(r);
                }
            }
        }
    }
}
