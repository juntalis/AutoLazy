﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Xml.Schema;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using FieldAttributes = Mono.Cecil.FieldAttributes;

namespace AutoLazy.Fody
{
    internal class DoubleCheckedLockingWeaver
    {
        readonly MethodDefinition _method;
        readonly VisitorContext _context;
        readonly TypeReference _objRef;
        readonly MethodReference _objCtorRef;
        TypeReference _valueWrapper;
        FieldReference _valueWrapperField;
        MethodReference _valueWrapperCtor;
        FieldDefinition _valueField;
        FieldDefinition _syncRootField;

        public DoubleCheckedLockingWeaver(MethodDefinition method, VisitorContext context)
        {
            _method = method;
            _context = context;
            _objRef = method.Module.Import(typeof(object));
            _objCtorRef = method.Module.Import(new MethodReference(".ctor", method.Module.TypeSystem.Void, _objRef)
            {
                HasThis = true,
            });
        }

        private bool Validate()
        {
            var valid = true;
            if (_method.Parameters.Count > 0)
            {
                _context.LogError("[Lazy] methods may not have any parameters.", _method);
                valid = false;
            }
            if (_method.ReturnType.MetadataType == MetadataType.Void)
            {
                _context.LogError("[Lazy] methods must have a non-void return type.", _method);
                valid = false;
            }
            if (_method.HasGenericParameters)
            {
                _context.LogError("[Lazy] is not supported on generic methods.", _method);
                valid = false;
            }
            var bannedPropertyMethods =
                from prop in _method.DeclaringType.Properties
                where prop.SetMethod != null
                select prop.GetMethod;
            if (bannedPropertyMethods.Contains(_method))
            {
                _context.LogError("[Lazy] properties may not have a setter.", _method);
            }
            return valid;
        }

        public bool Instrument()
        {
            if (Validate())
            {
                if (_method.ReturnType.IsValueType || _method.ReturnType.IsGenericParameter)
                {
                    InitializeValueWrapper();
                }
                CreateFields();
                InitializeFields();
                WriteInstructions();
                return true;
            }
            return false;
        }

        static int _wrapperCounter;

        private void InitializeValueWrapper()
        {
            var type = _method.DeclaringType;
            var wrapperType = type.NestedTypes.FirstOrDefault(t =>
                t.Name.StartsWith("AutoLazy$")
                && t.Fields.Count == 1
                && t.Fields[0].FieldType == _method.ReturnType);
            if (wrapperType == null)
            {
                var typeAttributes = TypeAttributes.Class
                    | TypeAttributes.NestedPrivate
                    | TypeAttributes.Sealed
                    | TypeAttributes.BeforeFieldInit;
                wrapperType = new TypeDefinition(string.Empty, "AutoLazy$" + _method.ReturnType.Name + "$Wrapper" + ++_wrapperCounter, typeAttributes, _objRef);
                type.NestedTypes.Add(wrapperType);
                FieldDefinition field;
                if (_method.ReturnType.IsGenericParameter)
                {
                    var param = new GenericParameter("TInner", wrapperType);
                    wrapperType.GenericParameters.Add(param);
                    field = new FieldDefinition("Value", FieldAttributes.Public, param);
                    wrapperType.Fields.Add(field);
                }
                else
                {
                    field = new FieldDefinition("Value", FieldAttributes.Public, _method.ReturnType);
                    wrapperType.Fields.Add(field);
                }
                var ctor = new MethodDefinition(".ctor", MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, _method.Module.TypeSystem.Void);
                wrapperType.Methods.Add(ctor);
                var il = ctor.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, _objCtorRef);
                il.Emit(OpCodes.Ret);
            }
            if (_method.ReturnType.IsGenericParameter)
            {
                _valueWrapper = new GenericInstanceType(wrapperType) { GenericArguments = { _method.ReturnType } };
                _valueWrapperCtor = new MethodReference(".ctor", _method.Module.TypeSystem.Void, _valueWrapper)
                {
                    HasThis = true
                };
                _valueWrapperField = new FieldReference("Value", _method.ReturnType, _valueWrapper);
            }
            else
            {
                _valueWrapper = wrapperType;
                _valueWrapperCtor = wrapperType.Methods[0];
                _valueWrapperField = wrapperType.Fields[0];
            }
        }

