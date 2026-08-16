# BadNorthBlackSpearman v1.3 —— 特质 Mod 式注入（新思路）

> 为《Bad North》添加**黑色长矛手（Black Spearman）**敌方单位。
> **本版本彻底改变思路**：不再克隆（`Instantiate`）现有预制件，而是像特质 Mod 一样，
> 用 **MMHOOK + Harmony 注入**，**新建**一个 `VikingReference`，配好美术资源，加入**敌人生成池**。

---
## 🏗 当前状态（2026-08-16，第二十八轮·闪白根因实锤与修复）

- **✅ 已定稿**：技能触发/表现/伤害（登岛触发 + 长矛突击 + 可躲高收益）、盾牌格挡、技能期可被击杀、10s 冷却。
- **🔴 闪白根因实锤（本轮定案）**：
  - **`[像素采样]` 在登岛交战时全是浅色**（亮度 0.5~0.9，绿/蓝灰）—— **身体是透明的**，采到的是身后岛屿/海水；
  - `[VISUAL] BodySprite color=(0,0.25,0,0)` **顶点色 A=0.00 恒定** —— `Body.SetGrass` 只在 `grass != shadow.a` 时写颜色，
    黑矛兵 shadow.a=0 时判定短路 → **身体 alpha 恒 0 → AlphaToMask 把身体整块丢弃 → 身体透明露背景 = "闪白"**（背景不是纯白而是浅绿/蓝灰）。
  - `[死亡分裂]` 转储：死亡瞬间 4 渲染器材质块为空（`_MainTex=`/`_PartTex=` 空 → 白尸）+ 两对网格 UV 不同（0.152,0.384 vs 0.339,0.724 → 分裂/重影）。
- **🔧 修复**：`BlackSpearmanVisual` **强制身体顶点 alpha=1**（不透明）——黑身不再被丢弃、不再露背景；
  `[渲染诊断⚠️]` 新增 **alpha<0.9 异常判定**（下次日志若不再出现 `⚠️顶点alpha` 即根治确认）。
- **📋 待实测**：① 身体应为**不透明黑**（`[像素采样]` 亮度应降到 ≈0.05 以下）；② 无 `[渲染诊断⚠️] alpha` 异常；③ 死亡"分裂/白尸"是否缓解。



## ✅ 技能效果实现成功（2026-08-14 定稿）

> **登岛触发 + 长矛突击表现 + 可躲高收益** —— 目标全部达成。

**技能机制（Twohanded 触发 × 我方 Pike Charge 表现 的合体）：**

| 维度 | 实现 |
|---|---|
| 触发逻辑 | 登岛（`navPos.onMain`）后**优先跟随 Swordsman 大脑锁定目标**（Twohanded 式），路径朝目标导航格；大脑无目标才退回 6m 扫描兜底 |
| 技能表现 | 0.5s 举矛前摇 → **非追踪**直线冲锋（5m/s、穿透锁定格 1.5m）→ 后退 0.6m 迎击 → **10s 冷却** |
| 攻击效果 | 沿途**单矛线宽 0.5m** 扫过一排（能量逐击递减 ×0.8）→ **终点爆发**（伤害×0.3、击退+2、撞飞）—— 沿途推散、终点撞飞 |
| 可躲高收益 | 冲锋**非追踪 + 前摇可见 + 线宽 0.5m**：横向拉开即可躲过；躲过则黑矛兵冲空陷入长后摇 |
| 战术价值 | 逼迫我方放弃优先占据的窄道/少接战面地形、转入可进可退的开阔地，或阻碍单位转场——与不同兵种同场可碰撞出火花 |

**本轮关键修复（对照 `BadNorthDatabase-main` 原版源码逐条落地）：**
1. 触发改由**大脑目标**驱动（修复"走路到阵前不冲锋"）；
2. 命中改用 `AgentEnumerators` + **视觉长矛锚点**（修复冲锋时 transform 滞后 navPos 约 1m 导致的"隔空命中"）；
3. **抵达爆发**对终点周围所有存活单位生效（还原原版"最后一撞"）；
4. 协同防重**软降级**（大脑目标也参与防重，避免多个黑矛兵挤同一目标）；
5. 参数定稿：`CooldownTime=10s`（2026-08-15 用户延长，冲锋更稀有、更像"技能"）、`HitRadius=0.5m`、`ArrivalBurstRadius=1.2m`。

---

## 与历史版本的差异

| 版本 | 策略 | 问题 | 状态 |
|------|------|------|:---:|
| v1.0 | 运行时新建 VikingReference + 反射 | 时序问题（dict 在 Awake 时为空） | 🔒 封存 |
| v1.1 | BBB 风格劫持 `Viking_SwordShield` | SwordShield 消失 | 🔒 封存 |
| v1.2 | `Instantiate(源VR)` 克隆 + Enum/Asset 外部工具链 | 克隆脏状态、外部工具链复杂 | 🔒 封存 |
| **v1.3** | **新建 VikingReference（非克隆）+ 注入敌人生成池 + 特质式美术资源** | ✅ 定稿 | 🏆 当前 |

## 核心思路（源码验证）

敌人完整生成链路：

```
GameSetup.Awake()                          ← MMHOOK ① 在这里新建并注册
  └─ VikingReference.OnGameAwake()
       └─ LevelStateObjectReferences.dict[name] = this    ← 敌人生成池注册表

战役生成：
  LevelNode.Setup(levelState)              ← Harmony ② 在这里加入每关 enemies
       └─ levelState.GetReferencedObjects(this.enemies)    ← 真正的“敌人生成池”

实际生成：
  Raid → possibleAgents = levelNode.enemies → ShipLoad.vikingRef
  Landing.Spawn()                          ← MMHOOK ③ 在这里施加外观/数值/技能
       └─ squad.CreateAgent(vikingRef.agent)
```

**本版本做以下事情：**

1. **新建 + 注册**（`GameSetup.Awake`，MMHOOK）：
   `new GameObject` + `AddComponent<VikingReference>`（**不 `Instantiate` 克隆**），
   反射配置私有字段（`viking` 仅借用 `Viking_SwordShield` 的 `VikingAgent` 预制体引用），
   注册进 `LevelStateObjectReferences.dict`。

2. **加入敌人生成池**（`LevelNode.Setup`，Harmony postfix）：
   把新单位 append 到每关的 `enemies` 列表，使其成为 `Raid.possibleAgents` 的候选。

3. **生成时表现**（`Landing.Spawn`，MMHOOK）：
   对新单位施加黑色外观（`BatchedSprite` B 通道）、数值强化（伤害/击退/眩晕/体型）、
   冲锋（`SpearChargeComponent`）技能；近战攻击由 `Swordsman.Attack` 穿刺补丁接管。

4. **美术资源**（特质 Mod 风格）：
   从插件目录 `Resources/black_spearman_icon.png` 加载图标（无 PNG 时回退程序化图标），
   并注册 I2 本地化名称/描述。

5. **武器混搭**（复用我方素材）：
   移除基底剑视觉（`agent.shield=false` + 禁用 `Shield` 组件 + 按名称禁用剑/武器子对象），
   **保留剑盾兵基底的盾牌美术**，并从我方 Pikeman（`Spear.spearAnim` 的 `BatchedSprite`）克隆长矛挂到黑矛兵身上——
   "剑盾兵的身子 + 保留盾牌 + 我方的矛 + 染黑"。

> ⚠️ **关于枚举**：运行时无法新增 `VikingAgent.Type` 枚举值。本版本**复用 `SwordShield` 枚举值**
> （获得近战大脑行为），用 **VikingReference 的名字 + 引用**区分新单位，因此不需要 v1.2 的
> Mono.Cecil 枚举补丁，也不需要 UnityPy 资源补丁——更简单、更稳定。

---

## 安装要求

- 《Bad North》（Steam 版）
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx)
- `MMHOOK-Assembly-CSharp.dll`（放入 `BepInEx/plugins/`，来自 BadNorthDatabase-main）

## 编译

1. 修改 `BadNorthBlackSpearman1.3.csproj` 中游戏 DLL 的 `HintPath`（默认 `D:\Steam\steamapps\common\BadNorth\...`）。
2. 编译：

```bash
dotnet build BadNorthBlackSpearman1.3.csproj -c Release
```

3. 将 `bin/Release/net472/BadNorthBlackSpearman1.3.dll` 复制到 `BepInEx/plugins/`。
4. 将 `Resources/black_spearman_icon.png` 复制到插件 DLL 同目录（可选，缺失时会自动生成占位图标）。

