using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimWorld
{
    // ============================================================
    // "自主治疗"开关的持久化状态(管理用药方案界面, 定时列右半区)
    // 键: 药物方案id -> 该方案内开启自主治疗的针剂defName集合
    // 原版DrugPolicy/DrugPolicyEntry无扩展点, 故用平行GameComponent存储
    // 存档安全: 键为int方案id与string defName, 无def交叉引用;
    //          旧档读取时缺失键回退为"全部关闭", 无兼容性破坏
    // ============================================================
    /// <summary>
    /// 单个药物方案的自主治疗开关集合(IExposable值条目):
    /// HashSet不能作为LookMode.Value被ParseHelper解析(存档回报"unknown data type HashSet`1"),
    /// 必须以LookMode.Deep经IExposable序列化, 内部用List承载defName
    /// </summary>
    public class EL_AutoTreatEntry : IExposable
    {
        public List<string> defNames = new List<string>();

        public void ExposeData()
        {
            Scribe_Collections.Look(ref defNames, "defNames", LookMode.Value);
            defNames ??= new List<string>();
        }
    }

    public class EL_AutoTreatState : GameComponent
    {
        private Dictionary<int, EL_AutoTreatEntry> autoTreat = new Dictionary<int, EL_AutoTreatEntry>();

        // ExposeData时药物方案数据库可能尚未就绪, 孤儿键清理留待加载后首个tick
        private bool cleanupPending;

        public EL_AutoTreatState()
        {
        }

        public EL_AutoTreatState(Game game)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            // v2: 旧标签"autoTreat"曾以LookMode.Value写入损坏的HashSet节点(存档报
            // "unknown data type HashSet`1"), 换用新标签使旧坏节点被读档器自然忽略
            Scribe_Collections.Look(ref autoTreat, "autoTreatV2", LookMode.Value, LookMode.Deep);
            autoTreat ??= new Dictionary<int, EL_AutoTreatEntry>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // 读档完成后安排一次孤儿键清理(方案被删除后的残留id键)
                cleanupPending = true;
            }
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            if (!cleanupPending) return;
            DrugPolicyDatabase db = Current.Game?.drugPolicyDatabase;
            if (db == null) return; // 数据库未就绪, 保留标记下一tick重试
            cleanupPending = false;
            CleanupOrphanKeys(db);
        }

        // 孤儿键清理: 方案删除后其id键不再有意义, 读档时统一移除
        private void CleanupOrphanKeys(DrugPolicyDatabase db)
        {
            List<int> orphans = autoTreat.Keys
                .Where(id => db.AllPolicies.All(p => p.id != id))
                .ToList();
            for (int i = 0; i < orphans.Count; i++)
            {
                autoTreat.Remove(orphans[i]);
            }
            if (orphans.Count > 0)
            {
                Log.Message("[EL]Stims: auto-treat state cleaned " + orphans.Count + " orphan policy key(s).");
            }
        }

        public static bool IsEnabled(DrugPolicy policy, ThingDef stim)
        {
            if (policy == null || stim == null) return false;
            var state = Current.Game?.GetComponent<EL_AutoTreatState>();
            if (state?.autoTreat == null) return false;
            return state.autoTreat.TryGetValue(policy.id, out var entry)
                && entry?.defNames != null
                && entry.defNames.Contains(stim.defName);
        }

        public static void Set(DrugPolicy policy, ThingDef stim, bool value)
        {
            if (policy == null || stim == null) return;
            var state = Current.Game?.GetComponent<EL_AutoTreatState>();
            if (state == null) return;
            state.autoTreat ??= new Dictionary<int, EL_AutoTreatEntry>();
            if (!state.autoTreat.TryGetValue(policy.id, out var entry))
            {
                state.autoTreat[policy.id] = entry = new EL_AutoTreatEntry();
            }
            if (value)
            {
                if (!entry.defNames.Contains(stim.defName))
                {
                    entry.defNames.Add(stim.defName);
                }
            }
            else
            {
                entry.defNames.Remove(stim.defName);
            }
        }
    }
}
