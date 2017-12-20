// Copyright (c) Microsoft. All rights reserved.

#if DESKTOP

// StyleCop.Analyzers 1.0 doesn't do well with C# 7 () tuple literals.
#pragma warning disable SA1008 // Opening parenthesis must be spaced correctly
#pragma warning disable SA1009 // Closing parenthesis must be spaced correctly

namespace Microsoft.VisualStudio.Composition.Reflection
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;

    /// <summary>
    /// Manages the creation of a cache assembly.
    /// </summary>
    /// <remarks>
    /// This type is *not* thread-safe.
    /// </remarks>
    internal class CacheAssemblyBuilder
    {
        /// <summary>
        /// The module builder for the default module of the <see cref="AssemblyBuilder"/>.
        /// </summary>
        private readonly ModuleBuilder moduleBuilder;

        /// <summary>
        /// A dictionary of MEF-related types to the <see cref="TypeBuilder"/> instances that wrap them.
        /// </summary>
        private readonly Dictionary<Type, (TypeBuilder, TypeRef)> typeBuilders = new Dictionary<Type, (TypeBuilder, TypeRef)>();

        /// <summary>
        /// A dictionary of method wrappers.
        /// </summary>
        private readonly Dictionary<MethodBase, (MethodInfo, MethodRef)> methodBuilders = new Dictionary<MethodBase, (MethodInfo, MethodRef)>();

        private readonly Dictionary<FieldInfo, (MethodInfo, MethodRef)> fieldGetterBuilders = new Dictionary<FieldInfo, (MethodInfo, MethodRef)>();

        private readonly Dictionary<FieldInfo, (MethodInfo, MethodRef)> fieldSetterBuilders = new Dictionary<FieldInfo, (MethodInfo, MethodRef)>();

        /// <summary>
        /// Tracks adding skip visibility check attributes to the dynamic assembly.
        /// </summary>
        private readonly SkipClrVisibilityChecks skipVisibilityChecks;

        private readonly Resolver resolver;
        private readonly AssemblyName assemblyName;

        /// <summary>
        /// Initializes a new instance of the <see cref="CacheAssemblyBuilder"/> class.
        /// </summary>
        /// <param name="assemblyName">The assembly name for the generated assembly.</param>
        /// <param name="assemblyBuilder">The dynamic assembly builder.</param>
        /// <param name="moduleBuilder">The module builder for the default module of the <paramref name="assemblyBuilder"/>.</param>
        /// <param name="resolver">The resolver to use when loading types.</param>
        internal CacheAssemblyBuilder(AssemblyName assemblyName, AssemblyBuilder assemblyBuilder, ModuleBuilder moduleBuilder, Resolver resolver)
        {
            Requires.NotNull(assemblyBuilder, nameof(assemblyBuilder));
            Requires.NotNull(moduleBuilder, nameof(moduleBuilder));
            Requires.NotNull(resolver, nameof(resolver));

            this.assemblyName = assemblyName;
            this.resolver = resolver;
            this.moduleBuilder = moduleBuilder;
            this.skipVisibilityChecks = new SkipClrVisibilityChecks(assemblyBuilder, this.moduleBuilder);
        }

        internal ComposableCatalog Wrap(ComposableCatalog catalog)
        {
            // Note: This drops any DiscoveredParts and discovery errors in the original catalog.
            //       We can preserve that here if we need to.
            ComposableCatalog cachedCatalog = ComposableCatalog.Create(catalog.Resolver)
                .AddParts(catalog.Parts.Select(this.Wrap));

            // Complete the Type so it can be used and the assembly can be saved later.
            foreach (var tb in this.typeBuilders.Values)
            {
                if (!tb.Item1.IsCreated())
                {
                    tb.Item1.CreateTypeInfo();
                }
            }

            return cachedCatalog;
        }

        private ComposablePartDefinition Wrap(ComposablePartDefinition partDefinition)
        {
            var importingMembers = partDefinition.ImportingMembers.Select(this.Wrap).ToList();
            var exportingMembers = partDefinition.ExportingMembers.ToDictionary(
                kv => new MemberRef(this.Wrap(kv.Key.Resolve(), setValue: false).Item2),
                kv => kv.Value);
            var onImportsSatisfiedRef = partDefinition.OnImportsSatisfiedRef.IsEmpty ? default(MethodRef) : this.Wrap(partDefinition.OnImportsSatisfied).Item2;
            var importingConstructorRef = partDefinition.ImportingConstructorOrFactoryRef.IsEmpty ? default(MethodRef) : this.Wrap(partDefinition.ImportingConstructorOrFactory).Item2;

            return new ComposablePartDefinition(
                partDefinition.TypeRef,
                partDefinition.Metadata,
                partDefinition.ExportedTypes,
                exportingMembers,
                importingMembers,
                partDefinition.SharingBoundary,
                onImportsSatisfiedRef,
                importingConstructorRef,
                partDefinition.ImportingConstructorImports,
                partDefinition.CreationPolicy,
                partDefinition.ExtraInputAssemblies,
                partDefinition.IsSharingBoundaryInferred);
        }

        private (MemberInfo, MethodRef) Wrap(MemberInfo memberInfo, bool setValue = true)
        {
            switch (memberInfo)
            {
                case ConstructorInfo ctor:
                    return this.Wrap(ctor);
                case MethodInfo methodInfo:
                    return this.Wrap(methodInfo);
                case PropertyInfo property when setValue:
                    return this.Wrap(property.SetMethod);
                case PropertyInfo property when !setValue:
                    return this.Wrap(property.GetMethod);
                case FieldInfo field:
                    return this.Wrap(field, setValue);
                default:
                    throw new ArgumentException("Unsupported type: " + memberInfo.GetType().Name);
            }
        }

        private ImportDefinitionBinding Wrap(ImportDefinitionBinding importDefinitionBinding)
        {
            Requires.NotNull(importDefinitionBinding, nameof(importDefinitionBinding));

            if (importDefinitionBinding.ImportingMemberRef.IsEmpty)
            {
                // TODO: What do we do about importing parameters?
                throw new NotSupportedException();
            }

            var importingMember = new MemberRef(this.Wrap(importDefinitionBinding.ImportingMember, setValue: true).Item2);
            return new ImportDefinitionBinding(
                importDefinitionBinding.ImportDefinition,
                importDefinitionBinding.ComposablePartTypeRef,
                importingMember,
                importDefinitionBinding.ImportingSiteTypeRef,
                importDefinitionBinding.ImportingSiteTypeWithoutCollectionRef);
        }

        private (MethodInfo, MethodRef) Wrap(ConstructorInfo constructorInfo)
        {
            Requires.NotNull(constructorInfo, nameof(constructorInfo));

            if (!this.methodBuilders.TryGetValue(constructorInfo, out var tuple))
            {
                this.skipVisibilityChecks.SkipVisibilityChecksFor(constructorInfo);
                var (typeBuilder, typeRef) = this.GetTypeBuilderForMember(constructorInfo);

                ParameterInfo[] ctorParameters = constructorInfo.GetParameters();
                var (parameterTypes, parameterTypeRefs) = this.GetParameterTypes(ctorParameters);
                var methodBuilder = typeBuilder.DefineMethod(
                   "_ctor",
                   MethodAttributes.Static | MethodAttributes.HideBySig,
                   constructorInfo.DeclaringType,
                   parameterTypes);
                var methodRef = new MethodRef(
                    methodBuilder,
                    this.resolver,
                    parameterTypeRefs);
                this.methodBuilders[constructorInfo] = tuple = (methodBuilder, methodRef);

                var il = methodBuilder.GetILGenerator();
                for (int i = 0; i < ctorParameters.Length; i++)
                {
                    LoadArg(il, i);
                }

                il.Emit(OpCodes.Newobj, constructorInfo);
                il.Emit(OpCodes.Ret);
            }

            return tuple;
        }

        private (MethodInfo, MethodRef) Wrap(MethodInfo methodInfo)
        {
            Requires.NotNull(methodInfo, nameof(methodInfo));

            if (!this.methodBuilders.TryGetValue(methodInfo, out var tuple))
            {
                this.skipVisibilityChecks.SkipVisibilityChecksFor(methodInfo);
                var (typeBuilder, typeRef) = this.GetTypeBuilderForMember(methodInfo);

                ParameterInfo[] methodBaseParameters = methodInfo.GetParameters();
                int parameterCount = methodInfo.IsStatic ? methodBaseParameters.Length : methodBaseParameters.Length + 1;
                Type[] parameterTypes = new Type[parameterCount];
                int parameterIndex = 0;
                if (!methodInfo.IsStatic)
                {
                    parameterTypes[0] = methodInfo.DeclaringType;
                    parameterIndex++;
                }

                for (int i = 0; i < methodBaseParameters.Length; i++)
                {
                    parameterTypes[parameterIndex++] = methodBaseParameters[i].ParameterType;
                }

                var methodBuilder = typeBuilder.DefineMethod(
                   methodInfo.Name,
                   MethodAttributes.Static | MethodAttributes.HideBySig,
                   methodInfo.ReturnType,
                   parameterTypes);
                var methodRef = new MethodRef(
                    methodBuilder,
                    this.resolver,
                    parameterTypes.Select(p => TypeRef.Get(p, this.resolver)).ToImmutableArray());
                this.methodBuilders[methodInfo] = tuple = (methodBuilder, methodRef);

                var il = methodBuilder.GetILGenerator();
                for (int i = 0; i < parameterTypes.Length; i++)
                {
                    LoadArg(il, i);
                }

                il.Emit(methodInfo.IsStatic ? OpCodes.Call : OpCodes.Callvirt, methodInfo);

                il.Emit(OpCodes.Ret);
            }

            return tuple;
        }

        private (MemberInfo, MethodRef) Wrap(FieldInfo fieldInfo, bool setValue)
        {
            Requires.NotNull(fieldInfo, nameof(fieldInfo));

            var relevantAccessorDictionary = setValue ? this.fieldSetterBuilders : this.fieldGetterBuilders;
            if (!relevantAccessorDictionary.TryGetValue(fieldInfo, out var tuple))
            {
                this.skipVisibilityChecks.SkipVisibilityChecksFor(fieldInfo);
                var (typeBuilder, typeRef) = this.GetTypeBuilderForMember(fieldInfo);

                int parameterCount = fieldInfo.IsStatic ? 0 : 1;
                if (setValue)
                {
                    parameterCount++;
                }

                Type[] parameterTypes = new Type[parameterCount];
                int parameterIndex = 0;
                if (!fieldInfo.IsStatic)
                {
                    parameterTypes[parameterIndex++] = fieldInfo.DeclaringType;
                }

                if (setValue)
                {
                    parameterTypes[parameterIndex++] = fieldInfo.FieldType;
                }

                var methodBuilder = typeBuilder.DefineMethod(
                   (setValue ? "set_" : "get_") + fieldInfo.Name,
                   MethodAttributes.Static | MethodAttributes.HideBySig,
                   setValue ? null : fieldInfo.FieldType,
                   parameterTypes);
                var methodRef = new MethodRef(
                    methodBuilder,
                    this.resolver,
                    parameterTypes.Select(p => TypeRef.Get(p, this.resolver)).ToImmutableArray());
                relevantAccessorDictionary[fieldInfo] = tuple = (methodBuilder, methodRef);

                var il = methodBuilder.GetILGenerator();
                for (int i = 0; i < parameterTypes.Length; i++)
                {
                    LoadArg(il, i);
                }

                il.Emit(setValue ? OpCodes.Stfld : OpCodes.Ldfld, fieldInfo);

                il.Emit(OpCodes.Ret);
            }

            return tuple;
        }

        private (Type[], ImmutableArray<TypeRef>) GetParameterTypes(ParameterInfo[] parameters)
        {
            Requires.NotNull(parameters, nameof(parameters));

            var types = new Type[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                types[i] = parameters[i].ParameterType;
            }

            var typeRefs = types.Select(t => TypeRef.Get(t, this.resolver)).ToImmutableArray();

            return (types, typeRefs);
        }

        private static void LoadArg(ILGenerator il, int argIndex)
        {
            switch (argIndex)
            {
                case 0:
                    il.Emit(OpCodes.Ldarg_0);
                    break;
                case 1:
                    il.Emit(OpCodes.Ldarg_1);
                    break;
                case 2:
                    il.Emit(OpCodes.Ldarg_2);
                    break;
                case 3:
                    il.Emit(OpCodes.Ldarg_3);
                    break;
                default:
                    il.Emit(OpCodes.Ldarg, argIndex);
                    break;
            }
        }

        private (TypeBuilder, TypeRef) GetTypeBuilderForMember(MemberInfo memberToWrap)
        {
            Requires.NotNull(memberToWrap, nameof(memberToWrap));

            Type type = memberToWrap.DeclaringType.IsGenericType ? memberToWrap.DeclaringType.GetGenericTypeDefinition() : memberToWrap.DeclaringType;
            if (!this.typeBuilders.TryGetValue(type, out var tuple))
            {
                this.skipVisibilityChecks.SkipVisibilityChecksFor(memberToWrap);

                TypeBuilder typeBuilder;
                var typeAttributes = TypeAttributes.NotPublic | TypeAttributes.BeforeFieldInit;
                if (type.IsNested)
                {
                    // Define the nesting type.
                    var nestingType = this.GetTypeBuilderForMember(type).Item1;
                    typeBuilder = nestingType.DefineNestedType(type.Name, typeAttributes | TypeAttributes.NestedPrivate);
                }
                else
                {
                    typeBuilder = this.moduleBuilder.DefineType(type.FullName, typeAttributes);
                }

                if (type.IsGenericType)
                {
                    var genericTypeDefinition = type.GetGenericTypeDefinition();
                    var typeArgs = genericTypeDefinition.GetGenericArguments();
                    var typeParameterBuilders = typeBuilder.DefineGenericParameters(typeArgs.Select(a => a.Name).ToArray());
                    for (int i = 0; i < typeArgs.Length; i++)
                    {
                        var typeArg = typeArgs[i];
                        var builder = typeParameterBuilders[i];

                        builder.SetGenericParameterAttributes(typeArg.GenericParameterAttributes);

                        var constraints = typeArg.GetGenericParameterConstraints();
                        var baseTypeConstraint = constraints.SingleOrDefault(t => t.IsClass);
                        if (baseTypeConstraint != null)
                        {
                            builder.SetBaseTypeConstraint(baseTypeConstraint);
                        }

                        var interfaceConstraints = constraints.Where(t => t.IsInterface).ToArray();
                        if (interfaceConstraints.Length > 0)
                        {
                            builder.SetInterfaceConstraints(interfaceConstraints);
                        }
                    }
                }

                var typeRef = TypeRef.Get(
                    typeBuilder,
                    this.resolver,
                    this.assemblyName);
                this.typeBuilders[type] = tuple = (typeBuilder, typeRef);

                // TODO: suppress generation of the default constructor.
            }

            return tuple;
        }
    }
}

#endif