> ⚠️ **.NET 3.5 运行时兼容（重要）**：
> 本游戏是 Unity 2018 的老 Mono 运行时（CLR 2.0 ≈ .NET 3.5），而本项目必须编译为 `net472` 才能引用游戏/BepInEx/MMHOOK 的 DLL。
> 因此代码里**只能使用 .NET 3.5 就有的 API**，下面这些 .NET 4.x 专属写法会运行时崩溃，务必避免：
> - `string.Join(string, IEnumerable<string>)`（改用 `BSLog.Join(...)`）
> - `MethodInfo` / `FieldInfo` / `PropertyInfo` 的 `==` / `!=`（改用 `ReferenceEquals(x, null)`）
> - `lock` 语句（会编译成 `Monitor.Enter(obj, ref bool)`）
> - 三参数 `Path.Combine(a, b, c)`
> - LINQ（`ToArray`/`Where`/`Select`…）谨慎使用

## 配置（BepInEx 配置）

| 分组 | 键 | 默认 | 说明 |
|------|----|------|------|
| General | `SourceVikingName` | `Viking_SwordShield` | 借用 VikingAgent 预制体引用的源单位（默认剑盾兵基底：保留其盾牌美术） |
| General | `NewVikingName` | `Viking_BlackSpearman` | 新单位在生成池中的名字 |
| General | `Bounty` | 8 | 赏金 |
| Spawn | `SpawnChance` | 0.7 | 每关加入生成池概率 |
| Spawn | `ForceFirstWave` | false | 强制第一波出现（便于测试） |
| Combat | `DamageMult` / `KnockbackMult` / `StunMult` / `ScaleMult` | 1.6 / 2.5 / 1.2 / 1.05 | 数值倍率 |
| Visual | `EnableRecolor` | true | 黑色外观 |
| Visual | `EnableWeaponSwap` | true | 移除剑视觉 + 复用我方长矛 | 
| Skills | `EnableCharge` | true | 冲锋 |
| Skills | `EnableShield` | false | ★第十七轮：`false`=**完全移除盾牌**（效果+美术均不挂载，默认）；`true`=保留基底剑盾兵盾牌并具备格挡效果（近战正面归零、箭矢×0.05 弹开、飞斧归零、长矛×0.2） |

---

## 诊断与日志（测试必备）

本方案属首创，故内置了**双通道日志 + 运行时诊断探针**，用于收集比报错日志更多的现场信息。

### 日志去哪了

| 通道 | 位置 | 内容 |
|------|------|------|
| BepInEx 控制台 | 游戏控制台 / `BepInEx/LogOutput.log` | 我们自己的 INFO/WARN/ERROR |
| **独立诊断文件** | 插件 DLL 同目录 `BadNorthBlackSpearman1.3.log` | 我们自己的日志 + **全游戏错误/异常**（含堆栈）+ 所有转储 |

- 通过 `Application.logMessageReceived` 捕获**全游戏**的 Error/Exception/Assert（含堆栈）；
- 通过 `AppDomain.CurrentDomain.UnhandledException` 捕获未处理异常；
- 即使游戏闪退，诊断文件也会保留最后现场。

### 日志阶段标记

按生命周期打标签，方便定位问题发生在哪一步：

| 标记 | 含义 |
|------|------|
| `[BOOT]` | `GameSetup.Awake` 完成，dict 键清单 |
| `[REGISTER]` | 新建并注册 VikingReference、注册后 dict 键 |
| `[ART]` | 美术图标 / 本地化加载结果 |
| `[PATCH]` | Harmony 是否成功 Patch `LevelNode.Setup` |
| `[CAMPAIGN]` | 本关是否命中 SpawnChance、注入后敌人生成池清单 |
| `[SPAWN]` | 每艘敌舰生成的黑矛兵数量 + 首个黑矛兵组件层级转储 |
| `[AGENT]` / `[Charge]` / `[Stab]` | 技能组件日志 |
| `[心跳]` | 每 8 秒：dict 条目、新单位状态、累计处理 Agent 数 |

### 手动完整转储

游戏运行时按 **`F8`**，立即向诊断文件写入一次完整现场：
- 生成池注册表 `LevelStateObjectReferences.dict`（每个键 → 类型/type/bounty/vikingClone/agent）
- 新单位 `VikingReference` 的**所有字段**（含私有 `[SerializeField]`，反射读取）
- 新单位 `vikingClone` 的 GameObject 层级
- 场景中所有 `VikingAgent` 概况（黑矛兵 vs 其他）

---

## 调试成果与待解决困惑（2026-08-14）

> 本轮聚焦“举矛冲锋”的四项验证：① 下船后才触发；② 位移明显变大；③ 判定贴近模型；④ 剑的视觉去除。
> 实测对照日志见插件目录 `BadNorthBlackSpearman1.3.log`。

### ✅ 已定位并修复的根因

**1. 冲锋位移无效（本轮核心修复）**

- 实测：16 次冲锋位移全部仅 **0.08~0.42 m**，且 `[Charge] HIT` 全程 **0 次**（冲锋根本没冲出去）。
- 根因（对照原版源码逐行确认）：
  1. `NavPos` 是**结构体**（`NavPos.cs`），`_agent.navPos.pos = newPos` 是在**临时副本**上写值——静默无效；
  2. Agent 的 `transform` **每帧都由 navPos 同步**：`Agent.FixedUpdateAgent` 里 `wPos = navPos.wPos`，
     `Body` 的 standing/stepping/sliding 状态再据此驱动 `transform.position` —— 所以 `LateUpdate` 里直接写
     `transform.position` 下一帧就被覆盖。
- 修复：照搬原版 `PikeChargeComponent.charge.OnUpdate`（`agent.navPos = current;`）的做法 —— 把 `agent.navPos`
  **整体赋值**为沿冲锋方向推进的新 `NavPos`（先 `MoveTo`，入参为 navmesh 本地坐标；失败则
  `new NavPos(navMesh, worldPos, world:true)` 回退）。
  参数 `ChargeSpeed=4 × ChargingMaxTime=0.5s` → 理论位移 ~2 m。

**2. 敌舰上过早触发冲锋**

- 实测：敌舰刚生成黑矛兵就立刻出现 `WIND-UP`/`冲锋起点`，此时 `navPos=(-0.04, 0.03, -0.31)` 与世界坐标
  `pos=(-7.74, -2.08)` **对不上**；等登上主岛后 `navPos.pos` 才与 `pos` 一致（主岛 navmesh 的 transform 为单位矩阵）。
  → 判据成立：黑矛兵在**敌舰导航网格**上时就已触发冲锋。
- 根因：敌舰上同样有有效 navPos，`aliveAndGrounded` 在船上同样激活，仅靠它拦不住。
- 修复：`MaybeAct` 追加 `navPos.onMain` 拦截（`onMain` = navPos 已在主岛导航网格上，即 `NavigationMesh.island != null`）。
- 诊断配合：冲锋起点日志新增 `onMain=` 字段，回查即可确认是否真的“下船后才触发”。

**3. 默认源单位改为 `Viking_SwordShield`（保留真实盾牌美术）**

- 曾因"盾牌美术不出现"改用 `Viking_Sword`（无盾牌基底 + 运行时克隆盾牌遮挡，克隆体不进入渲染管线 → 看不到盾牌）。
- 现按用户建议改回 `Viking_SwordShield` 基底：**不销毁盾牌**（剥离只删逻辑组件、禁用剑视觉），盾牌随基底正常渲染；
  擦除剑刃的 `SwordRemover` 方案不变。
- 已更新 `BepInEx/config/badnorth.blackspearman.v1.3.cfg` → `SourceVikingName = Viking_SwordShield`（旧 cfg 会覆盖默认值，需同步）。

### 📐 原版参考值（校准用）

| 项 | 值 |
|----|----|
| 原版 Pike Charge | duration≈0.447s、radius=0.732、speed=5、range=20 |
| 举矛公式 | `spearAim.rotation = LookRotation(矛尖方向, 角色right) * Euler(0,0,90)` |
| 旋转层级 | 旋转发生在父骨 `spearAim`，`spearAnim` 局部旋转恒等（`spearAnim.localRot=(0,0,0)`） |
| 我方长矛参考 | `spearAim.localPos=(0,0,0)`，`spearAnim.localPos=(0,-0.033,0.037)` |

### ✅ 待验证 / 待解决困惑 —— 逐项定稿（历史表格保留于下方）

