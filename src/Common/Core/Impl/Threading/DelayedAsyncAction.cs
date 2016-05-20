﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Common.Core.Threading {

    public class DelayedAsyncAction {
        private readonly int _timeout;
        private readonly Timer _timer;

        /// <summary>
        /// DelayedAsyncAction invokes <paramref name="action"/> after specified <paramref name="timeout"/> when <see cref="Invoke"/> is called.
        /// If <see cref="Invoke"/> is called again before <paramref name="timeout"/>, previous invocation request is cancelled and <paramref name="action"/> will be invoked only once
        /// </summary>
        public DelayedAsyncAction(Func<Task> action, int timeout = 0) {
            if (action == null) {
                throw new ArgumentNullException(nameof(action));
            }
            if (timeout < 0) {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            _timeout = timeout;
            _timer = new Timer(Callback, action, -1, -1);
        }

        public void Invoke() {
            _timer.Change(_timeout, -1);
        }

        private static TimerCallback Callback { get; } = s => ((Func<Task>) s)().DoNotWait();
    }
}