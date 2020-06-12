﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Quantum.QsCompiler.DataTypes;
using Microsoft.Quantum.QsCompiler.DependencyAnalysis;
using Microsoft.Quantum.QsCompiler.SyntaxTokens;
using Microsoft.Quantum.QsCompiler.SyntaxTree;
using Microsoft.Quantum.QsCompiler.Transformations.Core;


// ToDo: Review access modifiers

namespace Microsoft.Quantum.QsCompiler.Transformations.CallGraphWalker
{
    using ExpressionKind = QsExpressionKind<TypedExpression, Identifier, ResolvedType>;
    using TypeParameterResolutions = ImmutableDictionary<Tuple<QsQualifiedName, NonNullable<string>>, ResolvedType>;

    /// <summary>
    /// This transformation walks through the compilation without changing it, building up a call graph as it does.
    /// This call graph is then returned to the user.
    /// </summary>
    public static class BuildCallGraph
    {
        public static CallGraph Apply(QsCompilation compilation)
        {
            var globals = compilation.Namespaces.GlobalCallableResolutions();
            var entryPointNodes = compilation.EntryPoints.Select(name =>
                new CallGraphNode(name, QsSpecializationKind.QsBody, QsNullable<ImmutableArray<ResolvedType>>.Null));
            var walker = new BuildGraph(globals, entryPoints: entryPointNodes); 

            if (compilation.EntryPoints.Any())
            {
                while (walker.SharedState.RequestStack.TryPop(out var currentRequest))
                {
                    // The current request must be added before it is processed to prevent
                    // self-references from duplicating on the stack.
                    walker.SharedState.ResolvedCallableSet.Add(currentRequest);

                    walker.SharedState.CurrentCaller = currentRequest;
                    if (!walker.SharedState.Callables.TryGetValue(currentRequest.CallableName, out var decl))
                        throw new ArgumentException($"Couldn't find definition for callable {currentRequest.CallableName}", nameof(currentRequest));

                    // FIXME: FIND THE RIGHT SPECIALIZATION BUNDLE
                    var relevantSpecs = decl.Specializations
                        .Where(s => s.Kind == currentRequest.Kind && s.TypeArguments.IsNull);
                    if (relevantSpecs.Count() != 1)
                        throw new ArgumentException($"Could not identify a suitable {currentRequest.Kind} specialization for {currentRequest.CallableName}"); 
                    
                    foreach (var spec in relevantSpecs)
                    {
                        walker.Namespaces.OnSpecializationImplementation(spec.Implementation);
                    }
                }
            }
            else
            {
                // ToDo: can be replaced by walker.Apply(compilation) once master is merged in
                foreach (var ns in compilation.Namespaces)
                {
                    walker.Namespaces.OnNamespace(ns);
                }
            }

            return walker.SharedState.Graph;
        }

        private class BuildGraph : SyntaxTreeTransformation<TransformationState>
        {
            public BuildGraph(ImmutableDictionary<QsQualifiedName, QsCallable> callables, IEnumerable<CallGraphNode> entryPoints = null) 
            : base(new TransformationState(callables, entryPoints))
            {
                this.Namespaces = new NamespaceTransformation(this);
                this.Statements = new StatementTransformation<TransformationState>(this, TransformationOptions.NoRebuild);
                this.StatementKinds = new StatementKindTransformation<TransformationState>(this, TransformationOptions.NoRebuild);
                this.Expressions = new ExpressionTransformation(this);
                this.ExpressionKinds = new ExpressionKindTransformation(this);
                this.Types = new TypeTransformation<TransformationState>(this, TransformationOptions.Disabled);
            }
        }

        /// <summary>
        /// Gets the type parameter names for the given callable. 
        /// Throws an ArgumentException if any of its type parameter names is invalid. 
        /// </summary>
        private static ImmutableArray<NonNullable<string>> GetTypeParameterNames(QsQualifiedName callable, ResolvedSignature signature)
        {
            var typeParams = signature.TypeParameters.Select(p =>
                p is QsLocalSymbol.ValidName name ? name.Item
                : throw new ArgumentException($"invalid type parameter name for callable {callable}"));
            return typeParams.ToImmutableArray();
        }

