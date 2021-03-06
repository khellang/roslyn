﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.UnitTests
{
    public class SymbolIdTests : TestBase
    {
        [Fact]
        public void TestMemberDeclarations()
        {
            var source = @"

public class C
{
    public class B { };
    public delegate int D(int v);

    public int F;
    public B F2;
    public int P { get; set;}
    public B P2 { get; set; }
    public void M() { };
    public void M(int a) { };
    public void M(int a, string b) { };
    public void M(string a, int b) { };
    public void M(B b) { };
    public int M2() { return 0; }
    public int M2(int a) { return 0; }
    public int M2(int a, string b) { return 0; }
    public int M2(string a, int b) { return 0; }
    public B M3() { return default(B); }
    public int this[int index] { get { return 0; } }
    public int this[int a, int b] { get { return 0; } }
    public B this[B b] { get { return b; } }
    public event D E;
    public event D E2 { add; remove; }
}
";
            var compilation = GetCompilation(source);
            TestRoundTrip(GetDeclaredSymbols(compilation), compilation);
        }

        [Fact]
        public void TestNamespaceDeclarations()
        {
            var source = @"
namespace N { }
namespace A.B { }
namespace A { namespace B.C { } }
namespace A { namespace B { namespace C { } } }
namespace A { namespace N { } }
";
            var compilation = GetCompilation(source);
            var symbols = GetDeclaredSymbols(compilation);
            Assert.Equal(5, symbols.Count);
            TestRoundTrip(symbols, compilation);
        }

        [Fact]
        public void TestConstructedTypeReferences()
        {
            var source = @"
using System.Collections.Generic;

public class C
{
    public List<int> G1;
    public List<List<int>> G2;
    public Dictionary<string, int> G3;
    public int[] A1;
    public int[,] A2;
    public int[,,] A3;
    public List<int>[] A4;
    public int* P1;
    public int** p2;
}
";
            var compilation = GetCompilation(source);
            TestRoundTrip(GetDeclaredSymbols(compilation).OfType<IFieldSymbol>().Select(fs => fs.Type), compilation);
        }

        [Fact]
        public void TestErrorTypeReferences()
        {
            var source = @"
using System.Collections.Generic;

public class C
{
    public T E1;
    public List<T> E2;
    public T<int> E3;
    public T<A> E4;
}
";
            var compilation = GetCompilation(source);
            TestRoundTrip(GetDeclaredSymbols(compilation).OfType<IFieldSymbol>().Select(fs => fs.Type), compilation, s => s.ToDisplayString());
        }

        [Fact]
        public void TestParameterDeclarations()
        {
            var source = @"
using System.Collections.Generic;

public class C
{
    public void M(int p) { }
    public void M(int p1, int p2) { }
    public void M<T>(T p) { }
    public void M<T>(T[] p) { }
    public void M<T>(List<T> p) { }
    public void M<T>(T* p) { }
    public void M(ref int p)  { }
}
";
            var compilation = GetCompilation(source);
            TestRoundTrip(GetDeclaredSymbols(compilation).OfType<IMethodSymbol>().SelectMany(ms => ms.Parameters), compilation);
        }

        [Fact]
        public void TestTypeParameters()
        {
            var source = @"
public class C
{
    public void M() { }
    public void M<A>(A a) { }
    public void M<A>(int i) { }
    public void M<A, B>(A a, B b) { }
    public void M<A, B>(A a, int i) { }
    public void M<A, B>(int i, B b) { }
    public void M<A, B>(B b, A a) { }
    public void M<A, B>(B b, int i) { }
    public void M<A, B>(int i, A a) { }
    public void M<A, B>(int i, int j) { }
    public void M(C c) { }
    public int GetInt() { return 0 ; }
    public A GetA<A>(A a) { return a; }
    public A GetA<A, B>(A a, B b) { return a; }
    public B GetB<A, B>(A a, B b) { return b; }
    publi C GetC() { return default(C); }
}

public class C<T>
{
    public void M() { }
    public void M(T t) { }
    public void M<A>(A a) { }
    public void M<A>(T t, A a) { }
    public void M<A, B>(A a, B b) { }
    public void M<A, B>(B b, A a) { }
    public void M(C<T> c) { }
    public void M(C<int> c) { }
    public T GetT() { return default(T); }
    public C<T> GetCT() { return default(C<T>); }
    public C<int> GetCInt() { return default(C<int>); }
    public C<A> GetCA<A>() { return default(C<A>); }
}

public class C<S, T>
{
    public void M() { }
    public void M(T t, S s) { }
    public void M<A>(A a) { }
    public void M<A>(T t, S s, A a) { }
    public void M<A>(A a, T t, S s) { }
    public void M<A, B>(A a, B b) { }
    public void M<A, B>(T t, S s, A a, B b) { }
    public void M<A, B>(A a, B b, T t, S s) { }
    public T GetT() { return default(T); } 
    public S GetS() { return default(S); }
    public C<S, T> GetCST() { return default(C<S,T>); }
    public C<T, S> GetCTS() { return default(C<T, S>); }
    public C<T, A> GetCTA<A>() { return default(C<T, A>); }
}
";

            var compilation = GetCompilation(source);
            TestRoundTrip(GetDeclaredSymbols(compilation), compilation);
        }

        [Fact]
        public void TestLocals()
        {
            var source = @"
using System.Collections.Generic;

public class C
{
    public void M() {
        int a, b;
        if (a > b) {
           int c = a + b;
        }

        {
            string d = "";
        }

        {
            double d = 0.0;
        }

        {
            bool d = false;
        }

        var q = new { };
    }
}
";
            var compilation = GetCompilation(source);
            var symbols = GetDeclaredSymbols(compilation).OfType<IMethodSymbol>().SelectMany(ms => GetInteriorSymbols(ms, compilation).OfType<ILocalSymbol>()).ToList();
            Assert.Equal(7, symbols.Count);
            TestRoundTrip(symbols, compilation);
        }

        [Fact]
        public void TestLabels()
        {
            var source = @"
using System.Collections.Generic;

public class C
{
    public void M() {
        start: goto end;
        end: goto start;
        end: ; // duplicate label
        }
    }
}
";
            var compilation = GetCompilation(source);
            var symbols = GetDeclaredSymbols(compilation).OfType<IMethodSymbol>().SelectMany(ms => GetInteriorSymbols(ms, compilation).OfType<ILabelSymbol>()).ToList();
            Assert.Equal(3, symbols.Count);
            TestRoundTrip(symbols, compilation);
        }

        [Fact]
        public void TestRangeVariables()
        {
            var source = @"
using System.Collections.Generic;

public class C
{
    public void M() {
        int[] xs = new int[] { 1, 2, 3, 4 };
        
        {
            var q = from x in xs where x > 2 select x;
        }

        {
            var q2 = from x in xs where x < 4 select x;
        }
    }
}
";
            var compilation = GetCompilation(source);
            var symbols = GetDeclaredSymbols(compilation).OfType<IMethodSymbol>().SelectMany(ms => GetInteriorSymbols(ms, compilation).OfType<IRangeVariableSymbol>()).ToList();
            Assert.Equal(2, symbols.Count);
            TestRoundTrip(symbols, compilation);
        }

        [Fact]
        public void TestMethodReferences()
        {
            var source = @"
public class C
{
    public void M() { }
    public void M(int x) { }
    public void M2<T>() { }
    public void M2<T>(T t) { }
    public T M3<T>(T t) { return default(T); }
   
    public void Test() {
        M():
        M(0);
        M2<string>();
        M2(0);
        var tmp = M3(0);
    }
}
";
            var compilation = GetCompilation(source);
            var tree = compilation.SyntaxTrees.First();
            var model = compilation.GetSemanticModel(tree);
            var symbols = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Select(s => model.GetSymbolInfo(s).Symbol).ToList();
            Assert.True(symbols.Count > 0);
            TestRoundTrip(symbols, compilation);
        }

        [Fact]
        public void TestExtensionMethodReferences()
        {
            var source = @"
using System.Collections.Generic;

public static class E 
{
    public static void Z(this C c) { }
    public static void Z(this C c, int x) { }
    public static void Z<T>(this T t, string y) { }
    public static void Z<T>(this T t, T t2) { }
    public static void Y<T, S>(this T t, S other) { }
}

public class C
{
    public void M() {
        this.Z();
        this.Z(1);
        this.Z(""test"");
        this.Z(this);
        this.Y(1.0);
    }
}
";
            var compilation = GetCompilation(source);
            var tree = compilation.SyntaxTrees.First();
            var model = compilation.GetSemanticModel(tree);
            var symbols = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Select(s => model.GetSymbolInfo(s).Symbol).ToList();
            Assert.True(symbols.Count > 0);
            Assert.True(symbols.All(s => s.IsReducedExtension()));
            TestRoundTrip(symbols, compilation);
        }

        [Fact]
        public void TestAliasSymbols()
        {
            var source = @"
using G=System.Collections.Generic;
using GL=System.Collections.Generic.List<int>;

public class C
{
    public G.List<int> F;
    public GL F2;
}
";
            var compilation = GetCompilation(source);
            var tree = compilation.SyntaxTrees.First();
            var model = compilation.GetSemanticModel(tree);

            var symbols = tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>().Select(s => model.GetDeclaredSymbol(s)).ToList();
            Assert.Equal(2, symbols.Count);
            Assert.NotNull(symbols[0]);
            Assert.True(symbols[0] is IAliasSymbol);
            TestRoundTrip(symbols, compilation);

            var refSymbols = GetDeclaredSymbols(compilation).OfType<IFieldSymbol>().Select(f => f.Type).ToList();
            Assert.Equal(2, refSymbols.Count);
            TestRoundTrip(refSymbols, compilation);
        }

        [Fact]
        public void TestDynamicSymbols()
        {
            var source = @"
public class C
{
    public dynamic F;
    public dynamic[] F2;
}
";
            var compilation = GetCompilation(source);
            var tree = compilation.SyntaxTrees.First();
            var model = compilation.GetSemanticModel(tree);

            var symbols = GetDeclaredSymbols(compilation).OfType<IFieldSymbol>().Select(f => f.Type).ToList();
            Assert.Equal(2, symbols.Count);
            TestRoundTrip(symbols, compilation);
        }

        [Fact]
        public void TestSelfReferentialGenericMethod()
        {
            var source = @"
public class C
{
    public void M<S, T>() { }
}
";
            var compilation = GetCompilation(source);
            var tree = compilation.SyntaxTrees.First();
            var model = compilation.GetSemanticModel(tree);

            var method = GetDeclaredSymbols(compilation).OfType<IMethodSymbol>().First();
            var constructed = method.Construct(compilation.GetSpecialType(SpecialType.System_Int32), method.TypeParameters[1]);

            TestRoundTrip(constructed, compilation);
        }

        [Fact]
        public void TestSelfReferentialGenericType()
        {
            var source = @"
public class C<S, T>
{
}
";
            var compilation = GetCompilation(source);
            var tree = compilation.SyntaxTrees.First();
            var model = compilation.GetSemanticModel(tree);

            var type = GetDeclaredSymbols(compilation).OfType<INamedTypeSymbol>().First();
            var constructed = type.Construct(compilation.GetSpecialType(SpecialType.System_Int32), type.TypeParameters[1]);

            TestRoundTrip(constructed, compilation);
        }

        private void TestRoundTrip(IEnumerable<ISymbol> symbols, Compilation compilation, Func<ISymbol, object> fnId = null)
        {
            foreach (var symbol in symbols)
            {
                TestRoundTrip(symbol, compilation, fnId);
            }
        }

        private void TestRoundTrip(ISymbol symbol, Compilation compilation, Func<ISymbol, object> fnId = null)
        {
            var id = SymbolId.CreateId(symbol);
            Assert.NotNull(id);
            var found = SymbolId.GetFirstSymbolForId(id, compilation);
            Assert.NotNull(found);

            if (fnId != null)
            {
                var expected = fnId(symbol);
                var actual = fnId(found);
                Assert.Equal(expected, actual);
            }
            else
            {
                Assert.Equal(symbol, found);
            }
        }

        private Compilation GetCompilation(string source)
        {
            var tree = SyntaxFactory.ParseSyntaxTree(source);
            return CSharpCompilation.Create("Test", syntaxTrees: new[] { tree }, references: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        }

        private List<ISymbol> GetDeclaredSymbols(Compilation compilation)
        {
            var list = new List<ISymbol>();
            GetDeclaredSymbols(compilation.Assembly.GlobalNamespace, list);
            return list;
        }

        private void GetDeclaredSymbols(INamespaceOrTypeSymbol container, List<ISymbol> symbols)
        {
            foreach (var member in container.GetMembers())
            {
                symbols.Add(member);

                var nsOrType = member as INamespaceOrTypeSymbol;
                if (nsOrType != null)
                {
                    GetDeclaredSymbols(nsOrType, symbols);
                }
            }
        }

        private static IEnumerable<ISymbol> GetInteriorSymbols(ISymbol containingSymbol, Compilation compilation)
        {
            var results = new List<ISymbol>();

            foreach (var declaringLocation in containingSymbol.DeclaringSyntaxReferences)
            {
                var node = declaringLocation.GetSyntax();
                if (node.Language == LanguageNames.VisualBasic)
                {
                    node = node.Parent;
                }

                var semanticModel = compilation.GetSemanticModel(node.SyntaxTree);

                GetInteriorSymbols(semanticModel, node, results);
            }

            return results;
        }

        private static void GetInteriorSymbols(SemanticModel model, SyntaxNode root, List<ISymbol> symbols)
        {
            foreach (var token in root.DescendantNodes())
            {
                var symbol = model.GetDeclaredSymbol(token);
                if (symbol != null)
                {
                    symbols.Add(symbol);
                }
            }
        }
    }
}