| # | 困惑 | 现状与原因 | 下一步 |
|---|------|-----------|-------|
| 1 | 位移是否真的达到 ~1.5–2 m | navPos 每帧推进 2 m，但 `Body` 踏步动画是“追赶式”，且冲锋时 `maxSpeed=0` 使 stepTime 偏大，视觉位移可能低于 navPos 位移（橡皮筋延迟）。 | 实测新日志 `冲锋结束 位移=`；不足则 `ChargingMaxTime` 0.5→0.6s 或 `ChargeSpeed` 4→5。 |
| 2 | 抬矛速率是否过快 | 黑矛兵 `Slerp` 速率 12/s，玩家方约 4/s；日志显示矛能快速贴到 targetRot，观感偏“瞬举”。 | 观感验证后把 `Time.deltaTime * 12f` 降到 4~6。 |
| 3 | 命中判定是否贴近模型 | 位移修好前 0.4 m 命中半径 + 0.15 s tick 从未命中，无有效数据。 | 位移正常后观察 `[Charge] HIT` 是否近身触发、伤害/击退/眩晕是否符合预期。 |
| 4 | 剑动画是否彻底去除 | 基底换为 `Viking_Sword` 后仍需确认；挥剑可能烘焙在身体动画/纹理中。 | 看 `[WEAPON] 禁用子对象 N 个` 与画面；仍残留则试 `Viking_Berserker`/`Viking_Tank` 基底。 |
| 5 | `onMain` 拦截是否真正生效 | 逻辑成立（敌舰 navmesh 无 island 引用），但需实测确认生成点不再立刻 `WIND-UP`。 | 看新日志“冲锋起点”是否带 `onMain=True`、且敌舰到达后有一段等待。 |
| 6 | 冲锋撞崖/出界行为 | `MoveTo` 失败回退重建 NavPos（贴最近三角形）；遇悬崖/水边时具体表现未实测。 | 挑一个冲锋路径跨崖/水边的关卡实测。 |
| 7 | 单体 vs 整队列阵冲锋 | 当前受“独立单位非小队”限制，冲锋是**单体冲刺**；原版是整队列阵冲锋（含 `positionInFormation` 偏移）。 | 后续可选：升级为队列冲锋。 |

---

**定稿结论（2026-08-14）：**
1. 位移：✅ 实测 2.5~5m（含穿透余量），观感正常。
2. 抬矛速率：🟡 保留 12/s；如需更缓可降到 4~6/s。
3. 命中判定：✅ 改用 `AgentEnumerators` + 视觉长矛锚点 + 线宽 0.5m —— 命中=所见。
4. 剑动画残留：✅ 接受剑影（身体动画帧像素，运行时无法去除），长矛主导视觉。
5. `onMain` 拦截：✅ 实测登岛前反复拦截、登岛后立刻触发。
6. 撞崖/出界：✅ 原版同款 `new NavPos(..., world:true)` 回退。
7. 单体 vs 列阵：🟡 设计决定保持单体冲刺（敌方无 `EnglishFormationAgent`）；列阵冲锋留作后续可选。

---

## 🔬 去剑研究（2026-08-15，✅ 已验证 + 二次修复）

> 目标：移除黑矛兵身体动画帧里烘焙的剑（视觉残留）。攻击逻辑早已是长矛（GetAttack patch），剑只是观感。

### 研究结论

| 项 | 发现 |
|---|---|
| 剑在哪 | 烘焙在 **`OnehandedXXXX` 动画帧**的帧右侧 **x≈29~38**（暗红垂直带，宽阈值 70/40/20 = 201 像素）。之前误认为是 `Swordsman0001`（那是 SwordShield 的帧） |
| 帧纹理 | **共享 2048x1024 精灵图集**（`SpriteAtlasTexture-Sprites`）→ 必须克隆后按帧 rect 擦除 |
| 外观来源 | `sprite2 = PartTex_Sword`（`PartTex_Median_BlurAlpha` 图集 (192,0,64,126) 单元），`SetSprite2` 是安全机制 |
| ⚠️ 真凶 | **`bSprite` 交换会破坏身体渲染（躯干透明）**——即使只擦剑区域、顶点色/UV 全部正常，换 Sprite 对象就坏 |
| ✅ 已验证 | **`_MainTex` 替换方案成功**：游戏内剑刃消失、躯干完好（不透明）。见下方"当前方案" |
| 🔬 二次发现 | **剑柄 = 剑刃 bbox 外侧的非红不透明像素**（灰金属色，不在 70/40/20 阈值内），且**剑到身体左侧的帧（0030~0033、0058~0059）`x≥28` 区域会漏擦** → 需"方向感知擦除" |

### 当前方案（✅ 已验证 + 二次修复）

**不交换 bSprite，只替换材质块 `_MainTex`**：网格 UV 指向图集单元，克隆纹理与图集同尺寸 → `_MainTex` 直接采样克隆的同一单元渲染"去剑帧"，不触发批量渲染重建。

实现：`SwordRemover.EnsureErasedTexture`（共享克隆 + 每帧 rect 擦一次 + 安全阀）+ `block.SetTexture("_MainTex", clone)`。

**擦除规则（方向感知，2026-08-15 二次修复）**：
- ① 整帧 rect 内红暗像素（`R>70,G<40,B<20`）——清除剑刃，**身体左/右侧的剑都能擦**（原 `x≥28` 固定区域在剑到左侧时漏擦）；
- ② 剑刃外侧的非红不透明像素（右偏剑擦 `x≥剑右缘-2`，左偏剑擦 `x≤剑左缘+2`，仅 bbox 上下 ±6px 纵向带）——清除**剑柄/护手**（灰金属色，原阈值漏网）；
- ③ 居中剑（|剑心-帧心|<5px）不擦外侧，避免误擦身体；安全阀 20% 兜底。
- **离线验证**：59 帧全量模拟 → 剑区残留=0、内侧误擦=0、擦除占比最高 15.7%（<20% 安全阀，无帧被跳过）。

**诊断**：`去剑·运行时诊断`（帧像素 ASCII 图 + sprite2 单元 ASCII 图 + 网格状态）+ `[去剑诊断]`（阈值命中/bbox）。

**调参**：`SwordRMin/GMax/BMax=70/40/20`、`OuterBandPx=6`、`OuterMarginPx=2`、`OuterMinOffsetPx=5`、`SafetyEraseRatio=0.2`。

**离线校准脚本**：`analyze_sword.py`（逐帧统计剑刃/剑柄坐标）、`validate_erase.py`（模拟新擦除算法全量验证）。

**🔬 PartTex 探针结论 + sprite2 亮银擦除（2026-08-15 五次进展）**：
- 运行时探针（4 组合 UV 解码采样 `PartTex_Median_BlurAlpha`）确认：**剑=亮银(159~189,144~186,137~189)、身体=暗(33,26,24)** → 剑柄残留走"克隆 sprite2 抹亮银"方案（`SetSprite2` 回写，rect 同尺寸同原点，顶点色编码不变，不伤身体）。
- 已实现：`EraseSilverPixels`（中性亮银阈值）+ `GetErasedSprite2` 亮银擦除（`Sprite2SafetyRatio=35%`）+ 探针新增剑柄采样。构建 0 警告 0 错误，Debug 77824B 已部署。
- 说明：旧 `EraseSwordPixels`（红暗阈值）对 PartTex 永远命中 0 → 之前 sprite2 去剑实际从未生效（日志无 `sprite2 已去剑`）。

**🔬 长矛穿刺（2026-08-15 六次进展）——剑柄去不掉，就换"真·穿刺"攻击**：
- 实测：sprite2 亮银擦除被安全阀拒绝（`2681/6571=40.8%`>35%）；剑柄在 PartTex 里是暗色 `(33,26,24)/(66,48,41)`，与身体同色 → **帧级/颜色级/部件级三路证明剑柄无法靠像素擦除解决**。
- 转方向：**Patch `Swordsman.Attack()`** 让黑矛兵攻击不播挥剑动画（原版播 Onehanded 挥剑帧 = "用长矛执行剑的劈砍"），**Patch `AttackUpdate()`** 用矛刺周期（0.5s）结束攻击；矛刺到位瞬间手动 `FirstHit()` 触发长矛伤害（原版由挥剑动画事件触发）。观感从"跳扑挥砍"改为"站桩端矛戳刺"。
- 副作用：攻击不再播挥剑动画 → 攻击时剑柄不再出现；待机剑柄是否残留待 `[近战·待机帧]` 日志确认。
**🎯 近战刺击收尾 + 盾牌格挡 + 冷却延长（2026-08-15 七次进展，✅ 定稿）**：
- **刺击"小的抽动"修复**（攻击全程零重算、两处同帧锁死）：
  - 攻击开始瞬间 `SetDirection(_thrustDirWorld)` 先把身体 snap 对准突刺方向，再把突刺位移以**本地空间**锁定为 `_thrustOffsetLocal`（`InverseTransformDirection(dir) × ThrustDistance` 只算一次）——旧版每帧重算，身体 SetDirection 阶跃/转向时本地偏移逐帧跳变 = 肉眼可见的抽动；
  - 刺击朝向改用 `LookRotation(dir, cross(up, dir))`（虚拟 right = cross(worldUp, dir)，恒 ⊥ dir、永不退化）——旧版 `LookRotation(dir, agent.right)` 在目标位于角色侧向时 roll 翻转 180°（矛精灵上下颠倒）；agent 正对目标时 `cross(up, dir) ≡ agent.right`，与旧式完全等价 → 观感零变化（⚠️ 首版误写 `cross(dir, up)` 符号反了 = −agent.right，验证日志 `spearWorldRot=(0,X,180)` 与冲锋举矛 `(0,X,0)` 不一致，已修正为 `cross(up, dir)`）；
  - `Plugin.SwordsmanAttackUpdatePrefix` 攻击期间面向改用**锁定突刺方向**（不再每帧追活动目标当前位置）→ 身体朝向恒定、突刺稳定。
