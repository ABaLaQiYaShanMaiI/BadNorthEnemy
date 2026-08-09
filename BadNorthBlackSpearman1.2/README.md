# BadNorthBlackSpearman v1.2 — D+F 外部工具链

> **理念**：不再使用运行时 DLL 反射注入。改为在游戏启动前，一次性 patch `Assembly-CSharp.dll`（添加枚举值）和 Unity 资源文件（克隆 prefab）。

## 文件结构

```
BadNorthBlackSpearman1.2/
├── README.md                    ← 本文件
├── LaunchModded.bat             ← 一键 Patch & 启动游戏
├── RestoreBackup.bat            ← 一键恢复原版（卸载 Mod）
│
├── EnumPatcher/
│   ├── EnumPatcher.cs           ← Mono.Cecil 补丁源码
│   └── EnumPatcher.csproj       ← 编译配置
│
├── AssetPatcher/
│   ├── asset_patcher.py         ← UnityPy 资源补丁
│   └── requirements.txt         ← Python 依赖
│
└── MiniPlugin/                   ← 可选：AI 技能（BepInEx）
    ├── BlackSpearmanAI.cs
    └── BlackSpearmanAI.csproj
```

## 快速开始

### 1. 前置条件

- 已安装 [.NET Framework 4.7.2 SDK](https://dotnet.microsoft.com/download)
- 已安装 [Python 3.8+](https://python.org)
- Bad North 已安装（Steam 默认路径或自定义路径）

### 2. 编译 EnumPatcher

```powershell
cd EnumPatcher
# 从 BepInEx 复制 Mono.Cecil.dll（如果还没复制）
copy ..\..\..\..\..\..\BepInEx\core\Mono.Cecil.dll . 2>nul
# 或从游戏目录:
copy D:\Steam\steamapps\common\BadNorth\BepInEx\core\Mono.Cecil.dll .

dotnet build -c Release
```

### 3. 安装 Python 依赖

```powershell
cd AssetPatcher
pip install -r requirements.txt
```

### 4. 应用 Mod

```powershell
# 双击或命令行运行:
LaunchModded.bat
```

首次运行会自动：
1. 备份 `Assembly-CSharp.dll` → `Assembly-CSharp.dll.orig_backup`
2. 备份 `data.unity3d` → `data.unity3d.orig_backup`
3. 向 `Assembly-CSharp.dll` 添加 `VikingAgent.Type.BlackSpearman = 8`
4. 在 `data.unity3d` 中克隆 `Viking_BlackSpearman` prefab
5. 启动 Bad North

### 5. 卸载 Mod

```powershell
# 双击或命令行运行:
RestoreBackup.bat
```

这会将所有被修改的文件恢复到原始状态，**不会损坏游戏和存档**。

## 自定义游戏路径

如果游戏不在 Steam 默认路径，修改 `LaunchModded.bat` 顶部的 `GAME_DIR` 变量：
```batch
set GAME_DIR=你的\BadNorth\安装\路径
```

或设置环境变量：
```powershell
set BADNORTH_DIR=D:\Games\BadNorth
LaunchModded.bat
```

## 安全机制

| 机制 | 说明 |
|------|------|
| `.orig_backup` 备份 | 每个被修改的文件首次 patch 前自动备份 |
| 永不覆盖备份 | 备份文件一旦创建就不会被再次修改 |
| `RestoreBackup.bat` | 从备份恢复所有文件 → 游戏恢复如初 |
| 存档安全 | **不修改任何存档文件**，卸载后存档正常使用 |
| 幂等 Patch | 重复运行不会重复 patch（检测已有改动） |
