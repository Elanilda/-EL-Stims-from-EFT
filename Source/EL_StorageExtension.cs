using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimWorld
{
    // ============================================================
    // EL存储扩展集（本地副本）
    // 本文件: EL_StorageExtension.cs（原名 EL_HiddenStorageSystem.cs，随前置库
    //   规范超集同步更名；功能由单一隐藏存储扩展为多机制存储扩展）。
    // 规范超集见前置库: Elanilda's Toolkit/Source/ElanToolkit/Storage/EL_StorageExtension.cs
    // （含堆叠数档位贴图 EL_StorageGraphicsDef、有货/空柜两态切换
    //   EL_SwitchGraphicExtension 等本副本未裁入的扩展，等待后续使用）。
    // 跨mod禁止直接IL引用（Prepatcher会解析全部类型引用，对方mod缺席时
    // ReflectionTypeLoadException 致无法进游戏，项目既定约束），故本mod保留按需裁剪副本；
    // 两mod同名类型共存无冲突：不同程序集可同名，XML类型解析任一命中行为一致；
    // Harmony前缀幂等（ShouldHide/ShouldHideStackCount对非本mod容器恒false→放行），
    // 双挂时重复执行无副作用。
    // 功能一: 隐藏内容物 - 存入容器的物品不再被地图网格渲染与禁用遮罩覆盖
    // 功能二: 隐藏堆叠数字 hideStackCount - 柜内物品不再显示地图悬浮的堆叠数量数字
    //   （原版链路: Thing.DrawGUIOverlay仅在镜头最近缩放层、def.stackLimit>1时
    //   经GenMapUI.DrawThingLabel绘制; ThingWithComps回调base, prefix挂基类单点覆盖。
    //   注意DrawGUIOverlay属GUI阶段, 不经地图网格打印, 隐藏内容物补丁拦截不到,
    //   故被隐藏的物品其堆叠数字此前仍会悬浮显示, 由本功能补齐）
    // 使用方式(XML):
    //   1. 建筑thingClass设为EL_Building_ExtendedStorage（或其子类）
    //   2. modExtensions添加EL_StorageExtension(hideContainedItems默认true,
    //      hideStackCount默认false; 两判定独立, 物品正常渲染时也可仅隐数字)
    // 注意: 本副本未裁入 EL_SwitchGraphicExtension（两态贴图切换），
    //   本mod XML不得挂载该类型（前置库DLL在场时类型可解析，但切换逻辑
    //   随前置库建筑实现，本mod的EL_Building_ExtendedStorage无此逻辑）。
    // ============================================================

    /// <summary>建筑上的XML参数入口</summary>
    public class EL_StorageExtension : DefModExtension
    {
        /// <summary>是否隐藏内容物(不渲染存入的物品)</summary>
        public bool hideContainedItems = true;

        /// <summary>是否隐藏柜内物品的堆叠数字等GUI悬浮标签(仅最近缩放层可见;
        /// 对stackLimit>1物品为堆叠数字, stackLimit==1物品为品质字样; 默认false)</summary>
        public bool hideStackCount;
    }

    public static class EL_HiddenStorageUtility
    {
        /// <summary>该物品是否应被隐藏(位于EL隐藏存储容器内且容器开启隐藏)</summary>
        public static bool ShouldHide(Thing thing)
        {
            if (thing == null || thing.def.category != ThingCategory.Item || !thing.Spawned)
            {
                return false;
            }
            if (thing.Position.GetEdifice(thing.MapHeld) is not EL_Building_ExtendedStorage storage)
            {
                return false;
            }
            var ext = storage.HideExtension;
            return ext == null || ext.hideContainedItems;
        }

        /// <summary>该物品是否应隐藏堆叠数字(位于EL隐藏存储容器内且容器开启hideStackCount;
        /// 独立于hideContainedItems判定——物品正常渲染时也可仅隐数字)</summary>
        public static bool ShouldHideStackCount(Thing thing)
        {
            if (thing == null || thing.def.category != ThingCategory.Item || !thing.Spawned)
            {
                return false;
            }
            if (thing.Position.GetEdifice(thing.MapHeld) is not EL_Building_ExtendedStorage storage)
            {
                return false;
            }
            var ext = storage.HideExtension;
            return ext != null && ext.hideStackCount;
        }
    }

    /// <summary>
    /// 隐藏存储容器: 基于原版Building_Storage, 支持隐藏内容物
    /// </summary>
    public class EL_Building_ExtendedStorage : Building_Storage
    {
        public EL_StorageExtension HideExtension => def.GetModExtension<EL_StorageExtension>();
    }

    // ============================================================
    // Harmony补丁: 隐藏被存储物品的地图渲染与禁用遮罩作用于:
    // 1. SectionLayer_ThingsGeneral.TakePrintFrom - 物品不再打印进地图网格
    // 2. OverlayDrawer.RenderForbiddenOverlay - 设计模式下不绘制禁用遮罩
    // ============================================================
    [HarmonyPatch]
    public static class EL_Patch_HideStoredThings
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            // 1.6中TakePrintFrom为protected, RenderForbiddenOverlay为private, 需用字符串引用
            yield return AccessTools.Method(typeof(SectionLayer_ThingsGeneral), "TakePrintFrom");
            yield return AccessTools.Method(typeof(OverlayDrawer), "RenderForbiddenOverlay");
        }

        [HarmonyPriority(Priority.High)]
        [HarmonyPrefix]
        public static bool Prefix(Thing t)
        {
            return !EL_HiddenStorageUtility.ShouldHide(t);
        }
    }

    // ============================================================
    // Harmony补丁: 隐藏柜内物品的堆叠数字 — Thing.DrawGUIOverlay prefix
    // 原版链路(1.6核实): Thing.DrawGUIOverlay在镜头最近缩放层、def.stackLimit>1时
    // 经GenMapUI.DrawThingLabel绘制堆叠数字(stackLimit==1且带品质则绘品质字样);
    // ThingWithComps.DrawGUIOverlay回调base后再画comps标签, prefix挂基类短路
    // 即单点覆盖全部物品子类; Pawn/建筑等子类自绘标签与本判定无关(Item前置排除)。
    // hideStackCount默认false时仅一次GetEdifice判定, 开销可忽略。
    // ============================================================
    [HarmonyPatch(typeof(Thing), "DrawGUIOverlay")]
    public static class EL_Patch_HideStackCount
    {
        [HarmonyPrefix]
        public static bool Prefix(Thing __instance)
        {
            return !EL_HiddenStorageUtility.ShouldHideStackCount(__instance);
        }
    }

    /// <summary>
    /// 储存柜内容物标签页: 复用原版ITab_ContentsBase, 从容器占格收集物品列表
    /// 物品为spawned状态(不在innerContainer), 故重写container从格子收集
    /// </summary>
    public class EL_ITab_StimStorage : ITab_ContentsBase
    {
        private readonly List<Thing> cachedItems = new List<Thing>();

        private EL_Building_ExtendedStorage Storage => SelThing as EL_Building_ExtendedStorage;

        // 原版IsVisible为SelThing.Faction == Faction.OfPlayer, 无空值保护:
        // shift多选时SingleSelectedThing为null会抛NRE导致每帧刷错, 故重写为空值安全判定
        public override bool IsVisible => Storage != null && Storage.Spawned && Storage.Faction == Faction.OfPlayer;

        public override IList<Thing> container
        {
            get
            {
                cachedItems.Clear();
                var storage = Storage;
                if (storage != null && storage.Spawned)
                {
                    foreach (var cell in storage.OccupiedRect())
                    {
                        foreach (var thing in storage.Map.thingGrid.ThingsListAt(cell))
                        {
                            if (thing.def.category == ThingCategory.Item)
                            {
                                cachedItems.Add(thing);
                            }
                        }
                    }
                }
                return cachedItems;
            }
        }

        public EL_ITab_StimStorage()
        {
            labelKey = "EL_Tab_StimStorageContents";
            containedItemsKey = "EL_Tab_StimStorageContents";
            // 禁用移除按钮, 防止从列表误删; 物品仍可点击查看信息卡并选中
            canRemoveThings = false;
        }
    }
}
