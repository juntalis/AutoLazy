﻿using System;
using System.Linq;
using Mono.Cecil;

namespace AutoLazy.Fody
{
    internal interface IPropertyVisitor
    {
        void Visit(PropertyDefinition property, VisitorContext context);
    }
}
