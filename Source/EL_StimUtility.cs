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
    // 工具类: 识别pawn携带的针剂并执行注射
    // 需StaticConstructorOnStartup特性: 因含Texture2D字段, 必须在主线程加载
    // ============================================================
    [StaticConstructorOnStartup]
    public static class EL_StimUtility
    {
        /// <summary>
        /// 统一菜单图标缓存
        /// </summary>
        private static Texture2D _injectionIcon;

        /// <summary>
        /// 注射音效缓存(Ingest_Inject是原版SoundDef, 不在SoundDefOf中, 需运行时查找)
        /// </summary>
        private static SoundDef _ingestInjectSound;

        /// <summary>
        /// 注射冷却时间(2秒 = 120 ticks), 体现注射动作消耗的时间
        /// </summary>
        public const int InjectionCooldownTicks = 120;

        /// <summary>
        /// 获取注射音效(EL_Stim_Inject, 自定义SoundDef), 首次访问时缓存
        /// 音频文件需放在 Sounds/EL_Stim_Inject.wav
        /// </summary>
        public static SoundDef IngestInjectSound
        {
            get
            {
                if (_ingestInjectSound == null)
                {
                    _ingestInjectSound = DefDatabase<SoundDef>.GetNamed("EL_Stim_Inject");
                }
                return _ingestInjectSound;
            }
        }

        /// <summary>
        /// 每个pawn的冷却到期tick, key=pawn.ID, value=Find.TickManager.TicksGame到此值前不可用
        /// 因gizmo每次GetGizmos都重新创建, 冷却状态必须存到静态字典
        /// </summary>
        private static readonly Dictionary<int, int> _cooldownUntil = new Dictionary<int, int>();

        /// <summary>
        /// 统一菜单图标, 加载Textures/UI/Abilities/EL_Ability_Injection.png
        /// </summary>
        public static Texture2D InjectionIcon
        {
            get
            {
                if (_injectionIcon == null)
                {
                    _injectionIcon = ContentFinder<Texture2D>.Get("UI/Abilities/EL_Ability_Injection", true);
                }
                return _injectionIcon;
            }
        }

        /// <summary>
        /// 识别: pawn inventory中带有EL_CompProperties_EFTStims标记的物品即为针剂
        /// 合并相同def以减少菜单项, 同def只显示一行并标注总数
        /// </summary>
        public static List<Thing> GetCarriedStims(Pawn pawn)
        {
            var list = new List<Thing>();
            if (pawn?.inventory == null) return list;

            // 用def去重, 保留首个实例代表该类
            var seen = new HashSet<ThingDef>();
            foreach (Thing t in pawn.inventory.innerContainer)
            {
                if (t.Destroyed) continue;
                if (t.TryGetComp<EL_Comp_EFTStims>() == null) continue;
                if (seen.Add(t.def))
                {
                    list.Add(t);
                }
            }
            return list;
        }

        /// <summary>
        /// 统计pawn携带的指定def针剂总数
        /// </summary>
        public static int CountCarried(Pawn pawn, ThingDef def)
        {
            int total = 0;
            if (pawn?.inventory == null) return 0;
            foreach (Thing t in pawn.inventory.innerContainer)
            {
                if (t.def == def && !t.Destroyed)
                {
                    total += t.stackCount;
                }
            }
            return total;
        }

        /// <summary>
        /// 注射: 复用针剂物品上的EL_CompUseEffect_ApplyHediff.ApplyStimEffect
        /// 再从inventory中消耗1个该def的物品
        /// 注射完成后启动冷却
        /// </summary>
        public static void Inject(Pawn pawn, ThingDef stimDef)
        {
            if (pawn == null || stimDef == null) return;

            // 从inventory中找一个该def的物品实例
            Thing stimThing = null;
            foreach (Thing t in pawn.inventory.innerContainer)
            {
                if (t.def == stimDef && !t.Destroyed && t.stackCount > 0)
                {
                    stimThing = t;
                    break;
                }
            }
            if (stimThing == null) return;

            // 调用其ApplyHediff comp施加效果
            var comp = stimThing.TryGetComp<EL_CompUseEffect_ApplyHediff>();
            if (comp != null)
            {
                comp.ApplyStimEffect(pawn);
            }

            // 消耗1个
            if (stimThing.stackCount > 1)
            {
                stimThing.SplitOff(1);
            }
            else
            {
                stimThing.Destroy();
            }

            // 启动冷却
            StartCooldown(pawn);
        }

        /// <summary>
        /// 对目标pawn注射: 从使用者inventory消耗1支针剂, 对目标pawn施加效果
        /// 用于"右键其他pawn注射"场景. 饮食扣除与Hediff都作用于target.
        /// </summary>
        public static void InjectToTarget(Pawn user, Pawn target, ThingDef stimDef)
        {
            if (user == null || target == null || stimDef == null) return;

            // 从user的inventory找针剂
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

            // 对TARGET施加效果(Hediff+饮食扣除), 复用针剂的comp
            var comp = stimThing.TryGetComp<EL_CompUseEffect_ApplyHediff>();
            if (comp != null)
            {
                comp.ApplyStimEffect(target);
            }

            // 从user消耗1支
            if (stimThing.stackCount > 1)
            {
                stimThing.SplitOff(1);
            }
            else
            {
                stimThing.Destroy();
            }

            // 冷却挂在user(执行注射动作的人)
            StartCooldown(user);
        }

        /// <summary>
        /// 启动pawn的注射冷却
        /// </summary>
        public static void StartCooldown(Pawn pawn)
        {
            if (pawn == null) return;
            _cooldownUntil[pawn.thingIDNumber] = Find.TickManager.TicksGame + InjectionCooldownTicks;
        }

        /// <summary>
        /// 检查pawn是否处于冷却中
        /// </summary>
        public static bool IsOnCooldown(Pawn pawn)
        {
            if (pawn == null) return false;
            if (!_cooldownUntil.TryGetValue(pawn.thingIDNumber, out int until)) return false;
            return Find.TickManager.TicksGame < until;
        }

        /// <summary>
        /// 获取pawn剩余冷却秒数(用于显示)
        /// </summary>
        public static float GetRemainingSeconds(Pawn pawn)
        {
            if (!_cooldownUntil.TryGetValue(pawn.thingIDNumber, out int until)) return 0f;
            int remaining = until - Find.TickManager.TicksGame;
            return remaining > 0 ? remaining / 60f : 0f;
        }

        /// <summary>
        /// 获取pawn冷却进度比例(0~1)
        /// 1=刚开始冷却(进度条满), 0=冷却结束(进度条消失)
        /// 供Command_ActionWithCooldown.cooldownPercentGetter使用, 实现原版风格进度条覆盖
        /// </summary>
        public static float GetCooldownPercent(Pawn pawn)
        {
            if (!_cooldownUntil.TryGetValue(pawn.thingIDNumber, out int until)) return 0f;
            int remaining = until - Find.TickManager.TicksGame;
            if (remaining <= 0) return 0f;
            return Mathf.Clamp01((float)remaining / InjectionCooldownTicks);
        }

        /// <summary>
        /// 分配自用注射job: 从pawn inventory找具体针剂实例, 创建原地等待+执行job
        /// 供TryGetInjectionGizmo的action调用, 替代旧版瞬时Inject
        /// </summary>
        public static void GiveSelfInjectJob(Pawn pawn, ThingDef stimDef)
        {
            // 从inventory找具体的针剂Thing实例
            Thing stimThing = null;
            foreach (Thing t in pawn.inventory.innerContainer)
            {
                if (t.def == stimDef && !t.Destroyed && t.stackCount > 0)
                {
                    stimThing = t;
                    break;
                }
            }
            if (stimThing == null) return;

            // 创建并分配自用注射job: targetA=stimThing
            Job job = JobMaker.MakeJob(EL_JobDefOf.InjectStimSelf, stimThing);
            pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }

        /// <summary>
        /// 生成统一"使用针剂"Gizmo
        /// 仅在征召状态且携带针剂时显示
        /// </summary>
        public static Command_Action TryGetInjectionGizmo(Pawn pawn)
        {
            // 约束: 仅玩家殖民者 + 征召状态
            if (!pawn.IsColonistPlayerControlled) return null;
            if (!pawn.Drafted) return null;

            var stims = GetCarriedStims(pawn);
            if (stims.NullOrEmpty()) return null;

            var gizmo = new EL_Command_InjectStim
            {
                defaultLabel = "EL_Stim_UseStim".Translate(),
                defaultDesc = "EL_Stim_UseStimDesc".Translate(),
                icon = InjectionIcon,
                activateSound = SoundDefOf.Tick_Tiny,
                // 自定义冷却进度回调: 返回0~1, 用于在GizmoOnGUI中绘制纯进度条
                cooldownPercentGetter = () => GetCooldownPercent(pawn),
                action = () =>
                {
                    // 冷却中则不弹出菜单(双保险, disabled也应阻止点击)
                    if (IsOnCooldown(pawn)) return;

                    var opts = new List<FloatMenuOption>();
                    foreach (Thing stim in stims)
                    {
                        // 闭包变量捕获
                        ThingDef stimDef = stim.def;
                        int total = CountCarried(pawn, stimDef);

                        // 格式: "吗啡注射器[1]", 左侧带针剂物品图标(由shownItemForIcon自动绘制)
                        opts.Add(new FloatMenuOption(
                            $"{stimDef.LabelCap}[{total}]",
                            () => GiveSelfInjectJob(pawn, stimDef),
                            stimDef,
                            null,
                            false,
                            MenuOptionPriority.Default,
                            null,
                            null,
                            0f,
                            null,
                            null,
                            true,
                            0));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
            };

            // 冷却中: 禁用点击(disabled为protected, 通过SetDisabled方法访问)
            if (IsOnCooldown(pawn))
            {
                gizmo.SetDisabled();
            }

            return gizmo;
        }
    }

    // ============================================================
    // Harmony Patch: 在Pawn.GetGizmos后缀追加"使用针剂"gizmo
    // 仅在征召状态且携带针剂时显示, 点击后弹出子菜单选择注射种类
    // ============================================================
    [StaticConstructorOnStartup]
    public static class EL_Stims_HarmonyInit
    {
        static EL_Stims_HarmonyInit()
        {
            var harmony = new Harmony("Elanilda.StimsFromEFT");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Log.Message("[EL]Stims from EFT: Harmony patched.");
        }
}
}
