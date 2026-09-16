using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace RimWorld
{
    // ============================================================
    // CE兼容: 向CE携带方案系统注入"任意针剂"泛化槽位(仅包含本mod的22种针剂)
    //
    // 机制说明(基于CE源码LoadoutGenericDef.cs/Dialog_ManageLoadouts.cs):
    // - CE的泛化槽位(任意食物/生食/成瘾品/药品)由静态构造器代码生成, 过滤核心是
    //   私有委托字段_lambda, XML无法序列化委托, 纯XML补丁不可行, 只能运行期构造注册
    // - CE的GenericDrugs过滤器为td.IsDrug, 针剂因ingestible.drugCategory=Medical被
    //   笼统包含; 本槽位以EL_CompProperties_EFTStims标记精确识别针剂, 改defName免疫
    // - 携带方案界面直接枚举DefDatabase<LoadoutGenericDef>.AllDefs, 注册即显示
    // - 槽位按defName序列化还原, 旧存档兼容
    // ============================================================

    // 引导类: 不引用任何CE类型, 保证CE未启用时零风险
    [StaticConstructorOnStartup]
    internal static class EL_CE_LoadoutGenericBootstrap
    {
        static EL_CE_LoadoutGenericBootstrap()
        {
            if (!ModsConfig.IsActive("CETeam.CombatExtended")) return;
            try
            {
                EL_CE_LoadoutGenericInjector.Inject();
                Log.Message("[EL]Stims from EFT: CE loadout generic 'EL_GenericStims' injected.");
            }
            catch (Exception e)
            {
                // CE更新导致结构变化时仅记一条红字, 不阻塞启动
                Log.Error("[EL]Stims from EFT: failed to inject CE loadout generic: " + e);
            }
        }
    }

    // 注入器: 纯反射实现, 整个程序集元数据层面不含任何CE类型的IL引用
    // 原因: Prepatcher(CE生态启动期组件)会重写并重载所有mod程序集并解析全部类型引用,
    // "方法体隔离+永不JIT"策略在Prepatcher环境下会因CE缺失而ReflectionTypeLoadException,
    // 导致无CE玩家无法进游戏(260912线上事故). 反射目标仅为字符串, 任何重写器都能安全处理
    internal static class EL_CE_LoadoutGenericInjector
    {
        private const string CeDefTypeName = "CombatExtended.LoadoutGenericDef";

        internal static void Inject()
        {
            // 第一道门: CE程序集不存在时静默返回(与ModsConfig.IsActive双保险)
            var defType = GenTypes.GetTypeInAnyAssembly(CeDefTypeName);
            if (defType == null) return;

            // 强制CE静态构造器先完成(注册四个内置泛化槽位, 供下方短哈希预填)
            RuntimeHelpers.RunClassConstructor(defType.TypeHandle);

            var def = (Def)Activator.CreateInstance(defType);
            // defName/label/description为原版Def基类字段, 直接赋值不涉及CE类型
            def.defName = "EL_GenericStims";
            def.label = "EL_CE_GenericStims".Translate();
            def.description = "EL_CE_GenericStimsDesc".Translate();

            // CE特有字段全部反射写入
            FieldInfo countTypeField = defType.GetField("defaultCountType");
            countTypeField.SetValue(def, Enum.Parse(countTypeField.FieldType, "pickupDrop")); // 主动补货至数量, 超出丢弃
            defType.GetField("defaultCount").SetValue(def, 2); // 初始值, 可在CE携带方案界面调整
            defType.GetField("thingRequestGroup").SetValue(def, ThingRequestGroup.Drug); // 针剂均为drugCategory=Medical
            defType.GetField("isBasic").SetValue(def, true);

            // _lambda为CE私有字段: 以标记组件识别针剂
            // Predicate<ThingDef>可强类型构造——委托类型同一性仅取决于原版ThingDef, 与CE程序集内一致
            Predicate<ThingDef> lambda = (ThingDef td) => td != null && td.GetCompProperties<EL_CompProperties_EFTStims>() != null;
            defType.GetField("_lambda", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(def, lambda);

            // 反射取DefDatabase<LoadoutGenericDef>.AllDefs, 预填已注册槽位哈希防冲突(Multiplayer兼容)
            var dbType = typeof(DefDatabase<>).MakeGenericType(defType);
            var allDefs = (IEnumerable<Def>)dbType.GetProperty("AllDefs").GetGetMethod().Invoke(null, null);
            var takenHashes = new HashSet<ushort>(allDefs.Select(d => d.shortHash));

            // 内联原版ShortHashGiver算法分配短哈希(原方法private)
            ushort shortHash = (ushort)(GenText.StableStringHash(def.defName) % 65535);
            int attempts = 0;
            while (shortHash == 0 || takenHashes.Contains(shortHash))
            {
                shortHash++;
                attempts++;
                if (attempts > 5000)
                {
                    Log.Message("Short hashes are saturated. There are probably too many Defs.");
                }
            }
            def.shortHash = shortHash;

            // 注册: DefDatabase<LoadoutGenericDef>.Add(def) + InitializeShortHashDictionary(), 均反射调用
            dbType.GetMethod("Add", BindingFlags.Public | BindingFlags.Static, null, new[] { defType }, null)
                  .Invoke(null, new[] { def });
            dbType.GetMethod("InitializeShortHashDictionary", BindingFlags.Public | BindingFlags.Static)
                  ?.Invoke(null, null);
        }
    }
}