- **近战命中对齐我方长矛兵 Spear**（`DoSpearHit` / `TestHit` / `DealSpearDamage`）：矛本地空间球判定（矛尖指向才中）、主目标 ×1 / 副目标 ×0.33 贯穿、`PrefabManager.hitEffect` 命中特效。
- **盾牌 = 剑盾兵真实格挡**（`BlackSpearmanShield.cs`，复刻 `Shield.ModifyAttack`）：近战正面格挡归零、箭矢 ×0.05 弹开/砸落、飞斧归零、长矛 ×0.2；`EnableShield` cfg 可关（false = 仅视觉）。
- **冲锋冷却 4.25s → 10s**（用户指定，冲锋更稀有更像技能；不改变已定稿的冲锋表现本身）。

**📋 当前已知遗留问题（2026-08-15）**：
1. 剑柄仍与持剑手像素重叠、PartTex 同色，像素擦除三路（帧/颜色/部件）均证明无解 → 由基底盾牌美术遮挡，观感可接受；
2. 身体仍播放 Onehanded 基底动画帧（挥剑/走路基底），攻击以矛前刺主导观感，但待机时手部姿态仍是持剑；
3. 盾牌为基底静态姿态（原 `Shield` 组件已剥离，不随动画举落），且 `facing <= 0.5f` 正面判定若与视觉朝向不符，需放宽阈值或调盾牌姿态；
4. 冲锋为单体冲刺（敌方无 `EnglishFormationAgent`），列阵冲锋留作后续可选。

**🔧 十一次进展（2026-08-16，新基底白框根因 + 修复）**：
- **根因**：换 `Viking_SwordShield` 基底后，剑/身体/盾美术全在 sprite2 部件贴图 `PartTex_SwordShield`（身体渲染 = 帧 R/G UV 编码采样部件贴图）。上一轮"整块清空"把身体一起清掉 → 身体区域渲染为空 → 背后亮地面透出 = **白框**。
- **修复**：`RemoveSwordSprite2Mode` cfg（Visual 区）三态——`0`=保留原部件贴图、只靠帧擦除去剑（默认，身体最完整无白框）；`1`=整块清空（旧方案，白框）；`2`=只擦亮银剑身像素（`GetBrightErasedSprite2`，去剑+保留身体折中）。
- **构建**：0 警告 0 错误，Debug 100864B 已部署。

**🔧 十二次进展（2026-08-16，白框仍在 → UV 感知亮采样擦除根治）**：
- **实测**：模式0后身体完整了（暗色维京轮廓 ✅），但**白框还在** + 持剑的手臂未被盾牌遮挡。
- **根因（日志 + 离线分析）**：运行时 ETC2 压缩的 `PartTex_SwordShield` 单元比离线亮（亮像素 bbox 从离线 y2~50 膨胀到运行时 y0~105）。部分**身体帧像素**（G 高、不满足红暗阈值 70/40/20）解码 UV 后采样到**亮银部件像素** → 渲染成白/亮块 = **白框**；旧红暗帧擦除永远抓不到它们（它们是"采样到亮部件"而不是"帧色红暗"）。
- **修复（`RemoveSwordFrameUVErase`，默认 true）**：帧擦除新增 **UV 感知亮采样判定**——任何帧像素的 R/G UV(R/255,G/255) 解码到部件单元坐标、采样到亮银像素(r,g,b>150)就一并擦除。白框像素无论帧色如何都被擦，暗身体像素（采样暗部件）不受影响。
- **连手一起删（`RemoveSwordFrameUVHalo`，默认 0）**：>0 时把"解码 UV 落在距亮部件像素 ≤N 部件像素"的帧像素也擦（光晕吃持剑的手/护手/剑刃边缘）。三步安全阀：红暗>20% / 亮采样>45% / 光晕>15% 任一超限即跳过该帧防误擦。
- **顺带修复**：`PreEraseAllOnehanded` 只擦 Onehanded 帧、新基底 Swordsman 帧从未预擦（首帧剑闪回仍在）→ 已放宽为 Onehanded+Swordsman 并加入 UV 擦除；预擦被安全阀跳过的帧不再错误标记"已擦"（留给逐帧路径重试）。
- **日志暴增（你要的"暴露更多问题"）**：`[去剑] 帧 X 去剑成功 擦除=N（红暗=.. 亮采样=.. 光晕=..）`；一次性 `UV亮采样图`（B=白框像素 S=红暗 .=身体）+ `亮采样分析`（白框像素计数）；`网格子对象材质块`（每个 MeshRenderer 的 block._MainTex/_PartTex，验证去剑克隆覆盖 _MIRROR_ON 变体）；F8 渲染诊断补网格块纹理。
- **构建**：0 警告 0 错误，Debug **107008B** 已部署到 plugins（SHA 验证通过）。

**🔧 十三次进展（2026-08-16，实测推翻 UV 理论 → 亮剑部件擦除 + 材质块修复）**：
- **实测（用户）**：模式0 + UV擦除后——剑刃剑柄仍存在、盾牌未盖住剑柄的手（疑似视角）、**白框仍存在**。
- **数据推翻第十二轮 UV 理论**：全帧日志 `亮采样(白框源)=0`（新 UV 亮采样擦除在游戏里 0 命中——运行时 ETC2 的亮像素位置与离线 PNG 完全不同，离线 29 个白框像素在运行时不存在）；F8 转储显示**身体 SpriteAnimator 下 4 个 MeshRenderer 有 2 个 `block._MainTex/_PartTex` 全为 null**（`Unlit/ColoredCharacter` 对 null 纹理默认采样白色 → 若网格有几何即白框），且游戏每帧用原图集重写 block 时 `_PartTex` 会被清掉。
- **离线渲染模拟定案**（`tmpfix/render_debug.py`，cand0 解码 = 游戏内解码）：模式0下**亮剑区域（部件贴图亮银剑刃）经帧 UV 采样渲染成亮/白色** = 白框+亮剑的直接来源；**模式2（擦亮银部件像素）后 706/798 暗身体像素保留、剑区 93~141px 变空洞（预期挖剑，非身体透明）**。
- **修复**：
  - cfg 默认改 `RemoveSwordSprite2Mode=2`：**部件贴图只擦亮银亮色像素**（剑刃+2D盾从 PartTex_SwordShield 抹掉、暗色身体保留）→ 白框/亮剑根治；加 **35% 亮银占比安全阀**（ETC2 把身体也染亮时退化模式0不伤身体）。
  - `RepairBodyMaterialBlocks`（每帧）：把去剑克隆+部件贴图**强制写入全部 ColoredCharacter MeshRenderer 材质块**（空块=白框源必须补纹理；游戏每帧用原图集覆盖 block，必须每帧重写；`GetPropertyBlock` 先拷全量属性再只覆盖 _MainTex/_PartTex，`_BloodTex`/`_Mirror` 等原样保留）。
  - `_sa.block` 更新时**保留 `_PartTex`**（不再被 ComittBlock 清掉）。
  - 顺带修诊断 bug：`IsPartBrightExact` 原在像素清零后调用（UV 解码变 cell(0,0)），把真亮采样误标成"光晕"（上一轮 `UVHalo=0 仍报光晕=6` 的来源）→ 先判纯亮再清零。实际模式0+UVErase 每帧约擦 6px 亮采样，远不足以消白框——白框主体是部件贴图亮剑，须模式2。
  - 诊断：一次性 `身体网格详细`（每个身体渲染器的 mesh 顶点数 / isVisible / `_MainTex` **实例 ID**——终于能区分去剑克隆 vs 原始图集）；F8 渲染诊断补顶点数 + `←去剑克隆✓`（实例 ID 比对，`SwordRemover.IsSharedClone`）。
- **构建**：0 警告 0 错误，Debug **110080B** 已部署（SHA `C14ED2F4…`）；cfg 已预写 `RemoveSwordSprite2Mode=2`。

**🔧 十四次进展（2026-08-16，白框根治✅ 后实测：剩剑柄 + 持矛手脱离身躯 → flood 擦剑柄 + 矛挂手位）**：
- **实测（用户）**：白框已消失 ✅；剩余两问题——① 剑柄依旧可见；② 黑矛兵持长矛的手脱离身躯（观感差 + 攻击范围显得异常大）。
- **问题①根因（剑柄残留，日志三证据）**：
  1. `[去剑] sprite2 亮银剑身擦除(剑盾基底) 擦除=833/6204 bbox=(133,0)-(191,105) 擦bbox=False`——模式2只擦"纯亮银"(r,g,b>150) 833px（剑刃+2D盾已抹），但 **bbox 接壤擦被安全阀拒绝**：剑刃与 2D 盾亮像素合流，bboxArea=59×106=6254 > 不透明×45%=2792 → `擦bbox=False` → 剑柄/护手区从未被擦；
  2. `[去剑·PartTex探针] cand0 剑柄→Part=(54,50,49,255)` / `(173,158,148,26)`——**剑柄在运行时 ETC2 部件贴图是暗灰/半透明、不是亮银** → 纯亮阈值永远漏；
  3. 帧级 UVHalo 的部件掩码只标记"亮>150"像素 → 暗灰剑柄不在掩码内 → 帧擦也够不着。
  → 结论：剑柄只能在部件贴图层用"从亮银区向外 flood"吃掉（身体暗色≈(33,26,24) 是天然屏障），或靠盾牌遮挡。
