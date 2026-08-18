using BepInEx.Configuration;

namespace BadNorthMixedSquad1_0
{
    /// <summary>
    /// 全部 cfg 配置收进本静态类（原 Plugin 上的 public static ConfigEntry 字段），
    /// Bind 按 cfg 段拆成小方法，Plugin.Awake 只调一次 <see cref="Bind"/>。
    /// 注意：RemoveSwordSprite2GripBand（剑柄改色）已随 RecolorGripToBody/GripFloodPx 删除，不再绑定。
    /// </summary>
    public static class ModConfig
    {
        // ============ General ============
        public static ConfigEntry<string> SourceVikingName;
        public static ConfigEntry<string> NewVikingName;
        public static ConfigEntry<int> Bounty;
        // 混编比例（每 9 人配额，M1 生成用；盾:矛:弓 默认 4:3:2）
        public static ConfigEntry<int> MixedShieldPer9;
        public static ConfigEntry<int> MixedSpearPer9;
        public static ConfigEntry<int> MixedArcherPer9;
        // M2 战术分层站位开关
        public static ConfigEntry<bool> EnableFormation;

        // ============ Spawn ============
        public static ConfigEntry<float> SpawnChance;
        public static ConfigEntry<bool> ForceFirstWave;

        // ============ Combat ============
        public static ConfigEntry<float> DamageMult;
        public static ConfigEntry<float> KnockbackMult;
        public static ConfigEntry<float> StunMult;
        public static ConfigEntry<float> ScaleMult;

        // ============ Visual ============
        public static ConfigEntry<bool> EnableRecolor;
        public static ConfigEntry<bool> EnableWeaponSwap;
        public static ConfigEntry<bool> RemoveSword;
        public static ConfigEntry<int> RemoveSwordSprite2Mode;
        public static ConfigEntry<bool> RemoveSwordFrameUVErase;
        public static ConfigEntry<int> RemoveSwordFrameUVHalo;
        public static ConfigEntry<bool> SpearMountToHand;
        // 第三只手整改（2026-08-18）：长矛精灵手部处理模式 + 握持点钉死
        public static ConfigEntry<int> SpearHandMode;
        public static ConfigEntry<bool> GripPinToHand;
        public static ConfigEntry<float> SpearGripOffset;

        // ============ Skills ============
        public static ConfigEntry<bool> EnableCharge;
        public static ConfigEntry<bool> EnableShield;

        // ============ Diag ============
        public static ConfigEntry<bool> DiagVerboseDumps;
        public static ConfigEntry<bool> DiagHeadTrace;
        public static ConfigEntry<bool> DiagDeathTrace;
        public static ConfigEntry<bool> DiagHitDoubleTrace;
        // 0=默认（保留 2主+2镜像 全部身体渲染器，游戏原样）；
        // 1=Setup 时禁用 2 个 _MIRROR_ON 镜像渲染器（ColoredCharacter 是 Cull Off，主身永远绘制，禁镜像不破朝向）→ 消除镜像重影；
        // 2=保留动画主身、禁用静态主身+2镜像（定稿）→ 无重影且保留待机律动。
        public static ConfigEntry<int> DiagSingleBodyMode;

        /// <summary>盾牌是否被用户完全移除（EnableShield=false）。三处重复判定的统一入口。</summary>
        public static bool ShieldFullyRemoved => EnableShield != null && !EnableShield.Value;

        public static void Bind(ConfigFile cfg)
        {
            BindGeneral(cfg);
            BindSpawn(cfg);
            BindCombat(cfg);
            BindVisual(cfg);
            BindSkills(cfg);
            BindDiag(cfg);
        }

        static void BindGeneral(ConfigFile cfg)
        {
            SourceVikingName = cfg.Bind("General", "SourceVikingName", "Viking_SwordShield",
                "借用其 VikingAgent 预制体作为视觉/行为模板（仅借用引用，不克隆整个 VikingReference）。\n" +
                "v1.3 基底：Viking_SwordShield（保留其真实盾牌美术，仅移除剑视觉并挂长矛）。\n" +
                "⚠️ 如果旧 cfg 里有其它值会覆盖此默认值，请删除或更新 cfg。");
            NewVikingName = cfg.Bind("General", "NewVikingName", "Viking_MixedSquad",
                "新单位在敌人生成池中的名字。");
            Bounty = cfg.Bind("General", "Bounty", 12,
                "赏金（决定该单位占用的敌舰配额）。混编小队 9 人（盾4矛3弓2），比单兵种高。");
            MixedShieldPer9 = cfg.Bind("General", "MixedShieldPer9", 4,
                "混编每 9 人盾兵数（占前排，原版剑盾兵）。");
            MixedSpearPer9 = cfg.Bind("General", "MixedSpearPer9", 3,
                "混编每 9 人长矛兵数（黑矛兵，冲锋/刺击）。");
            MixedArcherPer9 = cfg.Bind("General", "MixedArcherPer9", 2,
                "混编每 9 人弓手数（原版弓手，后排）。");
            EnableFormation = cfg.Bind("General", "EnableFormation", true,
                "M2 战术分层站位：盾前/矛中/弓后三列 + 抵阵等敌（敌入范围才交战）。false=退化为原版行为（各自冲建筑）。");
        }

