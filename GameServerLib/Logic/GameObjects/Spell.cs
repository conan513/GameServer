﻿using LeagueSandbox.GameServer.Core.Logic;
using System;
using System.Collections.Generic;
using LeagueSandbox.GameServer.Logic.API;
using LeagueSandbox.GameServer.Logic.Scripting.CSharp;
using Newtonsoft.Json.Linq;
using LeagueSandbox.GameServer.Logic.Content;

namespace LeagueSandbox.GameServer.Logic.GameObjects
{
    public enum SpellState
    {
        STATE_READY,
        STATE_CASTING,
        STATE_COOLDOWN,
        STATE_CHANNELING
    };

    public class Spell
    {
        public Champion Owner { get; private set; }
        public short Level { get; private set; } = 0;
        public byte Slot { get; set; }
        public float CastTime { get; private set; } = 0;

        public string SpellName { get; private set; } = "";

        public SpellState state { get; protected set; } = SpellState.STATE_READY;
        public float CurrentCooldown { get; protected set; } = 0.0f;
        public float CurrentCastTime { get; protected set; } = 0.0f;
        public float CurrentChannelDuration { get; protected set; } = 0.0f;
        public uint FutureProjNetId { get; protected set; } = 0;
        public uint SpellNetId { get; protected set; } = 0;

        public Unit Target { get; private set; }
        public float X { get; private set; }
        public float Y { get; private set; }

        private CSharpScriptEngine _scriptEngine = Program.ResolveDependency<CSharpScriptEngine>();
        private Logger _logger = Program.ResolveDependency<Logger>();
        private Game _game = Program.ResolveDependency<Game>();

        private GameScript spellGameScript;

        public SpellData SpellData { get; private set; }

        public Spell(Champion owner, string spellName, byte slot)
        {
            Owner = owner;
            SpellName = spellName;
            Slot = slot;
            SpellData = _game.Config.ContentManager.GetSpellData(spellName);
            _scriptEngine = Program.ResolveDependency<CSharpScriptEngine>();

            //Set the game script for the spell
            spellGameScript = _scriptEngine.CreateObject<GameScript>("Spells", spellName);
            if(spellGameScript == null)
            {
                spellGameScript = new GameScriptEmpty();
            }
            //Activate spell - Notes: Deactivate is never called as spell removal hasn't been added
            spellGameScript.OnActivate(owner);
        }

        /// <summary>
        /// Called when the character casts the spell
        /// </summary>
        public virtual bool cast(float x, float y, Unit u = null, uint futureProjNetId = 0, uint spellNetId = 0)
        {
            X = x;
            Y = y;
            Target = u;
            this.FutureProjNetId = futureProjNetId;
            this.SpellNetId = spellNetId;

            if (SpellData.TargettingType == 1 && Target != null && Target.GetDistanceTo(Owner) > SpellData.CastRange[Level])
            {
                return false;
            }


            RunCastScript();

            if (SpellData.GetCastTime() > 0 && (SpellData.Flags & (int)SpellFlag.SPELL_FLAG_InstantCast) == 0)
            {
                Owner.setPosition(Owner.X, Owner.Y);//stop moving serverside too. TODO: check for each spell if they stop movement or not
                state = SpellState.STATE_CASTING;
                CurrentCastTime = SpellData.GetCastTime();
            }
            else
            {
                finishCasting();
            }
            return true;
        }

        
        private void RunCastScript()
        {
            spellGameScript.OnStartCasting(Owner, this, Target);
        }
        
        /// <summary>
        /// Called when the spell is finished casting and we're supposed to do things such as projectile spawning, etc.
        /// </summary>
        public virtual void finishCasting()
        {
            spellGameScript.OnFinishCasting(Owner, this, Target);
            if (SpellData.ChannelDuration[Level] == 0)
            {
                state = SpellState.STATE_COOLDOWN;

                CurrentCooldown = getCooldown();

                if (Slot < 4)
                {
                    _game.PacketNotifier.NotifySetCooldown(Owner, Slot, CurrentCooldown, getCooldown());
                }

                Owner.IsCastingSpell = false;
            }
        }

        /// <summary>
        /// Called when the spell is started casting and we're supposed to do things such as projectile spawning, etc.
        /// </summary>
        public virtual void channel()
        {
            state = SpellState.STATE_CHANNELING;
            CurrentChannelDuration = SpellData.ChannelDuration[Level];
        }

        /// <summary>
        /// Called when the character finished channeling
        /// </summary>
        public virtual void finishChanneling()
        {
            state = SpellState.STATE_COOLDOWN;

            CurrentCooldown = getCooldown();

            if (Slot < 4)
            {
                _game.PacketNotifier.NotifySetCooldown(Owner, Slot, CurrentCooldown, getCooldown());
            }

            Owner.IsCastingSpell = false;
        }

