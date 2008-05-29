﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Squared.Task;

namespace MUDServer {
    public interface IEntity {
        string Name {
            get;
        }

        string State {
            get;
        }

        string Description {
            get;
        }

        Location Location {
            get;
        }

        void NotifyEvent (EventType type, object evt);
    }


    public class EntityBase : IEntity, IDisposable {
        private static int _EntityCount;

        private Location _Location;
        private string _Name = null;
        private BlockingQueue<object> _EventQueue = new BlockingQueue<object>();
        private Future _ThinkTask;
        protected string _State = null;
        protected string _Description = null;

        public override string ToString () {
            return Description;
        }

        public string Description {
            get {
                return _Description ?? _Name;
            }
        }

        public virtual string State {
            get {
                return _State;
            }
        }

        public Location Location {
            get {
                return _Location;
            }
            set {
                if (_Name != null && _Location != null) {
                    _Location.Exit(this);
                }

                _Location = value;

                if (_Name != null && _Location != null) {
                    _Location.Enter(this);
                }
            }
        }

        public string Name {
            get {
                return _Name;
            }
            set {
                if (_Name == null) {
                    _Name = value;
                    _Location.Enter(this);
                } else {
                    throw new InvalidOperationException("An entity's name cannot be changed once it has been set");
                }
            }
        }

        protected virtual bool ShouldFilterNewEvent (EventType type, object evt) {
            return false;
        }

        public void NotifyEvent (EventType type, object evt) {
            if (!ShouldFilterNewEvent(type, evt))
                _EventQueue.Enqueue(evt);
        }

        protected Future GetNewEvent () {
            return _EventQueue.Dequeue();
        }

        protected static string GetDefaultName () {
            return String.Format("Entity{0}", _EntityCount++);
        }

        public EntityBase (Location location, string name) {
            if (location == null)
                throw new ArgumentNullException("location");
            _Name = name;
            Location = location;
            _ThinkTask = Program.Scheduler.Start(ThinkTask(), TaskExecutionPolicy.RunAsBackgroundTask);
        }

        protected virtual IEnumerator<object> ThinkTask () {
            yield return null;
        }

        public virtual void Dispose () {
            if (_ThinkTask != null) {
                _ThinkTask.Dispose();
                _ThinkTask = null;
            }
        }
    }

    public class CombatEntity : EntityBase {
        private bool _InCombat;
        private Future _CombatTask;
        private CombatEntity _CombatTarget = null;
        private double CombatPeriod;
        private int _CurrentHealth;
        private int _MaximumHealth;

        public bool InCombat {
            get {
                return _InCombat;
            }
        }

        public int CurrentHealth {
            get {
                return _CurrentHealth;
            }
        }

        public int MaximumHealth {
            get {
                return _MaximumHealth;
            }
        }

        public override string State {
            get {
                if (InCombat)
                    return String.Format("engaged in combat with {0}", _CombatTarget.Name);
                else if (_CurrentHealth <= 0)
                    return String.Format("lying on the ground, dead");
                else
                    return _State;
            }
        }

        public CombatEntity (Location location, string name)
            : base(location, name) {
            _InCombat = false;
            CombatPeriod = Program.RNG.NextDouble() * 4.0;
            _MaximumHealth = 20 + Program.RNG.Next(50);
            _CurrentHealth = _MaximumHealth;
        }

        public void Hurt (int damage) {
            if (_CurrentHealth <= 0)
                return;

            _CurrentHealth -= damage;
            if (_CurrentHealth <= 0) {
                Event.Send(new { Type = EventType.Death, Sender = this });
                _CurrentHealth = 0;
                _CombatTarget.EndCombat();
                EndCombat();
            }
        }

        public void StartCombat (CombatEntity target) {
            if (_InCombat)
                throw new InvalidOperationException("Attempted to start combat while already in combat.");

            _CombatTarget = target;
            _InCombat = true;
            _CombatTask = Program.Scheduler.Start(CombatTask(), TaskExecutionPolicy.RunAsBackgroundTask);
        }

        public void EndCombat () {
            if (!_InCombat)
                throw new InvalidOperationException("Attempted to end combat while not in combat.");

            _CombatTarget = null;
            _InCombat = false;
            _CombatTask.Dispose();
        }

        public virtual IEnumerator<object> CombatTask () {
            while (true) {
                yield return new Sleep(CombatPeriod);
                // Hitrate = 2/3
                // Damage = 2d6
                int damage = Program.RNG.Next(1, 6 - 1) + Program.RNG.Next(1, 6 - 1);
                if (Program.RNG.Next(0, 3) <= 1) {
                    Event.Send(new { Type = EventType.CombatHit, Sender = this, Target = _CombatTarget, WeaponName = "Longsword", Damage = damage });
                    _CombatTarget.Hurt(damage);
                } else {
                    Event.Send(new { Type = EventType.CombatMiss, Sender = this, Target = _CombatTarget, WeaponName = "Longsword" });
                }
            }
        }
    }
}