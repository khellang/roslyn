﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Elfie.Model;
using Microsoft.CodeAnalysis.Elfie.Model.Structures;
using Microsoft.CodeAnalysis.Elfie.Model.Tree;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Packaging;
using Microsoft.CodeAnalysis.SymbolSearch;
using Microsoft.Internal.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using static System.FormattableString;
using VSShell = Microsoft.VisualStudio.Shell;

namespace Microsoft.VisualStudio.LanguageServices.SymbolSearch
{
    /// <summary>
    /// A service which enables searching for packages matching certain criteria.
    /// It works against an <see cref="Microsoft.CodeAnalysis.Elfie"/> database to find results.
    /// 
    /// This implementation also spawns a task which will attempt to keep that database up to
    /// date by downloading patches on a daily basis.
    /// </summary>
    internal partial class SymbolSearchService :
        ForegroundThreadAffinitizedObject,
        ISymbolSearchService,
        IDisposable
    {
        private ConcurrentDictionary<string, AddReferenceDatabase> _sourceToDatabase = new ConcurrentDictionary<string, AddReferenceDatabase>();

        public SymbolSearchService(
            VSShell.SVsServiceProvider serviceProvider,
            Workspace workspace,
            IPackageInstallerService installerService)
            : this(workspace, 
                   installerService, 
                   CreateRemoteControlService(serviceProvider),
                   new LogService((IVsActivityLog)serviceProvider.GetService(typeof(SVsActivityLog))),
                   new DelayService(),
                   new IOService(),
                   new PatchService(),
                   new DatabaseFactoryService(),
                   new ShellSettingsManager(serviceProvider).GetApplicationDataFolder(ApplicationDataFolder.LocalSettings),
                   // Report all exceptions we encounter, but don't crash on them.
                   FatalError.ReportWithoutCrash,
                   new CancellationTokenSource())
        {
            installerService.PackageSourcesChanged += OnOptionChanged;
            var optionsService = workspace.Services.GetService<IOptionService>();
            optionsService.OptionChanged += OnOptionChanged;

            OnOptionChanged(this, EventArgs.Empty);
        }

        private static IRemoteControlService CreateRemoteControlService(VSShell.SVsServiceProvider serviceProvider)
        {
            var vsService = serviceProvider.GetService(typeof(SVsRemoteControlService));
            if (vsService == null)
            {
                // If we can't access the file update service, then there's nothing we can do.
                return null;
            }

            return new RemoteControlService(vsService);
        }

        /// <summary>
        /// For testing purposes only.
        /// </summary>
        internal SymbolSearchService(
            Workspace workspace,
            IPackageInstallerService installerService,
            IRemoteControlService remoteControlService,
            ILogService logService,
            IDelayService delayService,
            IIOService ioService,
            IPatchService patchService,
            IDatabaseFactoryService databaseFactoryService,
            string localSettingsDirectory,
            Func<Exception, bool> reportAndSwallowException,
            CancellationTokenSource cancellationTokenSource)
        {
            if (remoteControlService == null)
            {
                // If we can't access the file update service, then there's nothing we can do.
                return;
            }

            _workspace = workspace;
            _installerService = installerService;
            _delayService = delayService;
            _ioService = ioService;
            _logService = logService;
            _remoteControlService = remoteControlService;
            _patchService = patchService;
            _databaseFactoryService = databaseFactoryService;
            _reportAndSwallowException = reportAndSwallowException;

            _cacheDirectoryInfo = new DirectoryInfo(Path.Combine(
                localSettingsDirectory, "PackageCache", string.Format(Invariant($"Format{_dataFormatVersion}"))));
            // _databaseFileInfo = new FileInfo(Path.Combine(_cacheDirectoryInfo.FullName, "NuGetCache.txt"));

            _cancellationTokenSource = cancellationTokenSource;
            _cancellationToken = _cancellationTokenSource.Token;
        }

        public IEnumerable<PackageWithTypeResult> FindPackagesWithType(
            string source, string name, int arity, CancellationToken cancellationToken)
        {
            AddReferenceDatabase database;
            if (!_sourceToDatabase.TryGetValue(source, out database))
            {
                // Don't have a database to search.  
                yield break;
            }

            if (name == "var")
            {
                // never find anything named 'var'.
                yield break;
            }

            var query = new MemberQuery(name, isFullSuffix: true, isFullNamespace: false);
            var symbols = new PartialArray<Symbol>(100);

            if (query.TryFindMembers(database, ref symbols))
            {
                var types = FilterToViableTypes(symbols);

                var typesFromPackagesUsedInOtherProjects = new List<Symbol>();
                var typesFromPackagesNotUsedInOtherProjects = new List<Symbol>();

                foreach (var type in types)
                {
                    // Ignore any reference assembly results.
                    if (type.PackageName.ToString() != MicrosoftAssemblyReferencesName)
                    {
                        var packageName = type.PackageName.ToString();
                        if (_installerService.GetInstalledVersions(packageName).Any())
                        {
                            typesFromPackagesUsedInOtherProjects.Add(type);
                        }
                        else
                        {
                            typesFromPackagesNotUsedInOtherProjects.Add(type);
                        }
                    }
                }

                var result = new List<Symbol>();

                // We always returm types from packages that we've use elsewhere in the project.
                int? bestRank = null;
                foreach (var type in typesFromPackagesUsedInOtherProjects)
                {
                    yield return CreateResult(database, type);
                }

                // For all other hits include as long as the popularity is high enough.  
                // Popularity ranks are in powers of two.  So if two packages differ by 
                // one rank, then one is at least twice as popular as the next.  Two 
                // ranks would be four times as popular.  Three ranks = 8 times,  etc. 
                // etc.  We keep packages that within 1 rank of the best package we find.
                foreach (var type in typesFromPackagesNotUsedInOtherProjects)
                {
                    var rank = GetRank(type);
                    bestRank = bestRank == null ? rank : Math.Max(bestRank.Value, rank);

                    if (Math.Abs(bestRank.Value - rank) > 1)
                    {
                        yield break;
                    }

                    yield return CreateResult(database, type);
                }
            }
        }

        public IEnumerable<ReferenceAssemblyWithTypeResult> FindReferenceAssembliesWithType(
            string name, int arity, CancellationToken cancellationToken)
        {
            // Our reference assembly data is stored in the nuget.org DB.
            AddReferenceDatabase database;
            if (!_sourceToDatabase.TryGetValue(NugetOrgSource, out database))
            {
                // Don't have a database to search.  
                yield break;
            }

            if (name == "var")
            {
                // never find anything named 'var'.
                yield break;
            }

            var query = new MemberQuery(name, isFullSuffix: true, isFullNamespace: false);
            var symbols = new PartialArray<Symbol>(100);

            if (query.TryFindMembers(database, ref symbols))
            {
                var types = FilterToViableTypes(symbols);

                foreach (var type in types)
                {
                    // Only look at reference assembly results.
                    if (type.PackageName.ToString() == MicrosoftAssemblyReferencesName)
                    {
                        var nameParts = new List<string>();
                        GetFullName(nameParts, type.FullName.Parent);
                        yield return new ReferenceAssemblyWithTypeResult(
                            type.AssemblyName.ToString(), type.Name.ToString(), containingNamespaceNames: nameParts);
                    }
                }
            }
        }

        private List<Symbol> FilterToViableTypes(PartialArray<Symbol> symbols)
        {
            // Don't return nested types.  Currently their value does not seem worth
            // it given all the extra stuff we'd have to plumb through.  Namely 
            // going down the "using static" code path and whatnot.
            return new List<Symbol>(
                from symbol in symbols
                where this.IsType(symbol) && !this.IsType(symbol.Parent())
                select symbol);
        }

        private PackageWithTypeResult CreateResult(AddReferenceDatabase database, Symbol type)
        {
            var nameParts = new List<string>();
            GetFullName(nameParts, type.FullName.Parent);

            var packageName = type.PackageName.ToString();

            var version = database.GetPackageVersion(type.Index).ToString();

            return new PackageWithTypeResult(
                packageName: packageName, 
                typeName: type.Name.ToString(), 
                version: version,
                containingNamespaceNames: nameParts);
        }

        private int GetRank(Symbol symbol)
        {
            Symbol rankingSymbol;
            int rank;
            if (!TryGetRankingSymbol(symbol, out rankingSymbol) ||
                !int.TryParse(rankingSymbol.Name.ToString(), out rank))
            {
                return 0;
            }

            return rank;
        }

        private bool TryGetRankingSymbol(Symbol symbol, out Symbol rankingSymbol)
        {
            for (var current = symbol; current.IsValid; current = current.Parent())
            {
                if (current.Type == SymbolType.Package || current.Type == SymbolType.Version)
                {
                    return TryGetRankingSymbolForPackage(current, out rankingSymbol);
                }
            }

            rankingSymbol = default(Symbol);
            return false;
        }

        private bool TryGetRankingSymbolForPackage(Symbol package, out Symbol rankingSymbol)
        {
            for (var child = package.FirstChild(); child.IsValid; child = child.NextSibling())
            {
                if (child.Type == SymbolType.PopularityRank)
                {
                    rankingSymbol = child;
                    return true;
                }
            }

            rankingSymbol = default(Symbol);
            return false;
        }

        private bool IsType(Symbol symbol)
        {
            return symbol.Type.IsType();
        }

        private void GetFullName(List<string> nameParts, Path8 path)
        {
            if (!path.IsEmpty)
            {
                GetFullName(nameParts, path.Parent);
                nameParts.Add(path.Name.ToString());
            }
        }
    }
}