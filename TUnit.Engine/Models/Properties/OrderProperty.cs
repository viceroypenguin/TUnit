﻿using Microsoft.Testing.Platform.Extensions.Messages;

namespace TUnit.Engine.Models.Properties;

public class OrderProperty(int order) : IProperty
{
    public int Order { get; } = order;
}