using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorld
{
    // ============================================================
    // Harmony补丁: 针剂接入成瘾品方案的行为与UI控制
    // 针剂仅作为药物方案中"携带数量调节"条目存在,
    // 不存在任何自动使用途径(满足依赖、娱乐目的、计划服用)。
    //
    // 拦截清单:
    // 1. AllowedToTakeScheduledEver Postfix: 针剂恒false, 掐断定时服用链
    //    (AllowedToTakeScheduledNow/ShouldTryToTakeScheduledNow/
    //     JobGiver_TakeDrugsForDrugPolicy 均经由该方法判定)
    // 2. AllowedToTakeToInventory Prefix: 针剂忽略allowScheduled
    //    (原版逻辑: allowScheduled=true时直接返回false, 会停止自动补货携带,
    //     需防御旧存档遗留勾选导致补货失效)
    // 3. WillIngestFromInventoryNow Prefix: 针剂返回false,
    //    移除征召状态下背包栏"服用"gizmo与装备页"消耗"按钮
    // 4. DoEntryRow Transpiler: 针剂行不绘制"定时"复选框, 并强制重置勾选状态
    //
    // 无需补丁的路径(已天然安全):
    // - 地面/背包右键"服用": ingestible.showIngestFloatOption=false(基类XML已设),
    //   FloatMenuOptionProvider_Ingest 对此类def直接返回null
    // - 药物暴食精神崩溃(JobGiver_BingeDrug): 只认CompDrug的chemical, 针剂未设chemical
    // - AI战斗自动用药(JobGiver_TakeCombatEnhancingDrug):
    //   要求CompProperties_Drug.isCombatEnhancingDrug=true, 空CompDrug默认false
    // - 化学品需求满足(JobGiver_SatisfyChemicalNeed): 需要对应chemical需求, 针剂没有
    // ============================================================
    public static class EL_DrugPolicy
    {
        /// <summary>
        /// 判断def是否为本mod针剂(带EL_CompProperties_EFTStims标记组件)
        /// </summary>
        public static bool IsStimDef(ThingDef def)
        {
            return def != null && def.GetCompProperties<EL_CompProperties_EFTStims>() != null;
        }

        // ------------------------------------------------------------
        // 定时服用链的源头恒为false
        // AllowedToTakeScheduledNow 与 ShouldTryToTakeScheduledNow 内部
        // 都会先调用本方法, 因此一个补丁即可掐断整条定时服用链
        // ------------------------------------------------------------
        [HarmonyPatch(typeof(Pawn_DrugPolicyTracker), nameof(Pawn_DrugPolicyTracker.AllowedToTakeScheduledEver))]
        public static class Patch_AllowedToTakeScheduledEver
        {
            public static void Postfix(ThingDef thingDef, ref bool __result)
            {
                if (__result && IsStimDef(thingDef))
                {
                    __result = false;
                }
            }
        }

        // ------------------------------------------------------------
        // 防御: 针剂忽略allowScheduled, 保证"携带补货"始终可用
        // 原版语义: 勾选定时的药物改由定时链直接从库存领取, 不再搬入背包。
        // 针剂没有定时服用, 必须始终按takeToInventory补货
        // ------------------------------------------------------------
        [HarmonyPatch(typeof(Pawn_DrugPolicyTracker), nameof(Pawn_DrugPolicyTracker.AllowedToTakeToInventory))]
        public static class Patch_AllowedToTakeToInventory
        {
            public static bool Prefix(Pawn_DrugPolicyTracker __instance, ThingDef thingDef, ref bool __result)
            {
                if (!IsStimDef(thingDef))
                {
                    return true; // 非针剂走原版逻辑
                }
                DrugPolicy policy = __instance.CurrentPolicy;
                if (policy == null || __instance.pawn?.inventory == null)
                {
                    __result = false;
                    return false;
                }
                DrugPolicyEntry entry = policy[thingDef];
                // 与原版逻辑一致, 但跳过allowScheduled检查
                // (针剂drugCategory=Medical, 原版的IsNonMedicalDrug/CanTakeDrug检查不适用)
                __result = entry.takeToInventory > 0
                    && !__instance.pawn.inventory.innerContainer.Contains(thingDef);
                return false;
            }
        }

        // ------------------------------------------------------------
        // 防御: 背包内"服用"入口全部对针剂关闭
        // 覆盖: 征召状态下背包栏gizmo(Pawn_InventoryTracker.GetGizmos)、
        // 装备页消耗按钮(ITab_Pawn_Gear)等所有经由该方法的调用点
        // ------------------------------------------------------------
        [HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.WillIngestFromInventoryNow))]
        public static class Patch_WillIngestFromInventoryNow
        {
            public static bool Prefix(Thing inv, ref bool __result)
            {
                if (IsStimDef(inv?.def))
                {
                    __result = false;
                    return false;
                }
                return true;
            }
        }

        // ------------------------------------------------------------
        // 药物方案界面中针剂行不绘制"计划服用"复选框
        // Transpiler把 Widgets.Checkbox(ref entry.allowScheduled) 调用
        // 替换为下方辅助方法: 针剂行不绘制并强制重置勾选
        // (allowScheduled=false 同时保证携带补货不受旧存档遗留勾选影响)
        // ------------------------------------------------------------
        [HarmonyPatch(typeof(Dialog_ManageDrugPolicies), "DoEntryRow")]
        public static class Patch_DoEntryRow_HideScheduledCheckbox
        {
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                // 目标重载: Widgets.Checkbox(float x, float y, ref bool checkOn, float size,
                //                            bool disabled, bool paintable,
                //                            Texture2D texChecked, Texture2D texUnchecked)
                // 可选参数在IL调用点会补齐全部实参, 故按完整8参数签名精确匹配
                var checkbox = AccessTools.Method(typeof(Widgets), nameof(Widgets.Checkbox),
                    new[]
                    {
                        typeof(float),
                        typeof(float),
                        typeof(bool).MakeByRefType(),
                        typeof(float),
                        typeof(bool),
                        typeof(bool),
                        typeof(Texture2D),
                        typeof(Texture2D)
                    });
                var allowScheduled = AccessTools.Field(typeof(DrugPolicyEntry), nameof(DrugPolicyEntry.allowScheduled));
                var helper = AccessTools.Method(typeof(Patch_DoEntryRow_HideScheduledCheckbox), nameof(CheckboxScheduled));
                // 兜底守卫: 任一符号查找失败时保留原方法体并记红字,
                // 保证补丁失败不阻塞游戏启动(代价仅是"定时"复选框未被隐藏)
                if (checkbox == null || allowScheduled == null || helper == null)
                {
                    Log.Error("[EL]Stims: DoEntryRow transpiler symbol lookup failed, scheduled-checkbox patch skipped.");
                    return instructions;
                }
                var codes = instructions.ToList();
                int inspected = 0;
                int replaced = 0;

                for (int i = 0; i < codes.Count; i++)
                {
                    // 匹配Widgets.Checkbox调用点: 精确签名优先, 名称兜底
                    // (防重载MethodInfo实例比对失效; 是否为"定时"复选框由下方字段回溯把关)
                    var operand = codes[i].operand as System.Reflection.MethodInfo;
                    bool isCheckboxCall = codes[i].Calls(checkbox)
                        || (operand != null && operand.DeclaringType == typeof(Widgets)
                            && operand.Name == nameof(Widgets.Checkbox));
                    if (!isCheckboxCall) continue;
                    inspected++;

                    // 仅替换float版8参调用点(Vector2版7参栈形态不同, 不能用同一辅助方法接手)
                    if (operand.GetParameters().Length != 8)
                    {
                        Log.Warning("[EL]Stims: DoEntryRow has Widgets.Checkbox overload with "
                            + operand.GetParameters().Length + " params, skipped: " + operand);
                        continue;
                    }

                    // 向前搜索ldflda/ldfld entry.allowScheduled入参,
                    // 区分"定时"复选框与成瘾/娱乐两个复选框(它们用别的字段)
                    // 注: 原版以ldflda把字段引用传给ref参数;
                    //     Harmony 2.3.6的LoadsField默认模式不匹配ldflda(实测Ldflda->False),
                    //     故必须手动判定操作码, operand引用相等前提是加载同一Assembly-CSharp实例
                    bool isScheduledCheckbox = false;
                    for (int j = i - 1; j >= 0 && i - j <= 16; j--)
                    {
                        var opcode = codes[j].opcode;
                        if ((opcode == OpCodes.Ldflda || opcode == OpCodes.Ldfld)
                            && ReferenceEquals(codes[j].operand, allowScheduled))
                        {
                            isScheduledCheckbox = true;
                            break;
                        }
                    }
                    if (!isScheduledCheckbox) continue;
                    replaced++;

                    // 注入entry(DoEntryRow第3个参数)作为辅助方法最后一个实参
                    codes.Insert(i, new CodeInstruction(OpCodes.Ldarg_2));
                    i++;
                    var original = codes[i];
                    codes[i] = new CodeInstruction(OpCodes.Call, helper);
                    foreach (var label in original.labels)
                    {
                        codes[i].labels.Add(label);
                    }
                }

                // 诊断: 启动时输出匹配结果, 用于定位"定时复选框隐藏未生效"类问题
                Log.Message("[EL]Stims: DoEntryRow transpiler done, " + inspected
                    + " Checkbox call(s) inspected, " + replaced + " scheduled checkbox(es) hidden.");
                if (replaced == 0)
                {
                    Log.Warning("[EL]Stims: scheduled checkbox NOT hidden (0 replacements), matching failed.");
                }
                return codes;
            }

            // 签名与Widgets.Checkbox(float, float, ref bool, float, bool, bool, Texture2D, Texture2D)一致
            // (可选参数在IL调用点会补齐全部实参), 末尾追加entry
            public static void CheckboxScheduled(float x, float y, ref bool checkOn, float size, bool disabled, bool paintable,
                Texture2D texChecked, Texture2D texUnchecked, DrugPolicyEntry entry)
            {
                if (entry?.drug != null && IsStimDef(entry.drug))
                {
                    checkOn = false; // 强制重置, 清理旧存档遗留的勾选
                    return;          // 针剂行不绘制"定时"复选框
                }
                Widgets.Checkbox(x, y, ref checkOn, size, disabled, paintable, texChecked, texUnchecked);
            }
        }
    }
}