- **问题②根因（持矛手脱离身躯）**：长矛 `MountSpear` 用固定偏移 `(0, radius×1.4, radius×0.6)`——矛根在**身体正中**；基底剑盾兵"持剑的手"在 **Weapon 锚点（距身 ~0.20m、偏离身体中心）** → 矛根与手错位 → "手没握着矛、矛悬在身前"；矛长 0.6m 再叠加错位 → 有效攻击范围显得异常大。
- **修复（日志 + 预制件）**：
  - **`RemoveSwordSprite2GripBand`（新 cfg，默认 2）**：`GetBrightErasedSprite2` 擦完亮银后，从擦除的亮银像素出发 **BFS flood 扩散擦除"相连的非身体暗色"像素**（剑柄/护手/持剑手；身体暗色是屏障不被吃；flood≤300px 且面积≤1200 双保险，超限放弃防误擦）。日志 `剑柄flood=N（采样首/中=(r,g,b,a)）`。
  - **`SpearMountToHand`（新 cfg，默认 true）**：矛根改挂 **Weapon 锚点（持剑手位）**，`localPos=InverseTransformPoint(anchor)+(0, radius*0.1, radius*0.15)`，矛尖朝前；找不到锚点退回旧偏移。日志 `[WEAPON] 长矛握持位: 锚点=… → 矛根localPos=…`。
  - **盾牌前移 0.25→0.12**（`RepositionShieldToSwordHand`）：盾面贴住持剑手、真正盖剑柄；新增 `覆盖持剑手=是✓/否✗` 日志（Weapon 锚点是否落在盾 Renderer bounds 内）。
  - **诊断**：`[去剑] 剑柄残留诊断`（bbox 内剩余 暗灰剑柄/亮灰护手/暖色皮肤/亮银残 计数）+ `剑柄残留·定位图`（g=暗灰 G=亮灰 s=皮肤 b=身体，取暗灰最多一行±8 行）；F8 渲染诊断新增 `持矛手对齐: 矛根…|锚点…|矛根↔手距离=…`（0=矛根正好在手上）。
- **构建/部署**：0 警告 0 错误，Debug **116224B**，SHA `DCA42BCE…`；cfg 已预写 `RemoveSwordSprite2GripBand=2 / SpearMountToHand=true`。
- **待实测**：① 剑柄是否消失（日志 `剑柄flood=N>0` + `剑柄残留诊断 暗灰剑柄≈0`；若仍在 → `RemoveSwordSprite2GripBand` 3→4 加大，或看盾牌 `覆盖持剑手`）；② 身体若被误擦出洞 → `RemoveSwordSprite2GripBand=0` 关闭改盾牌遮挡；③ 持矛手与矛根对齐（F8 `矛根↔手距离`≈0、游戏内手握着矛）；④ 矛挂手位后攻击范围观感恢复。

**🔧 十五次进展（2026-08-16，问题未解决复盘 → flood 换"剑柄改色" + 矛跟手 + 盾贴手）**：
- **实测（用户）**：问题未能解决——日志铁证 `剑柄flood=0`（flood 从头到尾一个像素都没吃到）且 `暗灰剑柄=1893` 仍残留；`覆盖持剑手=否✗`（盾还是没盖住手）。
- **flood 为什么 0 命中（离线分析 `analyze_uv.py` / `analyze_grip_frames.py` 定案）**：
  - 剑是**竖握**的：剑刃(亮银)在上、**剑柄(暗灰 54,50,49)横在胸口**（帧 rows17-26、部件区 rows47-88），两者之间隔着**身体暗色带** → BFS 从擦除的亮银种子出发，第一步就被身体暗色屏障拦下 → `flood=0`；
  - 且"擦除(透明)"会把胸口剑柄带**挖成洞**（背后地面透出）或按模式1那样**变白框**——剑柄带不是"浮空残留"而是**画在身体上**的带子，不能擦、只能改色。
- **修复①（`RemoveSwordSprite2GripBand` 语义改为"剑柄改身体色"，默认仍 2）**：
  - 离线渲染模拟定案（`validate_grip_recolor.py`，用运行时 ETC2 贴图）：渲染=帧 R/G 编码 UV 采样部件贴图×黑染色；**把单元内暗灰(40≤r≤100,|r-b|≤25)+亮灰护手(100<r<150 中性)直接改为身体暗色(33,26,24)、保留 alpha** → 帧渲染 `剑柄色像素 1402 → 0`、身体轮廓完好（1536 身体色，洞=187 剑刃洞不变，无新增洞/白框）。
  - `GetBrightErasedSprite2` 擦完亮银后新增 `RecolorGripToBody` 改色一遍（替代 `GripFloodErase`）；日志 `剑柄flood=` → `剑柄改色=Npx`；残留诊断预期 `暗灰剑柄≈0`（改色后归入"身体暗色"类）。
- **修复②（矛根每帧跟手）**：`SpearChargeComponent` 新增 `TrackSpearToHand()`——Setup 记录"矛根相对 Weapon 锚点的偏移"，每帧 `_spearBaseLocalPos=当前手位+偏移`，突刺/冲锋/待机时矛根始终贴在手上（旧版矛根固定于挂载瞬间，手随身体动画一动就脱开）。`_handAnchor=null` 时退回旧行为。
- **修复③（盾真正贴手）**：`RepositionShieldToSwordHand` 前移 0.12→**0.05**、抬高 0.1→**0.02**、放大 1.2→**1.5**（盾心落在 Weapon 锚点上）；`BlackSpearmanShield.LateUpdate` 偏移同步（旧 0.25/0.1 与挂载 0.12 不一致，每帧被 LateUpdate 覆盖成 0.25 才是覆盖失败的元凶）→ `覆盖持剑手=是✓` 应达成。
- **构建/部署**：0 警告 0 错误，Debug **115200B**，SHA `405C2D2D…`；已部署 plugins 并哈希验证 MATCH；cfg 未新增（复用 `RemoveSwordSprite2GripBand`，>0 启用改色）。
- **待实测**：① 游戏内剑柄带是否消失（胸口不再有暗灰横带）、身体是否完整无洞；② 日志 `剑柄改色=Npx`（N 应为几千量级）+ `剑柄残留诊断 暗灰剑柄≈0` + `亮银残=0`；③ 跑步/刺击时矛根是否始终贴手（F8 `矛根↔手距离`≈0 且移动中不变）；④ 盾牌 `覆盖持剑手=是✓`；⑤ 攻击范围观感恢复正常。

**🔧 十六次进展（2026-08-16，核对 BadNorthDatabase 定案 → 走路线A"整剑改身体色"零洞方案）**：
- **数据库核对（回答\"有没有天然剑体相分离的单位\"）**：
  - ✅ **玩家方长矛兵（Pikeman）天然武器体分离**：反编译 `Spear.cs` 证据（数据库 09.01 §14.2-14.4）——矛是**独立 `BatchedSprite spearSprite`**（原生精灵 `Spear_0/1/2.png`），身体 SpriteAnimator 无武器；游戏官方做长矛兵就是这个架构。
  - ✅ **帧是单位无关的**：敌人 Swordsman 帧 × 我方无剑单元 `PartTex_English`(0,0) 解码渲染**完全自洽**（头盔+圆盾+身体+腿，无剑）→ 换\"绘画\"即可换外观，逻辑零改动（敌我渲染管线相同：SpriteAnimator/sprite2/AgentTextureBaker/Unlit/ColoredCharacter）。
  - 决策：用户选**路线A**——保留维京剑盾兵身体（含维京盔/皮甲/2D盾观感），把部件单元里的**整把剑**（亮银刃+暗灰柄+亮灰护手）改身体色，不换单元不换 rect。
- **离线验证（`validate_routeA_recolor.py`，运行时 ETC2 贴图）**：路线A（亮银+剑柄+护手全改身体暗色、保留 alpha）→ `剑柄/护手色=0`、`亮银残=0`（无白框）、**洞=原始单元基准（158/205，零新增——原\"擦透明\"路径新增 29/65 洞）**、身体色像素最多（1431/1258）。
- **代码改动（SwordRemover.cs + Plugin.cs）**：
  - `GetBrightErasedSprite2`（模式2）第二遍：**亮银擦透明 → 改身体暗色(33,26,24)、保留 alpha**（bbox 内接壤像素同样改色）；日志 `擦除=` → `亮银改色=`，注释\"零洞\"。
  - 安全阀 `Sprite2SafetyRatio` 0.35→**0.60**：改身体色无害（ETC2 增亮身体像素改回身体色=无害），仅留结构异常兜底。
  - `LateUpdate` 新增**路线A分支**：部件已改色（`_sa.sprite2==改色克隆`）时**跳过帧擦透明**（帧像素保持不透明、采样改色部件即渲染成身体色，避免帧级挖洞），材质块仍每帧重写 `_PartTex=改色克隆`；烘焙重设 sprite2 自动退回帧擦除兜底。日志 `[去剑] 路线A生效…`。
  - Plugin.cfg `RemoveSwordSprite2Mode` 模式2描述更新为\"整剑改身体色、零洞无白框\"。
