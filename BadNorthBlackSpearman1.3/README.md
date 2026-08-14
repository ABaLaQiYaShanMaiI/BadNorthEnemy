# BadNorthBlackSpearman v1.3 —— 特质 Mod 式注入（新思路）

> 为《Bad North》添加**黑色长矛手（Black Spearman）**敌方单位。
> **本版本彻底改变思路**：不再克隆（`Instantiate`）现有预制件，而是像特质 Mod 一样，
> 用 **MMHOOK + Harmony 注入**，**新建**一个 `VikingReference`，配好美术资源，加入**敌人生成池**。

---

## 与历史版本的差异

| 版本 | 策略 | 问题 | 状态 |
|------|------|------|:---:|
| v1.0 | 运行时新建 VikingReference + 反射 | 时序问题（dict 在 Awake 时为空） | 🔒 封存 |
| v1.1 | BBB 风格劫持 `Viking_SwordShield` | SwordShield 消失 | 🔒 封存 |
| v1.2 | `Instantiate(源VR)` 克隆 + Enum/Asset 外部工具链 | 克隆脏状态、外部工具链复杂 | 🔒 封存 |
| **v1.3** | **新建 VikingReference（非克隆）+ 注入敌人生成池 + 特质式美术资源** | ✅ | 🚀 当前 |

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
   反射配置私有字段（`viking` 仅借用 `Viking_Sword` 的 `VikingAgent` 预制体引用），
   注册进 `LevelStateObjectReferences.dict`。

2. **加入敌人生成池**（`LevelNode.Setup`，Harmony postfix）：
   把新单位 append 到每关的 `enemies` 列表，使其成为 `Raid.possibleAgents` 的候选。

3. **生成时表现**（`Landing.Spawn`，MMHOOK）：
   对新单位施加黑色外观（`BatchedSprite` B 通道）、数值强化（伤害/击退/眩晕/体型）、
   冲刺（`SpearChargeComponent`）与刺击（`SpearStabAction`）技能。

4. **美术资源**（特质 Mod 风格）：
   从插件目录 `Resources/black_spearman_icon.png` 加载图标（无 PNG 时回退程序化图标），
   并注册 I2 本地化名称/描述。

5. **武器混搭**（复用我方素材）：
   移除敌方剑盾（`agent.shield=false` + 禁用 `Shield` 组件 + 按名称禁用剑/盾子对象），
   并从我方 Pikeman（`Spear.spearAnim` 的 `BatchedSprite`）克隆长矛挂到黑矛兵身上——
   "敌人的身子 + 我方的矛 + 染黑"。

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
| General | `SourceVikingName` | `Viking_Sword` | 借用 VikingAgent 预制体引用的源单位（默认单手持剑兵基底） |
| General | `NewVikingName` | `Viking_BlackSpearman` | 新单位在生成池中的名字 |
| General | `Bounty` | 8 | 赏金 |
| Spawn | `SpawnChance` | 0.7 | 每关加入生成池概率 |
| Spawn | `ForceFirstWave` | false | 强制第一波出现（便于测试） |
| Combat | `DamageMult` / `KnockbackMult` / `StunMult` / `ScaleMult` | 1.6 / 2.5 / 1.2 / 1.05 | 数值倍率 |
| Visual | `EnableRecolor` | true | 黑色外观 |
| Visual | `EnableWeaponSwap` | true | 移除剑盾 + 复用我方长矛（混搭武器） |
| Skills | `EnableCharge` / `EnableStab` | true / true | 冲刺 / 刺击 |

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

**3. 默认源单位改为 `Viking_Sword`**

- 代码默认值早已改为 `Viking_Sword`，但旧 cfg 文件里的 `Viking_SwordShield` 会覆盖它。
- 已更新 `BepInEx/config/badnorth.blackspearman.v1.3.cfg` → `SourceVikingName = Viking_Sword`（单手持剑兵基底）。

### 📐 原版参考值（校准用）

| 项 | 值 |
|----|----|
| 原版 Pike Charge | duration≈0.447s、radius=0.732、speed=5、range=20 |
| 举矛公式 | `spearAim.rotation = LookRotation(矛尖方向, 角色right) * Euler(0,0,90)` |
| 旋转层级 | 旋转发生在父骨 `spearAim`，`spearAnim` 局部旋转恒等（`spearAnim.localRot=(0,0,0)`） |
| 我方长矛参考 | `spearAim.localPos=(0,0,0)`，`spearAnim.localPos=(0,-0.033,0.037)` |

### 🤔 待验证 / 待解决的困惑

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

## 文件结构

```
BadNorthBlackSpearman1.3/
├── BadNorthBlackSpearman1.3.csproj
├── Plugin.cs                   # 入口：注册 + 生成池注入 + 生成时表现
├── BSLog.cs                    # 统一日志（控制台 + 文件 + 全局异常捕获）
├── Diagnostics.cs              # 运行时诊断探针（心跳 + F8 转储）
├── BlackSpearmanArt.cs         # 美术资源（PNG 图标）+ I2 本地化
├── BlackSpearmanVisual.cs      # 黑色外观（对抗纹理重烘焙）
├── BlackSpearmanWeapon.cs      # 武器混搭（移除剑盾 + 复用我方长矛）
├── SpearChargeComponent.cs     # 冲刺技能（IBrainAction）
├── SpearStabAction.cs          # 刺击技能（IBrainAction）
├── Resources/
│   └── black_spearman_icon.png # 美术图标（可选）
└── README.md
```

## 许可

MIT License
