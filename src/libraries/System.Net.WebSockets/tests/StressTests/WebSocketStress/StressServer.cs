// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace WebSocketStress;

internal class StressServer
{
    public EndPoint ServerEndpoint => throw new NotImplementedException();

    public StressServer(Configuration config) { }

    public void Start()
    {
    }

    public Task StopAsync() => Task.CompletedTask;
}