- **构建/部署**：0 警告 0 错误，Debug **115712B**，SHA `D00443D3…`；已部署 plugins 并哈希验证 MATCH。
- **待实测（重点）**：① 胸口剑柄带彻底消失、**身体无洞**（这是与第十五轮的最大差异——旧擦透明有洞，现改色零洞）；② 无白框（亮银残=0）；③ 维京观感保留（角盔/皮甲/2D盾改没、3D盾在）；④ 矛根贴手 + 盾盖手（沿用十五轮修复）。

**🔧 十七次进展（2026-08-16，用户实测反馈 → 回退"擦透明"、移除盾牌、修复抽动与举矛）**：
- **用户实测反馈**：① 路线A 的"改身体色"把剑刃改成了**黑色剑影**（亮银→身体暗色后剑刃轮廓浮在身体旁），且剑柄未变色（暖色持剑手/剑柄带仍在）→ 用户明确回退："用之前的方法擦除掉剑刃，没想办法改变剑柄颜色"；② 盾牌效果+美术可删除；③ 黑矛兵存在抽动（日志证据：冲锋中 `spearWorldRot=(340.4,159.7,109.5)` 矛 180° 翻转）；④ 船上/未判定敌人前长矛未树立（一直水平持矛等待，违背"举矛"设计）；⑤ 问 `.bak_r15` 文件是什么。
- **改动①（回退"擦透明"）**：`GetBrightErasedSprite2`（模式2）第二遍从"改身体暗色、保留 alpha"**改回"擦透明"**（亮银 + bbox 内接壤像素 → `(0,0,0,0)`），`Sprite2SafetyRatio` 0.60→**0.35**（擦透明会挖洞，需收紧防 ETC2 增亮身体被误擦）；`LateUpdate` **删除路线A跳过帧擦分支**，帧级擦透明恢复（红暗+UV亮采样照常）；`RemoveSwordSprite2GripBand` 默认 **0**（不改剑柄颜色，用户指定）。预期：剑区挖洞（离线验证新增 29/65 洞，可接受）、无黑色剑影、无白框。
- **改动②（移除盾牌）**：`EnableShield` 默认 **false**，语义改为"**完全移除盾牌**"——剥离模板与运行时 `RemoveSword` 都按 `ShieldChildNameKeys` 禁用盾牌子对象，`MountShield` 直接 return（不挂 `BlackSpearmanShield`、不移盾遮蔽）；`true` 仍可回退到"保留盾牌美术+格挡"。
- **改动③（修抽动=矛翻转）**：`SpearVisual.TryGetAimRotation` 目标在**前半球之外**时混合回朝前（不再 180° 翻转），roll 改用虚拟 right=`cross(up, dir)`（恒 ⊥ dir、不随身体自旋退化）——冲锋/后退/刺击全链路共用；盾牌移除后其每帧 snap 也消失。
- **改动④（长矛始终树立）**：新增 `SpearVisual.TryGetRaisedRotation`（矛尖朝前上方 ~55°）+ `SpearChargeComponent.UpdateSpearPose()`：待机/移动/冷却时**无存活目标 → 举矛**、有目标 → 矛尖朝敌；`LateUpdate` 无目标时 Slerp 抬回举矛姿态。船上/未判定敌人前长矛保持树立（恢复设计）。
- **改动⑤（.bak_r15 说明）**：`BadNorthBlackSpearman1.3.dll.bak_r15` 是**第十六轮部署时对第十五轮 DLL 的手动备份**（116224B，第十五轮行为：剑柄改身体色+盾牌保留）。它不是 mod 的一部分，BepInEx 不会加载它（只加载 `.dll`），**可随时删除**；本轮部署又生成了 `.bak_r16`（第十六轮 115712B），同属回滚备份。
- **构建/部署**：0 警告 0 错误，Debug **116736B**，SHA `7D041156…`；已部署 plugins 并哈希验证 MATCH；cfg 已更新（`EnableShield=false`、`RemoveSwordSprite2GripBand=0`）。
- **待实测**：① 剑刃被擦除（无黑剑影、无白框，剑区有洞属预期）；② 盾牌完全消失（无美术无格挡）；③ 抽动是否消除（尤其冲锋中矛不再翻转）；④ 船上/无目标时长矛树立（举矛姿态）；⑤ 维京观感（角盔/皮甲/2D盾）保留。

**🔧 十八次进展（2026-08-16，用户新指令 → 剑柄改身体色、抽动双管修复+诊断、.bak 说明确认）**：
- **用户指令**：① 剑柄需改色，和黑矛兵的身躯颜色一致；② 黑矛兵人物存在抽动、找不到原因 → 安排日志和预制件分析；③ mod 文件夹中的 `.bak_r15`/`.bak_r16` 后缀文件。
- **改动①（剑柄改身体色，SwordRemover.cs + Plugin.cs + cfg）**：`RemoveSwordSprite2GripBand` 默认 **0→2**（`RecolorGripToBody` 恢复启用）：把部件贴图单元内"暗灰剑柄(40≤r≤100,|r-b|≤25)+亮灰护手(100<r<150 中性)"改为身体暗色 (33,26,24)、保留 alpha（不挖洞不白框）。预期日志 `剑柄改色≈1900px`、残留诊断 `暗灰剑柄≈0`。刃部仍按第十七轮"擦透明"（剑区挖洞可接受），不恢复"改刃色"的黑色剑影。
- **改动②（抽动修复①·拦截原版挥剑 Clash 动画，Plugin.cs）**：新增 Harmony 前缀 `SwordsmanClashActivatePrefix`。**根因**：黑矛兵每次命中（SpearHit/冲锋 HIT）后 `Agent.DealDamage → Swordsman.ModifyAttack → clash.SetActive → ClashActivate` 会播放 `Swordsman_Clash`（剑击滑动动画，日志 `clip=Swordsman_Clash / body=slide:True`），叠加在矛刺上 = "人物抽动"。黑矛兵直接跳过 `ClashActivate`（IL 验证其仅播动画+设 animator bool，伤害在 DealDamage 内已结算）。启动日志新增 `ClashBlock=OK`。
- **改动③（抽动修复②·冲锋橡皮筋，SpearChargeComponent.cs）**：WindUp 时 `movability 0.2/0.1 → 1/1`。**根因**：0.2 抄自原版 travelling，但原版 navPos 是走路速度推进，本冲锋 navPos 以 5m/s 推进 → movability=0.2 把 transform 追 navPos 限到 ~1m/s，实测 `lag=0.89~1.31m`；冲锋结束恢复 movability 时角色被"弹回"到 navPos = 抽动。另在 `EndCharge` 加安全网：navPos 与 transform 差 >0.3m 时把 navPos 重锚定到 transform 防回弹（日志 `[Charge] 收尾对齐`）。
- **改动④（抽动诊断，TwitchProbe.cs + Diagnostics.cs）**：新增 `TwitchProbeComponent`（每个黑矛兵挂载）：逐帧监控 ①位置跳变 >0.35m/帧 ②朝向急转 >40°/帧 ③动画倒退（同 clip norm 回落 >0.3）④橡皮筋 navLag 差 >0.35m/帧 ⑤长矛本地 yaw 翻转 >90°/帧 ⑥精灵帧闪动（1s 内 ≥3 帧名且变化 ≥4 次），异常打一行 `[抽动]` 日志（含 pos/yaw/navLag/clip/norm/animSpeed/sprite/body/phase/矛姿态），每实例 0.5s 节流。F8 新增 `DumpPrefabAnalysis()`：VikingReference + 模板(viking) + 运行实例(agent) 的完整层级/组件/Animator 控制器/动画片段数/当前动画/持剑锚点路径/长矛姿态。
- **改动⑤（.bak 说明确认）**：plugins 目录现有 `.bak_r15`（116224B，第十五轮 DLL）、`.bak_r16`（115712B，第十六轮 DLL）、本次新增 `.bak_r17`（125440B，第十七轮 DLL）。它们都是**各轮部署时对上一轮 DLL 的手动备份**，BepInEx 只加载 `.dll`，`*.bak_rNN` 永不被加载，**可随时删除**。
- **构建/部署**：0 警告 0 错误，Debug **125440B**，SHA `86CA16B2…`；已部署 plugins 并哈希验证 MATCH；cfg 已更新（`RemoveSwordSprite2GripBand=2`，描述同步）。
- **待实测**：① 剑柄带变身体色（胸口无暗灰横带）、身体完整无新增洞；② 刺击/冲锋命中时身体不再播 Swordsman_Clash（无剑击滑动）；③ 冲锋全程 navLag 保持小（<0.3m），冲锋结束无回弹；④ `[抽动]` 日志是否还会出现（出现则看原因编号与现场）；⑤ 无目标/船上时长矛树立（举矛姿态）。

