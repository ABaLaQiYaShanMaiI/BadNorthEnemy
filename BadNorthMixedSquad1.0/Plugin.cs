using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.RaidGeneration;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthMixedSquad1_0
{
    /// <summary>
    /// BadNorthMixedSquad v1.0 —— 混编战术小队：GameSetup.Awake 新建干净 VikingReference（Viking_MixedSquad）注册进
    /// 敌人生成池；Squad.CreateAgent 按每船角色队列换 prefab（盾=原版剑盾/矛=黑矛兵剥离模板/弓=原版弓手）；
    /// Landing.Spawn 施加长矛技能 + 战术分层站位（盾前矛中弓后 + 抵阵等敌）。
    /// </summary>
    [BepInPlugin(Plugin.GUID, Plugin.NAME, Plugin.VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "badnorth.mixedsquad.v1.0";
        public const string NAME = "Bad North - Mixed Squad v1.0";
        public const string VERSION = "1.0.0";

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        // 全部 cfg 配置已收进 ModConfig 静态类（字段/绑定/分段 Bind 见 ModConfig.cs），
        // 统一入口 ModConfig.ShieldFullyRemoved（cfg EnableShield=false = 完全移除盾牌）。

        static VikingReference _blackSpearman;
        static VikingAgent _sourceViking;
        static readonly HashSet<Agent> _done = new HashSet<Agent>();

        // ===== 混编小队（M1）：模板 + 每船角色队列 =====
        static Agent _spearTemplate;                         // 长矛角色模板（黑矛兵剥离模板，黑矛兵定型）
        static VikingReference _archerRef;                   // 原版 Archer 引用（弓模板懒解析）
        static Agent _archerTemplate;                        // 弓角色模板（缓存 _archerRef.agent）
        static readonly List<MixedRoleType> _roleQueue = new List<MixedRoleType>();  // 本船角色队列
        static int _roleCursor;                              // CreateAgent 消费游标
        static MixedRoleType _lastRole;                      // 最近消费角色（后缀挂 MixedRole）
        static bool _patchMixedCreate;
        static bool _patchPirate;   // 压制 Pirate 冲建筑（阵型纪律）
        static bool _inMixedLanding;                         // 混编 Landing.Spawn 进行中（补丁只在此刻生效，绝不干扰原版生成）
        static Agent _mixedNominal;                          // 混编标称 prefab（vikingClone.agent 缓存，防 vikingClone 未就绪 NPE）

        // Patch 生效状态（供启动总览与运行期检查）：启动日志里每一行都必须看到 OK
        static bool _patchLevelNode, _patchRange, _patchGetAttack, _patchAttack, _patchAttackUpdate, _patchPlayAnimation, _patchClashBlock;
        static readonly HashSet<Agent> _attackUpdateSeen = new HashSet<Agent>();
        static int _lastDictCount = -1;   // REGISTER 等待日志只在 dict 条目数变化时打（注册重试不刷屏）
        static readonly int AttackAnimHash = Animator.StringToHash("Attack");
        static readonly int ClashAnimHash = Animator.StringToHash("Clash");
        static float _lastAnimLogTime = -999f;
        static float _lastClashBlockLog = -999f;   // ClashActivate 拦截日志节流

        /// <summary>诊断探针读取：当前已注册的新单位。</summary>
        public static VikingReference BlackSpearman => _blackSpearman;
        /// <summary>诊断探针读取：已处理的黑矛兵 Agent 数量。</summary>
        public static int TrackedAgentCount => _done.Count;
        /// <summary>源 SwordShield 预制体（含未剥离的 Shield 组件 → 读取序列化 shieldSmash 火花；剥离模板已 DestroyImmediate 盾组件）。</summary>
        public static VikingAgent SourceViking => _sourceViking;

        void Awake()
        {
            Instance = this;
            Log = Logger;
            try
            {
                ModConfig.Bind(Config);

                // cfg → 弓手战术静态开关（集火点射 + 箭矢追踪命中率）
                try
                {
                    ArcherCombat.Enabled = ModConfig.EnableArcherFocus.Value;
                    ArcherCombat.TrackingStrength = ModConfig.ArrowTrackingStrength.Value;
                }
                catch { }

                // 把 cfg Diag 段写入 BSLog 静态开关（各诊断组件读取）
                try
                {
                    BSLog.VerboseDumps = ModConfig.DiagVerboseDumps.Value;
                    BSLog.HeadTrace = ModConfig.DiagHeadTrace.Value;
                    BSLog.DeathTrace = ModConfig.DiagDeathTrace.Value;
                    BSLog.HitDoubleTrace = ModConfig.DiagHitDoubleTrace.Value;
                }
                catch { }

                // 初始化日志系统（控制台 + 文件 + 全局异常捕获）+ 诊断探针
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                BSLog.Init(dir);
                // 装配身份：确认真正加载的 DLL 是刚构建/部署的那一份（防"改了源码却没部署/部署错位"）
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var fi = new FileInfo(asm.Location);
                    BSLog.Info("[装配] " + asm.FullName);
                    BSLog.Info("[装配] 路径=" + asm.Location);
                    BSLog.Info("[装配] 大小=" + fi.Length + "B 修改时间=" + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                }
                catch { }
                gameObject.AddComponent<DiagnosticsComponent>();

                On.Voxels.TowerDefense.GameSetup.Awake += OnGameSetupAwake;
                On.Voxels.TowerDefense.RaidGeneration.Landing.Spawn += OnLandingSpawn;

                var harmony = new Harmony(GUID);
                PatchLevelNodeSetup(harmony);
                PatchSwordsman(harmony);
                PatchMixedSquad(harmony);
                ArcherCombat.PatchAll(harmony);
                PatchPirate(harmony);

                // 启动总览：每条 Patch 的生效状态一眼可见；任一 FAIL 都意味着原版逻辑仍残留
                BSLog.Info("[PATCH·总览] " +
                    "LevelNode=" + (_patchLevelNode ? "OK" : "FAIL") +
                    " range=" + (_patchRange ? "OK" : "FAIL") +
                    " GetAttack=" + (_patchGetAttack ? "OK" : "FAIL") +
                    " Attack=" + (_patchAttack ? "OK" : "FAIL") +
                    " AttackUpdate=" + (_patchAttackUpdate ? "OK" : "FAIL") +
                    " PlayAnim=" + (_patchPlayAnimation ? "OK" : "FAIL") +
                    " ClashBlock=" + (_patchClashBlock ? "OK" : "FAIL") +
                    " Mixed=" + (_patchMixedCreate ? "OK" : "FAIL") +
                    " Pirate=" + (_patchPirate ? "OK" : "FAIL") +
                    " Archer=" + (ArcherCombat.PatchOK ? "OK" : "FAIL") +
                    " ← 全 OK 才代表长矛穿刺/混编真正接管；Mixed FAIL 则 CreateAgent 角色轮换静默失效");

                BSLog.Info($"[MixedSquad] Ready. 新单位: {ModConfig.NewVikingName.Value}");
                BSLog.Info($"[配置] Source={ModConfig.SourceVikingName.Value} New={ModConfig.NewVikingName.Value} Bounty={ModConfig.Bounty.Value} " +
                    $"SpawnChance={ModConfig.SpawnChance.Value} ForceFirstWave={ModConfig.ForceFirstWave.Value} " +
                    $"盾:{ModConfig.MixedShieldPer9.Value} 矛:{ModConfig.MixedSpearPer9.Value} 弓:{ModConfig.MixedArcherPer9.Value} " +
                    $"EnableFormation={ModConfig.EnableFormation.Value} 船前盾={ModConfig.EnableShipShieldFront.Value} " +
                    $"弓集火={ModConfig.EnableArcherFocus.Value} 追踪={ModConfig.ArrowTrackingStrength.Value}");
            }
            catch (Exception e)
            {
                try { Logger.LogError("[MixedSquad] Awake 初始化异常: " + e); }
                catch { }
                try { BSLog.Error("[MixedSquad] Awake 初始化异常: " + e); }
                catch { }
            }
        }

        void OnGameSetupAwake(On.Voxels.TowerDefense.GameSetup.orig_Awake orig, GameSetup self)
        {
            orig(self);
            // dict 键列表（很长）归入 VerboseDumps；平时只报条数
            BSLog.Info("[BOOT] GameSetup.Awake 完成，dict 现有 " + LevelStateObjectReferences.dict.Count + " 个条目" +
                (BSLog.VerboseDumps ? ": " + BSLog.Join(LevelStateObjectReferences.dict.Keys) : ""));
            EnsureBlackSpearmanRegistered();
        }

        /// <summary>确保混编小队已注册（dict 菜单阶段为空，GameSetup.Awake 与 LevelNode.Setup 都调用，重试直到成功）。</summary>
        bool EnsureBlackSpearmanRegistered()
        {
            if (_blackSpearman != null) return true;
            if (!LevelStateObjectReferences.dict.TryGetValue(ModConfig.SourceVikingName.Value, out var srcObj))
            {
                // 等待日志只在 dict 条目数变化时打（GameSetup.Awake 会重试多次，旧版每次都刷一行）
                int cnt = LevelStateObjectReferences.dict.Count;
                if (cnt != _lastDictCount)
                {
                    _lastDictCount = cnt;
                    BSLog.Info($"[REGISTER] 等待源单位 {ModConfig.SourceVikingName.Value} 注册（当前 dict 条目 {cnt}），稍后重试");
                }
                return false;
            }
            var src = srcObj as VikingReference;
            if (src == null)
            {
                BSLog.Error("[REGISTER] 源对象不是 VikingReference");
                return false;
            }
            try { RegisterMixedSquad(src); }
            catch (Exception e) { BSLog.Error("[REGISTER] 注册失败: " + e); }
            return _blackSpearman != null;
        }

        /// <summary>注册混编小队 VikingReference：viking 字段指向原版 SwordShield（标称=盾模板，不剥离），
        /// 长矛模板=黑矛兵剥离模板（黑矛兵定型，复用 BuildStrippedTemplate），弓模板=原版 Archer（懒解析）。</summary>
        void RegisterMixedSquad(VikingReference src)
        {
            var vikingField = typeof(VikingReference).GetField("viking",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _sourceViking = null;
            if (!ReferenceEquals(vikingField, null))
                _sourceViking = vikingField.GetValue(src) as VikingAgent;
            if (_sourceViking == null)
            {
                BSLog.Error("[REGISTER] 源 SwordShield VikingReference 没有 viking 预制体引用");
                return;
            }

            // 长矛模板 = 黑矛兵剥离模板（黑矛兵定型：无盾、长矛、冲锋/刺击全套）。
            // ⚠️ VikingAgent 是 AgentComponent（非 Agent），CreateAgent 需要 Agent 组件 → 取 .agent。
            var stripped = BuildStrippedTemplate(_sourceViking);
            _spearTemplate = stripped != null ? stripped.agent : _sourceViking.agent;
            // 弓模板 = 原版 Archer（懒解析引用，agent 在 Start 后可用）
            _archerRef = FindArcherReference();

            // 新建干净对象而非克隆 prefab：作为根对象存在，避开 LevelArcConsistency 扫描与 DomainBool NPE。
            var go = new GameObject(ModConfig.NewVikingName.Value);
            DontDestroyOnLoad(go);

            var vr = go.AddComponent<VikingReference>();
            vr.type = VikingAgent.Type.SwordShield; // 标称类型复用剑盾（盾兵角色即原版剑盾兵）
            vr.bounty = ModConfig.Bounty.Value;
            vr.approachAudioId = src.approachAudioId;
            vr.arriveAudioId = src.arriveAudioId;

            // 标称 viking = 原版 SwordShield 源（不剥离）→ 混编"默认角色"= 盾兵，CreateAgent 按角色换 prefab 生成矛/弓。
            if (!ReferenceEquals(vikingField, null))
                vikingField.SetValue(vr, _sourceViking);

            // 不手动实例化 vikingClone —— 原版 VikingReference.Start() 下一帧创建唯一副本。

            LevelStateObjectReferences.dict[ModConfig.NewVikingName.Value] = vr;
            _blackSpearman = vr;

            BSLog.Info($"[REGISTER] 已注册混编小队 {ModConfig.NewVikingName.Value} (type={vr.type}, bounty={vr.bounty} 盾/矛/弓模板就绪)");
            BSLog.Raw($"[REGISTER] 注册后 dict 键: {BSLog.Join(LevelStateObjectReferences.dict.Keys)}");
            StartCoroutine(ApplyArtDelayed(vr));
        }

        /// <summary>从 dict 找原版 Archer 引用（按 type 而非名字，稳健）。</summary>
        static VikingReference FindArcherReference()
        {
            try
            {
                foreach (var kv in LevelStateObjectReferences.dict)
                {
                    var r = kv.Value as VikingReference;
                    if (r != null && r.type == VikingAgent.Type.Archer) return r;
                }
            }
            catch (Exception e) { BSLog.Warn("[REGISTER] 找 Archer 引用异常: " + e.Message); }
            return null;
        }

        /// <summary>混编生成补丁：Squad.CreateAgent 前缀按每船角色队列换 prefab，后缀挂 MixedRole 标记。</summary>
        static void PatchMixedSquad(Harmony harmony)
        {
            TryPatch("Squad.CreateAgent（混编角色轮换）", () =>
            {
                var m = typeof(Squad).GetMethod("CreateAgent", new[] { typeof(Agent), typeof(NavPos) });
                // ⚠️ .NET 3.5 雷：反射对象判空必须 ReferenceEquals（`==` 引 MethodInfo.op_Equality → MissingMethodException）
                if (ReferenceEquals(m, null)) throw new Exception("CreateAgent 方法未找到");
                harmony.Patch(m,
                    prefix: new HarmonyMethod(typeof(Plugin).GetMethod("SquadCreateAgentPrefix", BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(Plugin).GetMethod("SquadCreateAgentPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
            }, ref _patchMixedCreate);
        }

        /// <summary>压制 Pirate 冲建筑（阵型纪律）：登岛后的混编阵型单位跳过原版 Pirate 移动驱动
        /// （walkDir += orderDir 冲建筑 + 面向船），由 TacticalFormation 统一控制站位。1.3 黑矛兵/原版单位不受影响。</summary>
        static void PatchPirate(Harmony harmony)
        {
            TryPatch("Pirate.ApplyOrder（压制冲建筑·阵型纪律）", () =>
            {
                var m = typeof(Pirate).GetMethod("ApplyOrder", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(m, null)) throw new Exception("Pirate.ApplyOrder 不存在");
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(Plugin).GetMethod("PirateApplyOrderPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
            }, ref _patchPirate);
        }

        /// <summary>Pirate.ApplyOrder 前缀：混编单位登岛后 → 跳过建筑指令（阵型纪律：盾/矛/弓优先接敌打人、不冲房区）。
        /// 船上/未登岛不拦（让 Pirate 正常下船）。</summary>
        static bool PirateApplyOrderPrefix(Pirate __instance)
        {
            try
            {
                if (__instance == null) return true;
                Agent agent = __instance.agent;
                if (agent != null && agent.navPos.valid && agent.navPos.onMain)
                {
                    if (TacticalFormation.InFormation(agent)) return false;   // 阵型接管移动
                    // 兜底：EnableFormation 下即便阵型角色列表暂未同步，带 MixedRole 标记的混编单位也压制建筑指令
                    if (ModConfig.EnableFormation != null && ModConfig.EnableFormation.Value)
                    {
                        var role = agent.GetComponent<MixedRole>();
                        if (role != null) return false;
                    }
                }
            }
            catch { }
            return true;
        }

        static void SquadCreateAgentPrefix(Squad __instance, ref Agent prefab)
        {
            try
            {
                _lastRole = MixedRoleType.None;   // P1 修复：每次调用先复位，只有真正消费角色时才赋值 → 杜绝串给同船后续原版 agent
                // ⚠️ 隔离：只有混编 Landing.Spawn 进行中才可能换 prefab；其余任何 CreateAgent（原版兵种）一律不碰
                if (!_inMixedLanding || _blackSpearman == null || prefab == null) return;
                if (_roleCursor >= _roleQueue.Count) return;
                // 只处理混编标称 prefab（= vikingClone.agent，非按名字）；vikingClone 未就绪时先缓存
                if (_mixedNominal == null && _blackSpearman.vikingClone != null)
                    _mixedNominal = _blackSpearman.vikingClone.agent;
                if (_mixedNominal == null || !ReferenceEquals(prefab, _mixedNominal)) return;
                var role = _roleQueue[_roleCursor++];
                _lastRole = role;
                switch (role)
                {
                    case MixedRoleType.Spear:
                        if (_spearTemplate != null) prefab = _spearTemplate;
                        break;
                    case MixedRoleType.Archer:
                        if (_archerTemplate == null && _archerRef != null && _archerRef.vikingClone != null) _archerTemplate = _archerRef.vikingClone.agent;
                        if (_archerTemplate != null) prefab = _archerTemplate;
                        break;
                    // Shield：保持标称（= 原版剑盾模板）
                }
            }
            catch (Exception e) { BSLog.Warn("[混编] CreateAgent 前缀异常: " + e.Message); }
        }

        static void SquadCreateAgentPostfix(Squad __instance, Agent __result)
        {
            try
            {
                if (!_inMixedLanding || __result == null) return;   // 隔离：非混编登岛绝不挂 MixedRole
                if (_lastRole == MixedRoleType.None) return;
                var mr = __result.GetComponent<MixedRole>();
                if (mr == null) mr = __result.gameObject.AddComponent<MixedRole>();
                if (mr != null) mr.role = _lastRole;
                _lastRole = MixedRoleType.None;
            }
            catch { }
        }

        /// <summary>按本船混编 ShipLoad 的 count 生成盾:矛:弓 角色队列（比例走 cfg，默认 4:3:2）。</summary>
        static void ComputeRoleQueue(Landing self)
        {
            _roleQueue.Clear(); _roleCursor = 0; _lastRole = MixedRoleType.None;
            _inMixedLanding = false;
            if (self == null || self.shipLoads == null || _blackSpearman == null) return;
            foreach (var load in self.shipLoads)
            {
                if (load == null || load.vikingRef != _blackSpearman) continue;
                _inMixedLanding = true;   // 本船确实有混编 ShipLoad → 本航次 CreateAgent 补丁才生效
                if (_mixedNominal == null && _blackSpearman.vikingClone != null)
                    _mixedNominal = _blackSpearman.vikingClone.agent;
                int count = Mathf.Max(0, load.count);
                int nShield = Mathf.Max(0, ModConfig.MixedShieldPer9 != null ? ModConfig.MixedShieldPer9.Value : 4);
                int nSpear = Mathf.Max(0, ModConfig.MixedSpearPer9 != null ? ModConfig.MixedSpearPer9.Value : 3);
                int nArcher = Mathf.Max(0, ModConfig.MixedArcherPer9 != null ? ModConfig.MixedArcherPer9.Value : 2);
                int ratioSum = Mathf.Max(1, nShield + nSpear + nArcher);
                int shield = Mathf.RoundToInt(count * (float)nShield / ratioSum);
                int spear = Mathf.RoundToInt(count * (float)nSpear / ratioSum);
                int archer = count - shield - spear;
                // 随人数提升：配置的三兵种比例都 >0 时保底每兵种至少 1（count≥3 盾/矛/弓都上场），
                // 不足的从"多出来"的角色匀出（循环直到可分配；比例含 0 的角色不硬凑）
                if (count >= 3 && nShield > 0 && nSpear > 0 && nArcher > 0)
                {
                    while (shield < 1 || spear < 1 || archer < 1)
                    {
                        if (shield < 1) { shield++; if (archer > 1) archer--; else if (spear > 1) spear--; else break; }
                        else if (spear < 1) { spear++; if (archer > 1) archer--; else if (shield > 1) shield--; else break; }
                        else if (archer < 1) { archer++; if (spear > 1) spear--; else if (shield > 1) shield--; else break; }
                    }
                }
                for (int i = 0; i < shield; i++) _roleQueue.Add(MixedRoleType.Shield);
                for (int i = 0; i < spear; i++) _roleQueue.Add(MixedRoleType.Spear);
                for (int i = 0; i < archer; i++) _roleQueue.Add(MixedRoleType.Archer);
            }
        }

        /// <summary>克隆源 VikingAgent 预制体并剥离逻辑残留组件（Pirate/KillAllEnemies/Swordsman 必须保留）。</summary>
        static VikingAgent BuildStrippedTemplate(VikingAgent src)
        {
            if (src == null) return null;
            try
            {
                GameObject stripped = UnityEngine.Object.Instantiate(src.gameObject);
                stripped.name = "BlackSpearman_StrippedTemplate";
                // 自身保持 active（Start 克隆的 vikingClone 才会可见），挂在 inactive holder 下避免出现在场景。
                var holder = new GameObject("BlackSpearman_StrippedHolder");
                holder.SetActive(false);
                stripped.transform.SetParent(holder.transform, false);

                // ① 逻辑残留：DestroyImmediate 立即销毁（Destroy 延迟到帧末，VR.Start() 同帧就会克隆本模板）。
                var arsonist = stripped.GetComponent<Arsonist>();
                if (arsonist != null) UnityEngine.Object.DestroyImmediate(arsonist);
                // 销毁 Shield 逻辑组件前先记录盾牌子对象（美术资源维度：
                // 权威引用 Shield.shield 字段优先，名称关键字兜底——避免\"组件销毁后再也定位不到盾牌\"）。
                var shieldComp = stripped.GetComponent<Shield>();
                Transform shieldTf = (shieldComp != null && shieldComp.shield != null) ? shieldComp.shield : null;
                if (shieldTf == null) shieldTf = BlackSpearmanWeapon.FindShieldTransform(stripped.transform);
                if (shieldComp != null) UnityEngine.Object.DestroyImmediate(shieldComp);

                // ② 视觉残留：禁用剑/武器/瞄准骨子对象（盾牌保留——剑盾兵基底的盾牌美术即黑矛兵的盾牌）
                int removedVisuals = BlackSpearmanWeapon.DisableChildrenByNames(stripped.transform, BlackSpearmanWeapon.VisualChildNameKeys);
                // 用户选择完全移除盾牌（效果+美术）→ 剥离模板里也禁用盾牌子对象（双保险，
                // VikingReference.Start() 克隆模板后 ApplyToAgent 还会再禁一次）。
                if (ModConfig.ShieldFullyRemoved)
                    BlackSpearmanWeapon.DisableChildrenByNames(stripped.transform, BlackSpearmanWeapon.ShieldChildNameKeys);

                BSLog.Info("[REGISTER] 已生成剥离模板: 删除Arsonist=" + (arsonist != null) +
                    " 删除Shield组件=" + (shieldComp != null) +
                    " 盾牌美术[美术资源]=" + (shieldTf != null
                        ? shieldTf.name + " active=" + shieldTf.gameObject.activeSelf
                        : "缺失（黑矛兵将无盾牌视觉!）") +
                    "，禁用剑/武器子对象 " + removedVisuals + " 个");
                return stripped.GetComponent<VikingAgent>();
            }
            catch (Exception e)
            {
                BSLog.Error("[REGISTER] 剥离模板失败: " + e);
                return null;
            }
        }

        IEnumerator ApplyArtDelayed(VikingReference vr)
        {
            yield return null;
            yield return null;
            try
            {
                var icon = BlackSpearmanArt.GetIcon();
                if (icon != null)
                {
                    vr.sprite2 = icon;
                    BSLog.Info($"[ART] 美术图标已应用: sprite2={(vr.sprite2 != null)}");
                }
                else
                {
                    BSLog.Warn("[ART] 图标加载失败，sprite2 保持为 Start() 提取值");
                }
            }
            catch (Exception e) { BSLog.Warn("[ART] 美术资源加载失败: " + e); }
            try { BlackSpearmanArt.RegisterLocalization(); }
            catch (Exception e) { BSLog.Warn("[ART] 本地化注册失败: " + e); }
            // 延迟到 vikingClone 生成后，转储完整字段，确认反射配置成功
            DiagnosticsComponent.DumpVikingReference(vr, "注册后·延迟检查");
        }

        void PatchLevelNodeSetup(Harmony harmony)
        {
            TryPatch("LevelNode.Setup（把新单位注入敌人生成池）", () =>
            {
                var method = typeof(LevelNode).GetMethod("Setup",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(LevelState) }, null);
                if (ReferenceEquals(method, null)) throw new Exception("LevelNode.Setup 不存在");
                harmony.Patch(method, postfix: new HarmonyMethod(
                    typeof(Plugin).GetMethod("LevelNodeSetupPostfix",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }, ref _patchLevelNode);
        }

        void PatchSwordsman(Harmony harmony)
        {
            // 1) 长矛攻击距离：Patch Swordsman.range getter（默认 radius*0.7，长矛更长）
            TryPatch("Swordsman.range（长矛攻击距离）", () =>
            {
                var prop = typeof(Swordsman).GetProperty("range");
                var m = !ReferenceEquals(prop, null) ? prop.GetGetMethod() : null;
                if (ReferenceEquals(m, null)) throw new Exception("range getter 不存在");
                harmony.Patch(m, postfix: new HarmonyMethod(
                    typeof(Plugin).GetMethod("SwordsmanRangePostfix",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }, ref _patchRange);

            // 2) 长矛攻击表现：Patch Swordsman.GetAttack（换成长矛音效/方向）
            TryPatch("Swordsman.GetAttack（长矛攻击）", () =>
            {
                var m = typeof(Swordsman).GetMethod("GetAttack",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(Agent) }, null);
                if (ReferenceEquals(m, null)) throw new Exception("GetAttack 不存在");
                harmony.Patch(m, prefix: new HarmonyMethod(
                    typeof(Plugin).GetMethod("SwordsmanGetAttackPrefix",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }, ref _patchGetAttack);

            // ⚠️ 勿按参数类型定位 Attack：Func<,> 引用会让 Unity Mono(mscorlib 2.0) JIT 抛 TypeLoadException，
            // 使整批 Patch 静默失效。按方法名查找（Swordsman 只有一个 public Attack，无歧义）。
            TryPatch("Swordsman.Attack（长矛穿刺·不播挥剑）", () =>
            {
                var m = typeof(Swordsman).GetMethod("Attack", BindingFlags.Instance | BindingFlags.Public);
                if (ReferenceEquals(m, null)) throw new Exception("Attack 不存在");
                harmony.Patch(m, prefix: new HarmonyMethod(
                    typeof(Plugin).GetMethod("SwordsmanAttackPrefix",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }, ref _patchAttack);

            // 3) 长矛穿刺节奏：Patch Swordsman.AttackUpdate（用矛刺周期结束攻击，而非等挥剑动画播完）
            TryPatch("Swordsman.AttackUpdate（长矛穿刺节奏）", () =>
            {
                var m = typeof(Swordsman).GetMethod("AttackUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
                if (ReferenceEquals(m, null)) throw new Exception("AttackUpdate 不存在");
                harmony.Patch(m, prefix: new HarmonyMethod(
                    typeof(Plugin).GetMethod("SwordsmanAttackUpdatePrefix",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }, ref _patchAttackUpdate);

            // 4) 挥剑动画溯源：Patch Agent.PlayAnimation(int)，黑矛兵播放 Attack/Clash 时打调用栈
            TryPatch("Agent.PlayAnimation（挥剑动画溯源）", () =>
            {
                var m = typeof(Agent).GetMethod("PlayAnimation",
                    BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(int) }, null);
                if (ReferenceEquals(m, null)) throw new Exception("PlayAnimation(int) 不存在");
                harmony.Patch(m, prefix: new HarmonyMethod(
                    typeof(Plugin).GetMethod("AgentPlayAnimationPrefix",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }, ref _patchPlayAnimation);

            // 5) 拦截原版挥剑 Clash 动画——黑矛兵每次命中（SpearHit/冲锋 HIT）后
            // Agent.DealDamage → Swordsman.ModifyAttack → clash.SetActive → ClashActivate 会播放
            // Swordsman_Clash（剑击滑动动画，实测 body=slide:True、clip=Swordsman_Clash），
            // 叠加在矛刺上 = “人物抽动”的元凶之一。黑矛兵直接跳过（伤害在 DealDamage 内已结算）。
            TryPatch("Swordsman.ClashActivate（拦截挥剑动画·治抽动）", () =>
            {
                var m = typeof(Swordsman).GetMethod("ClashActivate",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(m, null)) throw new Exception("ClashActivate 不存在");
                harmony.Patch(m, prefix: new HarmonyMethod(
                    typeof(Plugin).GetMethod("SwordsmanClashActivatePrefix",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }, ref _patchClashBlock);
        }

        // ⚠️ 雷区（Unity Mono / mscorlib 2.0）：禁止 System.Action/System.Func（TypeLoadException 使整批
        // Patch 失效）—— 用自定义委托；反射对象判空一律 ReferenceEquals（== 会引 op_Equality 导致
        // MissingMethodException）。
        delegate void PatchJob();

        // 每个 Patch 独立 try/catch：单个失败不拖垮其余 Patch。
        static void TryPatch(string label, PatchJob apply, ref bool ok)
        {
            try
            {
                apply();
                ok = true;
                BSLog.Info("[PATCH] ✅ " + label);
            }
            catch (Exception e)
            {
                ok = false;
                BSLog.Error("[PATCH] ❌ " + label + " 失败: " + e);
            }
        }

        // 挥剑动画溯源：黑矛兵被要求播放 Attack/Clash 动画时打调用栈。
        static void AgentPlayAnimationPrefix(Agent __instance, int anim)
        {
            try
            {
                if (__instance == null || !_done.Contains(__instance)) return;
                if (anim != AttackAnimHash && anim != ClashAnimHash) return;
                if (Time.time - _lastAnimLogTime < 0.5f) return;
                _lastAnimLogTime = Time.time;
                BSLog.Warn("[动画·溯源] ⚠️ 黑矛兵被要求播放 " +
                    (anim == AttackAnimHash ? "Attack(挥剑)" : "Clash") + " 动画 hash=" + anim +
                    " —— 刺击期间出现即说明还有原版挥剑路径，调用栈：");
                try
                {
                    var st = new System.Diagnostics.StackTrace(1, false);
                    var frames = st.GetFrames();
                    int n = frames != null ? Math.Min(frames.Length, 6) : 0;
                    for (int i = 0; i < n; i++)
                    {
                        var m = frames[i].GetMethod();
                        if (ReferenceEquals(m, null)) continue;
                        string cls = !ReferenceEquals(m.DeclaringType, null) ? m.DeclaringType.Name : "?";
                        BSLog.Info("    ← " + cls + "." + m.Name);
                    }
                }
                catch { }
            }
            catch { }
        }

        // 黑矛兵不播原版 Clash（剑击）动画。命中后 ModifyAttack→ClashActivate 会在矛刺期间
        // 播放 Swordsman_Clash（身体滑动击打动画，见日志 clip=Swordsman_Clash / body=slide:True）=“人物抽动”。
        // 只拦动画与音效（ClashActivate 仅播动画+设 animator bool，IL 已验证），clash 状态生命周期照旧。
        static bool SwordsmanClashActivatePrefix(Swordsman __instance)
        {
            try
            {
                if (__instance == null || __instance.agent == null) return true;
                if (!_done.Contains(__instance.agent)) return true;
                if (Time.time - _lastClashBlockLog > 2f)
                {
                    _lastClashBlockLog = Time.time;
                    BSLog.Info("[动画·拦截] 已拦截黑矛兵 ClashActivate（原版剑击动画，避免刺击时人物抽动）");
                }
                return false;   // 跳过原版 ClashActivate
            }
            catch (Exception e)
            {
                BSLog.Warn("[PATCH] ClashActivate 拦截异常: " + e);
                return true;
            }
        }

        static void SwordsmanRangePostfix(Swordsman __instance, ref float __result)
        {
            try
            {
                if (__instance == null || __instance.agent == null) return;
                if (!_done.Contains(__instance.agent)) return;
                // 长矛攻击距离：原 radius*0.7*1.3≈0.118m 是剑的贴脸距离；改为"矛长 0.6m + 身体半程"≈0.69m。
                __result = __instance.agent.radius * 0.7f + 0.6f;
            }
            catch { }
        }

        static bool SwordsmanGetAttackPrefix(Swordsman __instance, Agent target, ref Attack __result)
        {
            try
            {
                if (__instance == null || __instance.agent == null || target == null) return true;
                if (!_done.Contains(__instance.agent)) return true;

                Vector3 dir = (target.chestPos - __instance.agent.chestPos).normalized;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.001f) dir = __instance.transform.forward;

                var settings = new AttackSettings
                {
                    damage = __instance.damage,
                    knockback = __instance.knockback,
                    launchImpulse = 0f,
                    stun = __instance.stun
                };
                __result = new Attack(settings, dir, (target.wChestPos + __instance.agent.wChestPos) / 2f,
                    __instance, __instance.agent.squad, "Sfx/English/Spear",
                    ScriptableObjectSingleton<PrefabManager>.instance.hitEffect);
                SpearVisual.AimAt(__instance.agent, target.chestPos);   // 视觉：长矛刺向目标（盖过挥剑动画）
                BSLog.Info("[近战] GetAttack target=" + target.name + " dmg=" + settings.damage.ToString("F1") +
                    " kb=" + settings.knockback.ToString("F1") + " stun=" + settings.stun.ToString("F1") +
                    " sound=Spear dist=" + Vector3.Distance(__instance.agent.transform.position, target.transform.position).ToString("F2"));
                return false; // 跳过原版剑击
            }
            catch (Exception e)
            {
                BSLog.Warn("[PATCH] 长矛攻击改写异常: " + e);
                return true;
            }
        }

        // 长矛穿刺：跳过原版挥剑动画（观感是"用长矛执行剑的劈砍"），矛的刺出/收回/命中由
        // SpearChargeComponent.UpdateMeleeThrust 驱动。
        static readonly FieldInfo StaminaField = AccessTools.Field(typeof(Swordsman), "stamina");
        static readonly FieldInfo StaminaCostField = AccessTools.Field(typeof(Swordsman), "attackStaminaCost");

        static bool SwordsmanAttackPrefix(Swordsman __instance, Agent targetAgent, ref bool __result)
        {
            try
            {
                if (__instance == null || __instance.agent == null || targetAgent == null) return true;
                if (!_done.Contains(__instance.agent)) return true;

                // 复制原版 Attack() 的存活/距离检查（去掉 body.hopping 前置——着地即可攻击）
                if (!__instance.agent.aliveAndGrounded.active) { __result = false; return false; }
                float num = __instance.agent.radius + targetAgent.radius + __instance.range;
                if ((targetAgent.navPos.pos - __instance.agent.navPos.pos).sqrMagnitude > num * num)
                { __result = false; return false; }

                __instance.attack.SetActive(true);
                __instance.target = targetAgent;
                IslandGameplayManager.RequestCombatAudio(__instance.swingSound, __instance.gameObject);
                // 保留原版：目标为剑盾兵时触发格挡
                var enemySw = targetAgent.brain as Swordsman;
                if (enemySw != null && enemySw.shield != null) enemySw.shield.MaybeParry(__instance);
                SpearChargeComponent.NotifyMeleeAttackStart(__instance.agent);
                // 拦截证据：本行出现 = Attack 前缀确实在跑、原版挥剑确实被跳过
                // （range 应为 0.69 而非原版 0.09 —— 同时是 range Patch 的活体探针）
                BSLog.Info("[近战·拦截] Attack() 已接管 target=" + targetAgent.name +
                    " range=" + __instance.range.ToString("F2") + " → 跳过原版挥剑/跳扑");
                __result = true;
                return false;   // 跳过原版：不播挥剑动画
            }
            catch (Exception e)
            {
                BSLog.Warn("[PATCH] 长矛穿刺 Attack 改写异常: " + e);
                return true;
            }
        }

        static bool SwordsmanAttackUpdatePrefix(Swordsman __instance)
        {
            try
            {
                if (__instance == null || __instance.agent == null) return true;
                if (!_done.Contains(__instance.agent)) return true;

                // 接管证据：每个黑矛兵第一次进入 AttackUpdate 时打一行（证明前缀在跑）
                if (_attackUpdateSeen.Add(__instance.agent))
                    BSLog.Info("[近战·接管] Swordsman.AttackUpdate 已接管（黑矛兵 #" + _attackUpdateSeen.Count + "）——攻击结束改由矛刺周期判定");

                // 结束判定：动画播完（穿刺不播动画，恒 false）或 矛刺周期完成
                bool animDone = __instance.agent.animationDone;
                bool meleeDone = SpearChargeComponent.MeleeAttackDone(__instance.agent);
                if (animDone || meleeDone)
                {
                    float stam = (float)StaminaField.GetValue(__instance);
                    float cost = (float)StaminaCostField.GetValue(__instance);
                    stam -= cost;
                    StaminaField.SetValue(__instance, stam);
                    __instance.attack.SetActive(false);
                    SpearChargeComponent.NotifyMeleeAttackEnd(__instance.agent);
                    if (stam > 0f && __instance.agent.enemyAgent != null)
                    {
                        // 连刺：不调用原版 Attack()（其签名含 System.Func<,>，Unity Mono 类型加载雷区），
                        // 直接重启攻击状态 + 矛刺周期。
                        __instance.target = __instance.agent.enemyAgent;
                        __instance.attack.SetActive(true);
                        SpearChargeComponent.NotifyMeleeAttackStart(__instance.agent);
                    }
                    return false;
                }

                // 攻击中站桩刺击：面向锁定突刺方向（不再每帧追目标当前位置 → 消除"小的抽动"），
                // 锁方向无效时退回面向目标当前位置。
                __instance.agent.walkDir = Vector3.zero;
                var stabCharge = __instance.agent.GetComponent<SpearChargeComponent>();
                if (stabCharge != null && stabCharge.IsThrustDirectionLocked)
                    __instance.agent.SetDirection(stabCharge.LockedThrustDirection);
                else if (__instance.target != null && __instance.target.aliveAndGrounded.active)
                    __instance.agent.SetDirection(__instance.target.transform.position - __instance.transform.position);
                return false;
            }
            catch (Exception e)
            {
                BSLog.Warn("[PATCH] 长矛穿刺 AttackUpdate 改写异常: " + e);
                return true;
            }
        }

        static void LevelNodeSetupPostfix(LevelNode __instance)
        {
            // 兜底：若此前尚未注册成功（dict 在菜单阶段为空），在这里重试——此时 dict 必定已填充
            if (_blackSpearman == null && Instance != null)
                Instance.EnsureBlackSpearmanRegistered();

            if (_blackSpearman == null) return;
            if (__instance.enemies == null) return;
            if (__instance.enemies.Contains(_blackSpearman)) return;
            if (UnityEngine.Random.value > ModConfig.SpawnChance.Value)
            {
                BSLog.Info("[CAMPAIGN] 本关未抽中新单位（SpawnChance 未命中）");
                return;
            }

            if (ModConfig.ForceFirstWave.Value && __instance.enemies.Count > 0)
                __instance.enemies.Insert(0, _blackSpearman);
            else
                __instance.enemies.Add(_blackSpearman);

            BSLog.Info($"[CAMPAIGN] 已将 {ModConfig.NewVikingName.Value} 加入本关敌人生成池 (count={__instance.enemies.Count})");
            DiagnosticsComponent.DumpEnemies(__instance.enemies, "CAMPAIGN·注入后");
        }

        static Longship OnLandingSpawn(
            On.Voxels.TowerDefense.RaidGeneration.Landing.orig_Spawn orig, Landing self)
        {
            ComputeRoleQueue(self);   // 前缀：按本船混编 ShipLoad 计算 盾:矛:弓 角色队列
            var ship = orig(self);
            _roleQueue.Clear(); _roleCursor = 0; _lastRole = MixedRoleType.None; _inMixedLanding = false;   // 防御：orig 后清空（补丁立即失效）
            if (ship == null || ship.agents == null || _blackSpearman == null) return ship;

            // 本船角色统计（[混编] 关键词，验证生成比例）+ 收集混编 agent（供阵型）
            int nShield = 0, nSpear = 0, nArcher = 0;
            List<Agent> mixedAgents = null;
            foreach (var a in ship.agents)
            {
                if (a == null) continue;
                var r = a.GetComponent<MixedRole>();
                if (r == null) continue;
                if (r.role == MixedRoleType.Shield) nShield++;
                else if (r.role == MixedRoleType.Spear) nSpear++;
                else if (r.role == MixedRoleType.Archer) nArcher++;
                if (mixedAgents == null) mixedAgents = new List<Agent>();
                mixedAgents.Add(a);
            }
            if (nShield + nSpear + nArcher > 0)
            {
                BSLog.Info($"[混编] 本船角色统计 盾={nShield} 矛={nSpear} 弓={nArcher}（共 {nShield + nSpear + nArcher}）");
                // M2：给混编船挂战术分层站位（盾前/矛中/弓后 + 抵阵等敌）
                if (ModConfig.EnableFormation != null && ModConfig.EnableFormation.Value && mixedAgents != null && mixedAgents.Count > 0)
                {
                    var tf = ship.gameObject.GetComponent<TacticalFormation>();
                    if (tf == null) tf = ship.gameObject.AddComponent<TacticalFormation>();
                    if (tf != null) tf.Setup(ship, mixedAgents);
                }
            }

            int applied = 0;
            Agent first = null;
            foreach (var a in ship.agents)
            {
                if (a == null) continue;
                var va = a.GetComponent<VikingAgent>();
                if (va == null || va.vikingReference != _blackSpearman) continue;
                // 只有长矛角色应用黑矛兵全套（盾/弓保持原版行为，直接复用）
                var role = a.GetComponent<MixedRole>();
                if (role == null || role.role != MixedRoleType.Spear) continue;
                if (!_done.Add(a)) continue;
                ApplyToAgent(a);
                applied++;
                if (first == null) first = a;
            }

            if (applied > 0)
            {
                BSLog.Section("SPAWN 混编·长矛");
                BSLog.Info($"[SPAWN] 本艘敌舰混编长矛 {applied} 个（累计 {_done.Count}）");
                if (first != null && BSLog.VerboseDumps)
                    DiagnosticsComponent.DumpAgent(first, "SPAWN·混编长矛首例");
            }
            return ship;
        }

        static void ApplyToAgent(Agent a)
        {
            // 长矛挂持剑锚点开关（在 Apply 前设置，MountSpear 读取）
            BlackSpearmanWeapon.MountSpearToHand = ModConfig.SpearMountToHand.Value;
            if (ModConfig.EnableRecolor.Value)
            {
                var vis = a.gameObject.AddComponent<BlackSpearmanVisual>();
                if (vis != null) vis.ApplyOnce(a);
            }
            if (ModConfig.EnableWeaponSwap.Value)
                BlackSpearmanWeapon.Apply(a);
            if (Mathf.Abs(ModConfig.ScaleMult.Value - 1f) > 0.0001f)
                a.scale *= ModConfig.ScaleMult.Value;

            var sw = a.GetComponent<Swordsman>();
            if (sw != null)
            {
                ScaleArr(sw.damageLevels, ModConfig.DamageMult.Value);
                ScaleArr(sw.knockbackLevels, ModConfig.KnockbackMult.Value);
                ScaleArr(sw.stunLevels, ModConfig.StunMult.Value);
                // 静默基底挥剑音效：Swordsman.Attack() 会播 swingSound("Sfx/English/Sword/Swing")，
                // 这是"剑劈砍特效"的听觉部分。换成长矛挥击音效——原版 Spear.swingSound = "Sfx/English/Spear/Swing"
                // （真实叶子事件）；旧值 "Sfx/English/Spear" 只是命中前缀，不可直接播放 → 静音。
                try { sw.swingSound = "Sfx/English/Spear/Swing"; } catch { }
                BSLog.Info($"[AGENT] 黑矛兵 {a.name} 攻击范围 range={sw.range.ToString("F2")} radius={a.radius.ToString("F2")} dmg={sw.damage.ToString("F1")} kb={sw.knockback.ToString("F1")}");
            }

            if (ModConfig.EnableCharge.Value)
            {
                var ch = a.gameObject.AddComponent<SpearChargeComponent>();
                if (ch != null) ch.Setup(a);
                RegisterBrainAction(a, ch);
            }
            // 去剑组件：无论 ModConfig.RemoveSword 开关都挂载（用于运行时诊断输出），擦除动作按开关执行
            var remover = a.gameObject.AddComponent<SwordRemover>();
            if (remover != null) remover.Setup(a, ModConfig.RemoveSword.Value);
            // 移除逐帧抽动探针（TwitchProbe）——其"[抽动]⑥精灵帧闪动"口径多次误报刷屏，
            // 且橡皮筋/闪烁根因已由 LateUpdate 硬同步 + 同帧采样根治，不再需要常驻诊断。诊断可 F8 手动转储。
            // sprite2(部件贴图)处理模式（0/1/2 语义见 ModConfig.RemoveSwordSprite2Mode 说明）
            SwordRemover.Sprite2Mode = ModConfig.RemoveSwordSprite2Mode.Value;
            // UV 感知亮采样擦除（白框根治）+ 光晕（吃持剑的手）：模式0下按"帧 UV→部件采样"判定白框像素
            SwordRemover.UVErase = ModConfig.RemoveSwordFrameUVErase.Value;
            SwordRemover.UVHalo = ModConfig.RemoveSwordFrameUVHalo.Value;
            // 剑柄改色（RecolorGripToBody/GripFloodPx）已删除——剑柄改色会误涂肩甲/胸甲/头盔同色像素，
            // 且顶点色 B 恒 0.02 时剑柄本就是黑色剪影。对应 cfg 键 RemoveSwordSprite2GripBand 已从 ModConfig 移除。
            // 针对性诊断探针——死亡腾空轨迹 / 静态尸体烘焙钩子 / 受击两重分身探测
            var probe = a.gameObject.AddComponent<BlackSpearmanDiagProbe>();
            if (probe != null) probe.Setup(a);
        }

        static void RegisterBrainAction(Agent a, IBrainAction action)
        {
            var brain = a != null ? a.brain : null;
            if (brain == null || action == null) return;
            if (!brain.actions.Contains(action)) brain.actions.Add(action);
        }

        static void ScaleArr(float[] arr, float m)
        {
            if (arr == null || Mathf.Abs(m - 1f) < 0.0001f) return;
            for (int i = 0; i < arr.Length; i++) arr[i] *= m;
        }
    }
}