        /// <summary>
        /// Called every diff milliseconds to update the spell
        /// </summary>
        public virtual void update(float diff)
        {
            switch (state)
            {
                case SpellState.STATE_READY:
                    break;
                case SpellState.STATE_CASTING:
                    Owner.IsCastingSpell = true;
                    CurrentCastTime -= diff / 1000.0f;
                    if (CurrentCastTime <= 0)
                    {
                        finishCasting();
                        if(SpellData.ChannelDuration[Level] > 0)
                        {
                            channel();
                        }
                    }
                    break;
                case SpellState.STATE_COOLDOWN:
                    CurrentCooldown -= diff / 1000.0f;
                    if (CurrentCooldown < 0)
                    {
                        state = SpellState.STATE_READY;
                    }
                    break;
                case SpellState.STATE_CHANNELING:
                    CurrentChannelDuration -= diff / 1000.0f;
                    if(CurrentChannelDuration <= 0)
                    {
                        finishChanneling();
                    }
                    break;
            }
        }

        /// <summary>
        /// Called by projectiles when they land / hit, this is where we apply damage/slows etc.
        /// </summary>
        public void applyEffects(Unit u, Projectile p = null)
        {
            if (SpellData.HaveHitEffect && !string.IsNullOrEmpty(SpellData.HitEffectName))
            {
                ApiFunctionManager.AddParticleTarget(Owner, SpellData.HitEffectName, u);
            }

            spellGameScript.ApplyEffects(Owner, u, this, p);
        }

        public void AddProjectile(string nameMissile, float toX, float toY, bool isServerOnly = false)
        {
            var p = new Projectile(
                Owner.X,
                Owner.Y,
                (int) SpellData.LineWidth,
                Owner,
                new Target(toX, toY),
                this,
                SpellData.MissileSpeed,
                nameMissile,
                SpellData.Flags
            );
            _game.Map.AddObject(p);
            if (!isServerOnly)
            {
                _game.PacketNotifier.NotifyProjectileSpawn(p);
            }
        }

        public void AddProjectileTarget(string nameMissile, Target target, bool isServerOnly = false)
        {
            var p = new Projectile(
                Owner.X,
                Owner.Y,
                (int)SpellData.LineWidth,
                Owner,
                target,
                this,
                SpellData.MissileSpeed,
                nameMissile,
                SpellData.Flags
            );
            _game.Map.AddObject(p);
            if (!isServerOnly)
            {
                _game.PacketNotifier.NotifyProjectileSpawn(p);
            }
        }

        public void AddLaser(float toX, float toY, bool affectAsCastIsOver = true)
        {
            var l = new Laser(
                Owner.X,
                Owner.Y,
                (int)SpellData.LineWidth,
                Owner,
                new Target(toX, toY),
                this,
                SpellData.Flags,
                affectAsCastIsOver
            );
            _game.Map.AddObject(l);
        }

        public void spellAnimation(string animName, Unit target)
        {
            _game.PacketNotifier.NotifySpellAnimation(target, animName);
        }

        /// <returns>spell's unique ID</returns>
        public int getId()
        {
            return (int)HashFunctions.HashString(SpellName);
        }

        public string getStringForSlot()
        {
            switch (Slot)
            {
                case 0:
                    return "Q";
                case 1:
                    return "W";
                case 2:
                    return "E";
                case 3:
                    return "R";
                case 14:
                    return "Passive";
            }

            return "undefined";
        }

        /**
         * TODO : Add in CDR % from champion's stat
         */
        public float getCooldown()
        {
            return SpellData.Cooldown[Level];
        }

        public virtual void levelUp()
        {
            if (Level <= 5)
            {
                ++Level;
            }
            if (Slot < 4)
            {
                Owner.GetStats().ManaCost[Slot] = SpellData.ManaCost[Level];
            }
        }

        public void SetCooldown(byte slot, float newCd)
        {
            var targetSpell = Owner.Spells[slot];

            if (newCd <= 0)
            {
                _game.PacketNotifier.NotifySetCooldown(Owner, slot, 0, 0);
                targetSpell.state = SpellState.STATE_READY;
                targetSpell.CurrentCooldown = 0;
            }
            else
            {
                _game.PacketNotifier.NotifySetCooldown(Owner, slot, newCd, targetSpell.getCooldown());
                targetSpell.state = SpellState.STATE_COOLDOWN;
                targetSpell.CurrentCooldown = newCd;
            }
        }

        public void LowerCooldown(byte slot, float lowerValue)
        {
            SetCooldown(slot, Owner.Spells[slot].CurrentCooldown - lowerValue);
        }
    }
}
