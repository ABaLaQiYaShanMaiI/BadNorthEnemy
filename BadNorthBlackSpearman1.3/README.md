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
   反射配置私有字段（`viking` 仅借用 `Viking_SwordShield` 的 `VikingAgent` 预制体引用），
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
| General | `SourceVikingName` | `Viking_SwordShield` | 借用 VikingAgent 预制体引用的源单位 |
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