        static void BindSpawn(ConfigFile cfg)
        {
            SpawnChance = cfg.Bind("Spawn", "SpawnChance", 0.7f,
                "每关把新单位加入敌人生成池的概率 (0~1)。");
            ForceFirstWave = cfg.Bind("Spawn", "ForceFirstWave", false,
                "是否强制在第一波出现（便于测试）。");
        }

        static void BindCombat(ConfigFile cfg)
        {
            DamageMult = cfg.Bind("Combat", "DamageMult", 1.6f, "伤害倍率。");
            KnockbackMult = cfg.Bind("Combat", "KnockbackMult", 2.5f, "击退倍率。");
            StunMult = cfg.Bind("Combat", "StunMult", 1.2f, "眩晕倍率。");
            ScaleMult = cfg.Bind("Combat", "ScaleMult", 1.05f, "体型倍率。");
        }

        static void BindVisual(ConfigFile cfg)
        {
            EnableRecolor = cfg.Bind("Visual", "EnableRecolor", true, "是否把新单位染成黑色。");
            EnableWeaponSwap = cfg.Bind("Visual", "EnableWeaponSwap", true, "是否移除剑盾并复用我方长矛（混搭武器）。");
            RemoveSword = cfg.Bind("Visual", "RemoveSword", false,
                "是否移除烘焙在身体动画帧（OnehandedXXXX/SwordsmanXXXX）里的剑（默认关闭：颜色签名需先用日志诊断校准，" +
                "直接开启会误擦身体暗红衣物导致身体透明）。");
            RemoveSwordSprite2Mode = cfg.Bind("Visual", "RemoveSwordSprite2Mode", 2,
                "sprite2(部件贴图)处理模式——新基底 PartTex_SwordShield 的剑/盾/身体都在部件贴图里（剑盾=亮银亮色、身体=暗色）。\n" +
                "0=保留原部件贴图、只靠帧擦除去剑（剑盾亮色会经帧 UV 采样残留成白框，弃用）；\n" +
                "1=整块清空部件单元（身体一起消失会变白框，勿用）；\n" +
                "2=定稿：分区压暗（亮银×0.15 防剑/盾显形、暗灰×0.8 保留头盔/肩甲、躯干/手/脸烘黑）。");
            RemoveSwordFrameUVErase = cfg.Bind("Visual", "RemoveSwordFrameUVErase", true,
                "帧擦除是否启用\"UV 感知亮采样擦除\"（白框根治）：任何帧像素的 R/G UV 解码采样到\n" +
                "亮银部件像素(>150)都一并擦除——运行时 ETC2 压缩让部件贴图局部变亮，身体帧像素采样到亮像素 = 白框，\n" +
                "红暗阈值抓不到它们，只有按 UV 采样判定才抓得到。默认 true（暗身体像素采样暗部件像素，不受影响）。");
            RemoveSwordFrameUVHalo = cfg.Bind("Visual", "RemoveSwordFrameUVHalo", 0,
                "UV 亮像素光晕（0~6，部件像素距离）：>0 时把\"解码 UV 落在距亮部件像素 ≤N 部件像素\"的帧像素也擦除，\n" +
                "用于连持剑的手/护手/剑刃边缘一起删。默认 0（只擦纯亮像素=白框）；若手/剑柄仍可见，逐步加大 1→2→3。");
            SpearMountToHand = cfg.Bind("Visual", "SpearMountToHand", true,
                "长矛是否挂到持剑锚点（基底 Weapon 骨=原本持剑的手）。旧固定偏移 (0, 半径*1.4, 半径*0.6)\n" +
                "让矛根悬在身体正中、与持剑手（偏离身体中心 ~0.2m）错位 → 观感\"持矛手脱离身躯、攻击范围异常大\"。\n" +
                "true=矛根贴到 Weapon 锚点（手位）；false=旧固定偏移。");
            SpearHandMode = cfg.Bind("Visual", "SpearHandMode", 2,
                "长矛精灵皮肤（第三只手根治，PNG 整体换精灵，维京长矛同款机制）：\n" +
                "0=关——使用原版英军长矛（含英军臂，验证用）；\n" +
                "2=★PNG 皮肤——换成内嵌 spear_skin_0/1/2.png（离线按精确像素规则把英军臂改成身体色，ETC2 免疫、零运行期像素处理）。\n" +
                "可在插件目录放同名 PNG 覆盖（热替换免重编译）。");
            GripPinToHand = cfg.Bind("Visual", "GripPinToHand", false,
                "握持点钉死（可选增强）：矛根每帧 = 手 − 旋转×gripOffset，让握持点恒定落在维京拳上，\n" +
                "瞄准旋转时矛绕手握动（真实持矛感）。默认 false（模式2改身体色下无洞需对齐，非必需）。");
            SpearGripOffset = cfg.Bind("Visual", "SpearGripOffset", 0f,
                "握持点偏移微调（米）：在\"-0.309×矛长(0.6m)=-0.185m\"基准之上叠加（矛头方向为正）。\n" +
                "GripPinToHand 用它把握持点对齐维京拳；默认 0，游戏内 [像素采样] 校准后调整。");
        }