**🔧 十九次进展（2026-08-16，第十八轮实测反馈 → 盾牌格挡、技能期可击杀、"闪亮"根因）**：
- **用户实测反馈（第十八轮日志 + 游戏内）**：① 剩余"抽动"看起来更像**美术素材的闪亮**（不是身体橡皮筋——ClashBlock/movability 修复已生效，`[Charge] 收尾对齐` 正常收尾）；② 长矛突刺/冲锋打我方**持盾单位**时直接死亡——缺"打到盾牌上的反馈和免伤"；③ 希望黑矛兵在**技能释放过程中可被击杀**（平衡性）。
- **改动①（"闪亮"根因 = 去剑预擦除空转，SwordRemover.cs）**：`PreEraseAllOnehanded` 的入参误传**共享克隆**（`GetSharedClone` 产物）而非**源纹理**——`ReferenceEquals(sprite.texture, 克隆)` 恒 false → 帧列表恒空 → "空结果=完成"提前标记 `_preErased=true` → **所有 Swordsman/Onehanded 帧都退回运行时逐帧擦除** = 每帧"首次显示后才擦"（晚一帧、2048x1024 整图上传慢）→ 剑刃在战斗中/冲锋高帧率下持续闪回 = 用户所见"美术素材的闪亮"。修复：① 传源纹理匹配 `sprite.texture`；② 像素读写改在共享克隆上进行（与 `_MainTex` 同一份）；③ `_preErased` 单 bool → `_preErasedTex`（按源纹理实例 ID），多图集可独立预擦。预期日志：`[去剑] 预擦除 Onehanded/Swordsman 帧 N 张`（此前应为 0 张）。
- **改动②（长矛 vs 我方剑盾兵 = 正面格挡反馈 + 免伤，SpearChargeComponent.cs）**：新增 `TryShieldBlockSpear(target, ref atk)` 并在**突刺（DealSpearDamage）/ 冲锋命中（DealChargeDamage）/ 抵达爆发（ArrivalBurst）**三处统一调用。判定对齐原版 `Shield.ModifyAttack`：目标有 `Shield` 组件 + `agent.shield` 举起 + `Dot(shield.forward, -attack.direction) > 0.5` → **伤害 ×0.2、眩晕 ×0.4**（原版"长矛×0.2"同口径）+ 盾击音效/火花（冲锋/爆发 monoAttacker 非 CloseCombatBrain，原版不识别，由本组件补反馈；突刺是 Swordsman，原版 Shield 会自己播 Deflect/Block 反馈，只做减免避免双音效）。根因：原版 Shield.ModifyAttack 只认 `monoAttacker is Spear`（我方长矛兵类），黑矛兵突刺传 `Swordsman`（近战分支非 parry 不减免）、冲锋传 `SpearChargeComponent`（完全不识别）→ 剑盾兵无免伤被秒杀。
- **改动③（技能释放期可被击杀，SpearChargeComponent.cs）**：`IAttackResponder.ModifyAttack` 删除 `attack.ignore=true`（冲锋/后退不再免疫伤害）——技能期间照常吃箭矢/近战/眩晕/击退，可能被击杀；`Update()` 加死亡守卫：`aliveState` 失活立即 `AbortCharge()`（释放 exclusives、恢复 maxSpeed/movability、回 Idle），避免尸体继续推进 navPos。保留 IAttackResponder 注册，若想回调"冲锋霸体"只需在空实现里加一行。
- **改动④（抽动探针去噪，TwitchProbe.cs）**：⑥精灵帧闪动只统计"静止/站桩"时的帧变化——Body.stepping、navPos↔transform 位移、上一帧位移、WindUp/Charging/Retreat 阶段一律跳过。旧口径把正常走路/跑步帧循环（1s ≥3 帧名 恒真）误报成闪动，刷屏且掩盖真异常；现在只有"站着不动但精灵帧乱跳"才报警（这才是真·闪亮）。
- **构建/部署**：0 警告 0 错误，Debug **126976B**，SHA `73E9D5DC…`；已部署 plugins 并哈希验证 MATCH；本轮部署将上一轮（第十八轮 125440B）DLL 备份为 `.bak_r18`。
- **待实测**：① `[去剑] 预擦除 … N 张` 应 >0（战斗/冲锋全程无剑刃闪回）；② 黑矛突刺/冲锋/爆发打我方剑盾兵 → 日志 `[盾牌] 黑矛长矛被格挡 … dmg→0.42` + 盾击音效，剑盾兵不再被秒；③ 黑矛兵 WindUp/Charging 途中被箭矢/近战可击杀，死亡后冲锋状态机干净收尾；④ `[抽动]` ⑥不再刷屏（仅静止帧乱跳报警）。

**🔧 二十次进展（2026-08-16，第二十轮：身子闪烁根治 + 探针去噪 + 剑柄改色应用诊断）**：
- **用户实测反馈（第十九轮日志 + 游戏内）**：① 黑矛兵身子**仍**有闪烁（`[抽动]` ⑥精灵帧闪动在待机/冷却时持续刷屏，1s 内帧名变化 4~7 次）；② 刺击盾牌格挡已实现（`[盾牌] … dmg→0.42`）；③ 剑柄改身体色**未生效**——日志 `剑柄改色=2142px`、残留诊断 `暗灰剑柄=0` 说明部件贴图克隆已改色，但游戏内剑柄仍可见。
- **改动①（身子闪烁根治，SwordRemover.cs）**：**根因 = 去剑纹理与网格 UV 错位一帧**。动画系统在 `Update→LateUpdate` 之间把 `SpriteAnimator.sprite` 字段推进到下一帧；旧代码 `LateUpdate` 直接用 `_sa.sprite`（已是新帧）拿"新帧去剑纹理"盖到"旧帧网格 UV"上（网格 UV 要等原版下一次 `Update` 才换成新帧）→ 每次换动画帧都闪一帧 = 用户所见"身子闪烁"。修复：新增 `Update()` 采样 `_frameSprite`（与原版 `SpriteAnimator.SetSprite()` 同一时刻同一值），`LateUpdate` 只用 `_frameSprite` 覆盖 `_MainTex` → 去剑纹理与网格 UV 永远一致，闪烁消除。
- **改动②（抽动探针去噪，TwitchProbe.cs）**：③"动画倒退"只报**剪辑中途倒退**（`_prevNorm<0.7` 时回落），不报正常循环回卷（0.95→0.02）；⑥"精灵帧闪动"改为**同一动画进度(norm 差<0.02)出现不同帧名**才算真闪动（正常待机呼吸循环每帧各占一个进度，永不会同进度双帧）→ 待机/冷却不再刷屏。
- **改动③（剑柄改色应用诊断，SwordRemover.cs）**：每只黑矛兵一次 `[去剑] sprite2 应用诊断`：打印 sprite2 是否已是改色克隆 + 渲染器块 `_PartTex` 实例 ID 与克隆是否一致 → 判定"改色克隆是否真的上块"。判读：克隆已上块但剑柄仍可见 = 改色目标色 (33,26,24) 与运行时身体着色不符，下一步按运行时身体像素采样改色；块里是原部件贴图 = `SetSprite2` 被烘焙/重置覆盖，需查重烘焙路径。
- **构建**：0 警告 0 错误，Debug **128000B** 已部署 plugins（SHA `79C5527F…` 验证 MATCH），`.bak_r19`（126976B，第十九轮 DLL）已生成。
- **待实测**：① 战斗/冲锋/待机全程无身子闪烁；② `[抽动]` ③/⑥ 不再刷屏（仅真异常报警）；③ `[去剑] sprite2 应用诊断` 判定改色克隆是否上块 → 据此决定剑柄下一步（改色目标色校准 or 查 SetSprite2 重烘焙）。