        /// <summary>
        /// Returns the type arguments for the given callable according to the given type parameter resolutions.
        /// Throws an ArgumentException if any of its type parameter names is invalid or
        /// if the resolution is missing for any of the type parameters of the callable. 
        /// </summary>
        private static ImmutableArray<ResolvedType> ConcreteTypeArguments(QsCallable callable, TypeParameterResolutions typeParamRes)
        {
            var typeArgs = GetTypeParameterNames(callable.FullName, callable.Signature).Select(p =>
                typeParamRes.TryGetValue(Tuple.Create(callable.FullName, p), out var res) ? res
                : throw new ArgumentException($"unresolved type parameter {p.Value} for {callable.FullName}"));
            return typeArgs.ToImmutableArray();

        }

        private class TransformationState
        {
            internal bool IsInCall = false;
            internal bool HasAdjointDependency = false;
            internal bool HasControlledDependency = false;

            internal IEnumerable<TypeParameterResolutions> TypeParameterResolutions;
            internal TypeParameterResolutions CallerTypeParameterResolutions;

            private CallGraphNode _CurrentCaller;
            internal CallGraphNode CurrentCaller
            {
                get => _CurrentCaller;
                set
                { 
                    this.CallerTypeParameterResolutions = ImmutableDictionary<Tuple<QsQualifiedName, NonNullable<string>>, ResolvedType>.Empty;
                    _CurrentCaller = value;
                }
            }

            internal readonly CallGraph Graph;
            internal readonly bool IsLimitedToEntryPoints;
            internal readonly Stack<CallGraphNode> RequestStack; 
            internal readonly HashSet<CallGraphNode> ResolvedCallableSet;
            internal readonly ImmutableDictionary<QsQualifiedName, QsCallable> Callables;

            internal void PushEdge(CallGraphNode called, TypeParameterResolutions typeParamRes)
            {
                this.Graph.AddDependency(this.CurrentCaller, called, typeParamRes);
                if (this.IsLimitedToEntryPoints
                    && !this.RequestStack.Contains(called)
                    && !this.ResolvedCallableSet.Contains(called))
                {
                    // If we are not processing all elements, then we need to keep track of what elements
                    // have been processed, and which elements still need to be processed.
                    this.RequestStack.Push(called);
                }
            }

            internal TransformationState(ImmutableDictionary<QsQualifiedName, QsCallable> callables,
                IEnumerable<CallGraphNode> entryPoints = null, IEnumerable<CallGraphNode> resolved = null)
            {
                this.Callables = callables ?? throw new ArgumentNullException(nameof(callables));
                this.RequestStack = new Stack<CallGraphNode>(entryPoints ?? Array.Empty<CallGraphNode>());
                this.ResolvedCallableSet = new HashSet<CallGraphNode>(resolved ?? Array.Empty<CallGraphNode>());

                this.IsLimitedToEntryPoints = this.RequestStack.Any();
                this.TypeParameterResolutions = new List<TypeParameterResolutions>();
                this.CallerTypeParameterResolutions = ImmutableDictionary<Tuple<QsQualifiedName, NonNullable<string>>, ResolvedType>.Empty;
                this.Graph = new CallGraph();
            }
        }

        private class NamespaceTransformation : NamespaceTransformation<TransformationState>
        {
            public NamespaceTransformation(SyntaxTreeTransformation<TransformationState> parent) : base(parent, TransformationOptions.NoRebuild) { }

            public override QsSpecialization OnSpecializationDeclaration(QsSpecialization spec)
            {
                if (spec.TypeArguments.IsValue && spec.Signature.TypeParameters.Length != spec.TypeArguments.Item.Length)
                    throw new ArgumentException($"The number of type arguments for the {spec.Parent} does not match the number of type parameters.");

                SharedState.CurrentCaller = new CallGraphNode(spec.Parent, spec.Kind, spec.TypeArguments);
                if (spec.TypeArguments.IsValue)
                {
                    var typeParamNames = GetTypeParameterNames(spec.Parent, spec.Signature);
                    SharedState.CallerTypeParameterResolutions = spec.TypeArguments.Item
                        .Where(res => !res.Resolution.IsMissingType)
                        .Select((res, idx) => (Tuple.Create(spec.Parent, typeParamNames[idx]), res))
                        .ToImmutableDictionary(kv => kv.Item1, kv => kv.Item2);
                }

                return base.OnSpecializationDeclaration(spec);
            }
        }

        private class ExpressionTransformation : ExpressionTransformation<TransformationState>
        {
            public ExpressionTransformation(SyntaxTreeTransformation<TransformationState> parent) : base(parent, TransformationOptions.NoRebuild) { }

