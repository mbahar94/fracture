﻿using System;
using System.Collections.Generic;
using System.Threading;
using Squared.Util;

namespace Squared.Task {
    public class SchedulableGeneratorThunk : ISchedulable, IDisposable {
        public Func<object, IFuture> OnNextValue = null;

        IEnumerator<object> _Task;
        IFuture _Future;
        public IFuture WakeCondition;
        IFuture _WakePrevious = null;
        bool _WakeDiscardingResult = false;
        int _WakeFlag = 0;
        TaskScheduler _Scheduler;
        Action _Step, _QueueStep;
        OnComplete _QueueStepOnComplete;

        public override string ToString () {
            return String.Format("<Task {0} waiting on {1}>", _Task, WakeCondition);
        }

        public SchedulableGeneratorThunk (IEnumerator<object> task) {
            _Task = task;
            _QueueStep = QueueStep;
            _QueueStepOnComplete = QueueStepOnComplete;
            _Step = Step;
        }

        internal void CompleteWithResult (object result) {
            if (CheckForDiscardedError())
                return;

            _Future.Complete(result);
            Dispose();
        }

        internal void Abort (Exception ex) {
            if (_Future != null)
                _Future.Fail(ex);
            Dispose();
        }

        public void Dispose () {
            _WakePrevious = null;

            if (WakeCondition != null) {
                WakeCondition.Dispose();
                WakeCondition = null;
            }

            if (_Task != null) {
                _Task.Dispose();
                _Task = null;
            }

            if (_Future != null) {
                _Future.Dispose();
                _Future = null;
            }
        }

        void OnDisposed (IFuture _) {
            Dispose();
        }

        void ISchedulable.Schedule (TaskScheduler scheduler, IFuture future) {
            IEnumerator<object> task = _Task;
            _Future = future;
            _Scheduler = scheduler;
            _Future.RegisterOnDispose(this.OnDisposed);
            QueueStep();
        }

        void QueueStepOnComplete (IFuture f) {
            if (_WakeDiscardingResult && f.Failed) {
                Abort(f.Error);
                return;
            }

            if (WakeCondition != null) {
                _WakePrevious = WakeCondition;
                WakeCondition = null;
            }

            _Scheduler.QueueWorkItem(_Step);
        }

        void QueueStep () {
            _Scheduler.QueueWorkItem(_Step);
        }

        void ScheduleNextStepForSchedulable (ISchedulable value) {
            if (value is WaitForNextStep) {
                _Scheduler.AddStepListener(_QueueStep);
            } else if (value is Yield) {
                QueueStep();
            } else {
                var temp = _Scheduler.Start(value, TaskExecutionPolicy.RunWhileFutureLives);
                SetWakeCondition(temp, true);
                temp.RegisterOnComplete(_QueueStepOnComplete);
            }
        }

        bool CheckForDiscardedError () {
            if ((!_WakeDiscardingResult) && (_WakePrevious != null)) {
                bool shouldRethrow = (_WakePrevious.ErrorCheckFlag <= _WakeFlag);
                if (shouldRethrow && _WakePrevious.Failed) {
                    Abort(_WakePrevious.Error);
                    return true;
                }
            }

            return false;
        }

        void SetWakeCondition (IFuture f, bool discardingResult) {
            _WakePrevious = WakeCondition;

            if (CheckForDiscardedError())
                return;

            WakeCondition = f;
            _WakeDiscardingResult = discardingResult;
            if (f != null)
                _WakeFlag = f.ErrorCheckFlag;
        }

        void ScheduleNextStep (Object value) {
            if (CheckForDiscardedError())
                return;

            if (value is ISchedulable) {
                ScheduleNextStepForSchedulable(value as ISchedulable);
            } else if (value is NextValue) {
                NextValue nv = (NextValue)value;
                IFuture f = null;

                if (OnNextValue != null)
                    f = OnNextValue(nv.Value);

                if (f != null) {
                    SetWakeCondition(f, true);
                    f.RegisterOnComplete(_QueueStepOnComplete);
                } else {
                    QueueStep();
                }
            } else if (value is IFuture) {
                var f = (IFuture)value;
                SetWakeCondition(f, false);
                f.RegisterOnComplete(_QueueStepOnComplete);
            } else if (value is Result) {
                CompleteWithResult(((Result)value).Value);
            } else {
                if (value is IEnumerator<object>) {
                    ScheduleNextStepForSchedulable(new RunToCompletion(value as IEnumerator<object>, TaskExecutionPolicy.RunWhileFutureLives));
                } else if (value == null) {
                    QueueStep();
                } else {
                    throw new TaskYieldedValueException(_Task);
                }
            }
        }

        void Step () {
            if (_Task == null)
                return;

            if (WakeCondition != null) {
                _WakePrevious = WakeCondition;
                WakeCondition = null;
            }

            try {
                if (!_Task.MoveNext()) {
                    // Completed with no result
                    CompleteWithResult(null);
                    return;
                }

                // Disposed during execution
                if (_Task == null)
                    return;

                object value = _Task.Current;
                ScheduleNextStep(value);
            } catch (Exception ex) {
                Abort(ex);
            }
        }
    }
}
