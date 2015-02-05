﻿using System;
using System.Collections.Generic;
using Octopus.Shared.Variables;

namespace Octopus.Shared.Endpoints
{
    public abstract class AgentlessEndpoint : Endpoint
    {
        protected AgentlessEndpoint(IDictionary<string, Variable> raw)
            : base(raw)
        {
        }
    }
}