            public override TypedExpression OnTypedExpression(TypedExpression ex)
            {
                if (ex.TypeParameterResolutions.Any())
                {
                    SharedState.TypeParameterResolutions = SharedState.TypeParameterResolutions.Prepend(ex.TypeParameterResolutions);
                }
                return base.OnTypedExpression(ex);
            }
        }

        private class ExpressionKindTransformation : ExpressionKindTransformation<TransformationState>
        {
            public ExpressionKindTransformation(SyntaxTreeTransformation<TransformationState> parent) : base(parent, TransformationOptions.NoRebuild) { }

            public override ExpressionKind OnCallLikeExpression(TypedExpression method, TypedExpression arg)
            {
                var contextInCall = SharedState.IsInCall;
                SharedState.IsInCall = true;
                this.Expressions.OnTypedExpression(method);
                SharedState.IsInCall = contextInCall;
                this.Expressions.OnTypedExpression(arg);
                return ExpressionKind.InvalidExpr;
            }

            public override ExpressionKind OnAdjointApplication(TypedExpression ex)
            {
                SharedState.HasAdjointDependency = !SharedState.HasAdjointDependency;
                var result = base.OnAdjointApplication(ex);
                SharedState.HasAdjointDependency = !SharedState.HasAdjointDependency;
                return result;
            }

            public override ExpressionKind OnControlledApplication(TypedExpression ex)
            {
                var contextControlled = SharedState.HasControlledDependency;
                SharedState.HasControlledDependency = true;
                var result = base.OnControlledApplication(ex);
                SharedState.HasControlledDependency = contextControlled;
                return result;
            }

            public override ExpressionKind OnIdentifier(Identifier sym, QsNullable<ImmutableArray<ResolvedType>> tArgs)
            {
                // FIXME: SET TYPE ARGS TO NULL IF WE ARE RESOLVING A LIBRARY

                if (sym is Identifier.GlobalCallable called)
                {
                    TypeParamUtils.TryCombineTypeResolutionsForTarget(
                        called.Item, out var typeParamRes, 
                        SharedState.TypeParameterResolutions.Append(SharedState.CallerTypeParameterResolutions).ToArray());
                    SharedState.TypeParameterResolutions = new List<TypeParameterResolutions>();

                    if (!SharedState.Callables.TryGetValue(called.Item, out var decl))
                        throw new ArgumentException($"Couldn't find definition for callable {called.Item}");

                    var resTypeArgsCalled = ConcreteTypeArguments(decl, typeParamRes);
                    var typeArgsCalled = resTypeArgsCalled.Length != 0
                        ? QsNullable<ImmutableArray<ResolvedType>>.NewValue(resTypeArgsCalled.ToImmutableArray())
                        : QsNullable<ImmutableArray<ResolvedType>>.Null;

                    if (SharedState.IsInCall)
                    {
                        var kind = QsSpecializationKind.QsBody;
                        if (SharedState.HasAdjointDependency && SharedState.HasControlledDependency)
                        {
                            kind = QsSpecializationKind.QsControlledAdjoint;
                        }
                        else if (SharedState.HasAdjointDependency)
                        {
                            kind = QsSpecializationKind.QsAdjoint;
                        }
                        else if (SharedState.HasControlledDependency)
                        {
                            kind = QsSpecializationKind.QsControlled;
                        }

                        SharedState.PushEdge(new CallGraphNode(called.Item, kind, typeArgsCalled), typeParamRes);
                    }
                    else
                    {
                        // The callable is being used in a non-call context, such as being
                        // assigned to a variable or passed as an argument to another callable,
                        // which means it could get a functor applied at some later time.
                        // We're conservative and add all 4 possible kinds.
                        SharedState.PushEdge(new CallGraphNode(called.Item, QsSpecializationKind.QsBody, typeArgsCalled), typeParamRes);
                        SharedState.PushEdge(new CallGraphNode(called.Item, QsSpecializationKind.QsControlled, typeArgsCalled), typeParamRes);
                        SharedState.PushEdge(new CallGraphNode(called.Item, QsSpecializationKind.QsAdjoint, typeArgsCalled), typeParamRes);
                        SharedState.PushEdge(new CallGraphNode(called.Item, QsSpecializationKind.QsControlledAdjoint, typeArgsCalled), typeParamRes);
                    }
                }

                return ExpressionKind.InvalidExpr;
            }
        }
    }
}