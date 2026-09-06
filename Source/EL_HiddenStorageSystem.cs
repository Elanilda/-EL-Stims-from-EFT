using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimWorld
{
    // ============================================================
    // EL隐藏存储系统
    // 功能: 隐藏内容物 - 存入容器的物品不再被地图网格渲染
    // 使用方式(XML):
    //   1. 建筑thingClass设为EL_Building_HiddenStorage
    //   2. modExtensions添加EL_StorageExtension(hideContainedItems默认true)
    // ============================================================

    /// <summary>建筑上的XML参数入口</summary>
    public class EL_StorageExtension : DefModExtension
    {
        /// <summary>是否隐藏内容物(不渲染存入的物品)</summary>
        public bool hideContainedItems = true;
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
            if (thing.Position.GetEdifice(thing.MapHeld) is not EL_Building_HiddenStorage storage)
            {
                return false;
            }
            var ext = storage.HideExtension;
            return ext == null || ext.hideContainedItems;
        }
    }

    /// <summary>
    /// 隐藏存储容器: 基于原版Building_Storage, 支持隐藏内容物
    /// </summary>
    public class EL_Building_HiddenStorage : Building_Storage
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

    /// <summary>
    /// 储存柜内容物标签页: 复用原版ITab_ContentsBase, 从容器占格收集物品列表
    /// 物品为spawned状态(不在innerContainer), 故重写container从格子收集
    /// </summary>
    public class EL_ITab_StimStorage : ITab_ContentsBase
    {
        private readonly List<Thing> cachedItems = new List<Thing>();

        private EL_Building_HiddenStorage Storage => SelThing as EL_Building_HiddenStorage;

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
