using System;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.Ballistics;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵盾牌（剑盾兵格挡效果复刻，Shield.cs ModifyAttack）：
    /// 不是摆设——盾牌正面朝向攻击方向时生效：
    ///   · 近战（Swordsman/CloseCombatBrain）：正面格挡伤害归零 + 盾击特效/音效
    ///   · 箭矢（Arrow/TankArcherArrow）：正面伤害 ×0.05（敌方盾牌）或 ×0（我方），随机弹开/钉住/砸落
    ///   · 飞斧（ThrowingAxe）：正面伤害归零 + 弹开
    ///   · 长矛（Spear）：正面伤害 ×0.2（spearShield 特殊格挡）
    /// 由 BlackSpearmanWeapon.MountShieldCover 挂载盾牌视觉后，把本组件加入 agent.attackResponders。
    /// cfg EnableShield=false 时 ModifyAttack 直接放行（盾牌仅剩视觉）。
    /// </summary>
    public class BlackSpearmanShield : MonoBehaviour, IAttackResponder
    {
        public Agent agent;
        public Transform shield;
        public bool enabledByCfg = true;

        string blockSound = "Sfx/English/SwordShield/Block";
        string arrowBounceSound = "Sfx/English/SwordShield/DeflectArrow";

        public void Setup(Agent a, Transform shieldTf, bool cfgEnabled)
        {
            agent = a;
            shield = shieldTf;
            enabledByCfg = cfgEnabled;
            if (agent != null && agent.attackResponders != null && !agent.attackResponders.Contains(this))
                agent.attackResponders.Add(this);
        }

        public void ModifyAttack(ref Attack attack)
        {
            if (!enabledByCfg || agent == null || shield == null) return;
            if (attack.damage <= 0f) return;

            // 盾牌必须朝向攻击来袭方向才生效（原版 Shield.cs:166：Dot(shield.forward, -attack.direction) > 0.5）
            float facing = Vector3.Dot(shield.forward, -attack.direction.normalized);
            if (facing <= 0.5f) return;

            // ① 近战：正面格挡 → 伤害归零（原版 parry 分支；黑矛兵无 parry 状态，简化成"正面近战必格挡"）
            if (attack.monoAttacker is CloseCombatBrain)
            {
                attack.damage = 0f;
                attack.stun *= 0.4f;
                attack.soundSuffix = "Shield";
                try { IslandGameplayManager.RequestCombatAudio(blockSound, agent.gameObject); } catch { }
                BSLog.Info("[盾牌] 格挡近战 " + (attack.monoAttacker != null ? attack.monoAttacker.name : "?") + " facing=" + facing.ToString("F2"));
                return;
            }

            // ② 箭矢/投掷物：正面 → 伤害大幅减免 + 弹开/钉住
            var shootable = attack.monoAttacker as Shootable;
            if (shootable != null)
            {
                attack.damage *= agent.isEnglish ? 0f : 0.05f;   // 敌方盾牌挡箭 ×0.05，我方 ×0
                attack.stun *= 0.2f;
                attack.soundSuffix = "ShieldBounce";
                var arrow = shootable as Arrow;
                if (arrow != null)
                {
                    // 原版 Shield.cs:185-199：随机弹开/砸落（Stick 需 Shield 的 bounds+spriteStamper，简化掉）
                    if (UnityEngine.Random.value > 0.5f)
                    {
                        attack.soundSuffix = "ShieldSmash";
                        try { arrow.Smash(shield.position, shield.forward); } catch { }
                    }
                    else
                    {
                        attack.soundSuffix = "ShieldBounce";
                        try { arrow.Bounce(shield.forward + Vector3.up); } catch { }
                    }
                }
                try { IslandGameplayManager.RequestCombatAudio(arrowBounceSound, agent.gameObject); } catch { }
                BSLog.Info("[盾牌] 挡箭 " + (attack.monoAttacker != null ? attack.monoAttacker.name : "?") + " dmg→" + attack.damage.ToString("F2"));
                return;
            }

            // ③ 飞斧：正面 → 伤害归零 + 弹开
            var axe = attack.monoAttacker as ThrowingAxe;
            if (axe != null)
            {
                attack.damage = 0f;
                attack.stun *= 0.2f;
                attack.soundSuffix = "ShieldBounce";
                try { axe.Bounce(Vector3.up, Vector3.up * 7f); } catch { }
                try { IslandGameplayManager.RequestCombatAudio(arrowBounceSound, agent.gameObject); } catch { }
                BSLog.Info("[盾牌] 挡飞斧 " + (attack.monoAttacker != null ? attack.monoAttacker.name : "?"));
                return;
            }

            // ④ 我方长矛：正面 → 伤害 ×0.2（原版 spearShield 特殊格挡，黑矛兵无 spearShield 状态，恒生效）
            if (attack.monoAttacker is Spear)
            {
                attack.damage *= 0.2f;
                attack.stun *= 0.4f;
                attack.soundSuffix = "Shield";
                BSLog.Info("[盾牌] 格挡长矛 " + (attack.monoAttacker != null ? attack.monoAttacker.name : "?") + " dmg→" + attack.damage.ToString("F2"));
            }
        }

        void OnDestroy()
        {
            if (agent != null && agent.attackResponders != null)
                agent.attackResponders.Remove(this);
        }
    }
}