**🔧 二十一次进展（2026-08-16，第二十一轮：探针⑥振荡判定 + sprite2 克隆先上块 + 诊断延迟判读）**：
- **第二十轮日志判读**：① ⑥精灵帧闪动**仍在刷屏**，但逐条分析 badNorm 全部 ≈ 动画关键帧边界（0.234/0.25/0.498/0.998/1.248/1.748…≈0.25/0.50/0.75/1.00+k），且帧对沿动画顺序前进（0002↔0003→0003↔0004→0004↔0005→0005↔0002）→ **全部是正常慢速换帧的误报**（第二十轮口径"同进度(norm 差<0.02)不同帧名"把"恰落在关键帧边界两侧的采样"误判为闪动；真闪动应表现为同进度帧名**来回振荡**）；② ③ 无刷屏（去噪生效）；③ sprite2 应用诊断判"块里不是改色克隆"——但**当帧先写块（原部件）再换 sprite2**，且 `SetSprite2` 的 ComittBlock 只提交给 BatchedSprite.rends（2 个渲染器）、诊断只读 mrs[0] → 是**诊断时机+样本缺陷**，不能证明克隆没上块。
- **改动①（探针⑥改振荡判定，TwitchProbe.cs）**：真闪动 = 同一动画进度(norm 差<0.02)内帧名**来回振荡**（A→B→A，同一帧在同一进度出现≥2次）；正常换帧只在边界两侧各出现一次、同进度只对应一帧 → 待机慢速呼吸/冷却不再刷屏，静止画面帧级乱跳仍报警。
- **改动②（sprite2 克隆先上块，SwordRemover.cs）**：`LateUpdate` 顺序修复——先 `ApplySprite2Erase()` 换好去剑/改色克隆，再执行帧擦除/`RepairBodyMaterialBlocks` 写材质块 → 全部身体渲染器 `_PartTex` 写入的都是克隆纹理（旧顺序先写原部件块再换 sprite2，其余身体渲染器块 `_PartTex` 仍是原部件 = 剑柄改色实际没上大部分块）。
- **改动③（帧擦除 UV 掩码锁定原版部件，SwordRemover.cs）**：新增 `_partCacheSprite`（Setup/首次 ApplySprite2Erase 时锁定原版 sprite2），`EnsureErasedTexture` 的部件掩码永远按**原件**构建——否则 sprite2 换成克隆（剑区透明）后掩码变空、UV 亮采样擦除失效（第二十轮日志已见 `UV部件缓存就绪: …_NoSword … 亮(>150)=0`）。
- **改动④（sprite2 应用诊断延迟+全量，SwordRemover.cs）**：克隆上块后**延迟 5 帧**判读稳态，统计**全部** ColoredCharacter 身体渲染器里 `_PartTex == 克隆` 的数量（匹配 N/总数）→ verdict 可靠。
- **构建**：0 警告 0 错误，Debug **128512B**，SHA `C81043FC…`；已部署 plugins 并哈希验证 MATCH；`.bak_r20`（128000B，第二十轮 DLL）已生成。
- **待实测**：① 战斗/冲锋/待机全程身子无闪烁（肉眼确认；探针⑥现在只报振荡）；② `[抽动]` ③/⑥ 不再刷屏；③ `[去剑] sprite2 应用诊断` 应显示"匹配 N/N"——若全部匹配且剑柄仍可见 = 改色目标色 (33,26,24) 与运行时身体着色不符，下一步按运行时身体像素采样校准改色目标色。
**🔧 二十三轮进展（2026-08-16，第二十三轮：身体颜色/剑柄根治 + 冲锋/近战橡皮筋根治 + 探针清理）**：
- **上一轮判读（第二十二轮）**：① 剑柄改色克隆已 4/4 上块、残留=0，但游戏内剑柄带仍可见 → 根因实锤：改色目标色 (33,26,24)（离线采样的身体暗部）与运行时身体亮部 (170,146,115) 不符；② `[抽动]` ④橡皮筋仍真实存在（navLag 0.47→1.13m）——Body.stepping 追赶式插值追不上 5m/s 的 navPos。
- **改动①（身体颜色 + 剑柄根治，SwordRemover.cs + BlackSpearmanVisual.cs）**：不再依赖顶点色 B 通道乘算做黑（该乘算若被游戏重置/未生效 → 身体显示原色暖棕 + 剑柄深色带 = “颜色不对劲+闪烁”）。新方案把黑色**烘进部件贴图克隆**：亮银擦剑后 → 剑柄改“就近取身体像素色”（邻域复制，替代固定色）→ 部件贴图整体压暗 ×0.15 → 身体彻底变黑、剑柄与身体完全同色；顶点色 B 恢复 1.0（仅在偏离时修复）。
- **改动②（冲锋/后退橡皮筋根治，SpearChargeComponent.cs）**：`DoCharging/DoRetreat` 记录 navPos 快照，`LateUpdate` 重新断言 navPos 并把 `transform.position` 硬同步到冲锋线（Body 跑步动画照播，只保证渲染位置与导航一致，防大脑在 Update 后被改写造成回弹）。
- **改动③（近战橡皮筋根治，SpearChargeComponent.cs + Plugin.cs）**：刺击期间冻结矛根（不再每帧跟随持剑手动画摆动）→ 矛直线前刺；攻击期间 LateUpdate 同样把身体同步到 navPos（站桩刺击）。
- **改动④（无关代码清理，TwitchProbe.cs 删除）**：逐帧“抽动探针”（⑥精灵帧闪动口径多次误报刷屏）整体移除；`[近战·待机帧]` 删除、`[近战] 突刺中` 只在刺出段(thrust>0.3)记录、`[Charge] 冲刺中` 降频到 1.5s、心跳 8s→30s。
- **构建**：0 警告 0 错误，Debug **125440B**；已部署 plugins 并哈希验证 MATCH。
- **待实测**：① 游戏内黑矛兵身体是否整体黑色、剑柄带是否消失、无闪烁；② 冲锋/后退/近战刺击是否无橡皮筋；③ 日志量是否大幅下降。
**🔧 二十四周进展（2026-08-16，第二十四轮：闪白根治 + 头盔保护 + 渲染诊断）**：
- **上一轮实测反馈**：① 剑柄染色"成功"但误伤同色部件（**黑矛兵头盔被改色**）；② 剑刃擦除导致**头盔部分材质透明**；③ **黑色身躯仍在闪白**。
- **根因定案（对照 BadNorthDatabase 源码）**：
  - 闪白 = `Agent.cs:418` `aliveAndGrounded.OnUpdate += UpdateColor` → **每帧** `spriteAnimator.color.b = 1 - healthFraction`。B 通道是游戏的**受击白闪通道**；旧版周期写 B（60/30 帧间隔）打不过它 → 受击时 b→1 身体闪白/暖色。
  - 头盔被改色/透明 = `PartTex_SwordShield` 单元的**亮银中心竖条同时含头盔冠饰(y20-44)与剑刃(y45-69)**；亮银擦除把头盔冠饰一起擦透明，bbox 擦除把胸口挖洞，剑柄改色把肩甲/胸甲/头盔同类灰误涂。
- **改动①（闪白根治，BlackSpearmanVisual.cs）**：改为**每帧**在 LateUpdate 强制顶点色 B——身体（SpriteAnimator）B=0.02（恒黑、抑制受击白闪）、长矛/阴影（普通 BatchedSprite）B=1.0（保持原色可见）。
- **改动②（头盔保护，SwordRemover.cs）**：模式2 从"亮银擦除+剑柄改色"简化为**只整体压暗 ×0.15（不擦不涂）**——`GetBrightErasedSprite2` 重写为纯压暗（删 RecolorGripToBody/IsGripGray/FindNearestBodyColor/DumpGripResidue 死代码 + GripFloodPx 字段）；帧擦除掩码新增 `HelmetMaxY=45` 排除头盔区（不再把头盔冠饰擦透明）。
- **改动③（渲染诊断，SwordRemover.cs）**：新增 `[渲染诊断]` 周期日志（每 2s、前 2 只）：4 个身体渲染器 `_MainTex/_PartTex` 实例 ID（是否克隆/是否 null）+ 顶点色 B/alpha + healthFraction → 闪白/白框现形即定位。
- **构建**：0 警告 0 错误，Debug **123392B**；已部署 plugins 并哈希验证 MATCH。
- **待实测**：① 身体整体黑、头盔完好（无透明/异色）、受击无白闪；② 冲锋/近战无橡皮筋；③ `[渲染诊断]` 显示 4 渲染器 _PartTex 均为克隆且无 NULL。










---

```
BadNorthBlackSpearman1.3/
├── BadNorthBlackSpearman1.3.csproj
├── Plugin.cs                   # 入口：注册 + 生成池注入 + 生成时表现
├── BSLog.cs                    # 统一日志（控制台 + 文件 + 全局异常捕获）
├── Diagnostics.cs              # 运行时诊断探针（心跳 + F8 转储）
├── BlackSpearmanArt.cs         # 美术资源（PNG 图标）+ I2 本地化
├── BlackSpearmanVisual.cs      # 黑色外观（对抗纹理重烘焙）
├── BlackSpearmanWeapon.cs      # 武器处理（移除剑视觉、可选移除盾牌、挂我方长矛）
├── SpearChargeComponent.cs     # 冲锋技能（IBrainAction + 近战刺击表现）
├── SpearVisual.cs              # 长矛朝向统一工具（冲锋/刺击/普通攻击共用举矛公式）
├── BlackSpearmanShield.cs      # 盾牌格挡效果（复刻 Shield.ModifyAttack，EnableShield=true 才挂载）
├── SwordRemover.cs             # 去剑组件（运行时擦除 Onehanded 动画帧里的剑像素）
├── Resources/
│   └── black_spearman_icon.png # 美术图标（可选）
└── README.md
```

## 许可

MIT License