        static void BindSkills(ConfigFile cfg)
        {
            EnableCharge = cfg.Bind("Skills", "EnableCharge", true, "是否注入冲锋技能。");
            EnableShield = cfg.Bind("Skills", "EnableShield", false,
                "盾牌完全移除开关。true=保留基底剑盾兵盾牌并具备格挡效果（近战正面格挡、箭矢/飞斧减伤弹开）；\n" +
                "false=完全移除盾牌（效果+美术均不挂载，盾牌子对象禁用）——用户指定黑矛兵不带盾。" +
                "默认 false。");
        }

        static void BindDiag(ConfigFile cfg)
        {
            // 诊断开关：本 Mod 的日志目标从"什么都打"改为"按问题分类可开关"。
            // P0 头部闪白/抽搐 → DiagHeadTrace（头盔逐帧采样+帧擦除追踪）；
            // P1 死亡腾空影分身/受击两重分身 → DiagDeathTrace + DiagHitDoubleTrace（腾空轨迹+尸体烘焙钩子+镜像复启用探测）。
            DiagVerboseDumps = cfg.Bind("Diag", "VerboseDumps", false,
                "巨型转储开关（默认关，配合 F8 手动完整诊断用）：去剑 ASCII 像素图 / transform 层级 / SPAWN 首例完整 dump / [头部采样] 逐帧行。\n" +
                "开启会刷屏（每黑矛兵几 KB），平时保持 false；需要贴细节日志时再开。");
            DiagHeadTrace = cfg.Bind("Diag", "HeadTrace", true,
                "头部采样追踪（问题①头部闪白/抽搐）：帧末采盔顶实际渲染色 + 窗口统计 [头盔统计]。\n" +
                "采样点已修正为 Sprite 真实盔顶（旧 chestPos+up*0.45 常落空采到背景）。窗口含\"暗↔亮交替\"计数=闪白实锤。");
            DiagDeathTrace = cfg.Bind("Diag", "DeathTrace", true,
                "死亡腾空紧凑追踪（问题②死亡影分身/腾空分裂）：死亡→落地每 ~5 帧打一行身体位置/网格/块状态；\n" +
                "另钩住 CorpseManager.AddCorpse（静态尸体烘焙时刻），与飞行身体对比偏移=双尸证据。");
            DiagHitDoubleTrace = cfg.Bind("Diag", "HitDoubleTrace", true,
                "受击两重分身探针（问题②受击双影）：探测 _MIRROR_ON 镜像渲染器被游戏重新启用 / 两个身体 MeshRenderer 离位 /\n" +
                "BodySprite 的 SpriteRenderer(原始帧)与 MeshRenderer(黑色克隆) 同时启用=双重渲染。");
            DiagSingleBodyMode = cfg.Bind("Diag", "SingleBodyMode", 2,
                "身体渲染器去重影模式。\n" +
                "0=默认（保留 2主+2镜像，游戏原样，重影存在）；\n" +
                "1=禁用 2 个 _MIRROR_ON 镜像渲染器（ColoredCharacter 着色器是 Cull Off，主身永远绘制，禁镜像不会让背对时角色消失）；\n" +
                "2=默认：只保留 1 个身体渲染器——**保留动画主身（UV 随帧更新=律动源），禁用静态主身+2镜像**\n" +
                "  → 无重影且保留待机律动（前主身 UV 恒 0.152=Swordsman0001 静态帧，后主身随帧变化=动画源）。\n" +
                "改完重启游戏生效。若发现身体局部消失/朝向异常，改回 1 或 0。");
        }
    }
}