        private void CreateFields()
        {
            var fieldAttributes = FieldAttributes.Private;
            if (_method.IsStatic) fieldAttributes |= FieldAttributes.Static;
            var fieldType = _valueWrapper ?? _method.ReturnType;
            _valueField = new FieldDefinition(_method.Name + "$Value", fieldAttributes, fieldType);
            _method.DeclaringType.Fields.Add(_valueField);

            _syncRootField = new FieldDefinition(_method.Name + "$SyncRoot", fieldAttributes | FieldAttributes.InitOnly, _objRef);
            _method.DeclaringType.Fields.Add(_syncRootField);
        }

        private void InitializeFields()
        {
            if (_method.IsStatic)
            {
                var ctor = _method.DeclaringType.GetOrCreateStaticConstructor();
                InitializeFields(ctor);
            }
            else
            {
                foreach (var ctor in _method.DeclaringType.GetConstructors().Where(c => !c.IsStatic))
                {
                    InitializeFields(ctor);
                }
            }
        }

        private void InitializeFields(MethodDefinition ctor)
        {
            var il = ctor.Body.GetILProcessor();
            var start = ctor.Body.Instructions.First();
            if (!_method.IsStatic) il.InsertBefore(start, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(start, il.Create(OpCodes.Newobj, _objCtorRef));
            il.InsertBefore(start, il.CreateStore(_syncRootField));
        }

        private void WriteInstructions()
        {
            var bodyInstructions = _method.Body.Instructions.ToList();
            foreach (var instruction in bodyInstructions.Where(i => i.OpCode == OpCodes.Ret))
            {
                instruction.OpCode = OpCodes.Nop;
            }
            _method.Body.Instructions.Clear();
            var il = _method.Body.GetILProcessor();
            var result = new VariableDefinition(_valueField.FieldType);
            var val = new VariableDefinition(_method.ReturnType);
            _method.Body.InitLocals = true;
            _method.Body.Variables.Add(result);
            _method.Body.Variables.Add(val);
            if (!_method.IsStatic) il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Volatile);
            il.EmitLoad(_valueField);
            il.Emit(OpCodes.Stloc, result);
            il.Emit(OpCodes.Ldloc, result);
            using (il.BranchIfTrue())
            {
                il.EmitLock(() =>
                {
                    if (!_method.IsStatic) il.Emit(OpCodes.Ldarg_0);
                    il.EmitLoad(_syncRootField);
                }, () =>
                {
                    if (!_method.IsStatic) il.Emit(OpCodes.Ldarg_0);
                    il.EmitLoad(_valueField);
                    il.Emit(OpCodes.Stloc, result);
                    il.Emit(OpCodes.Ldloc, result);
                    using (il.BranchIfTrue())
                    {
                        foreach (var instruction in bodyInstructions)
                        {
                            _method.Body.Instructions.Add(instruction);
                        }
                        if (_valueWrapper != null)
                        {
                            il.Emit(OpCodes.Stloc, val);
                            il.Emit(OpCodes.Newobj, _valueWrapperCtor);
                            il.Emit(OpCodes.Dup);
                            il.Emit(OpCodes.Ldloc, val);
                            il.Emit(OpCodes.Stfld, _valueWrapperField);
                        }
                        il.Emit(OpCodes.Stloc, result);
                        if (!_method.IsStatic) il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldloc, result);
                        il.Emit(OpCodes.Volatile);
                        il.EmitStore(_valueField);
                    }
                });
            }
            il.Emit(OpCodes.Ldloc, result);
            if (_valueWrapper != null)
            {
                il.Emit(OpCodes.Ldfld, _valueWrapperField);
            }
            il.Emit(OpCodes.Ret);
        }
    }
}
