﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;

#if NET46 || NET472 || NETSTANDARD
using NuGet.Packaging.Signing;
#endif

namespace Microsoft.CodeAnalysis.Testing
{
    public sealed partial class ReferenceAssemblies
    {
        private const string ReferenceAssembliesPackageVersion = "1.0.0-preview.2";

        private static readonly FileSystemSemaphore Semaphore = new FileSystemSemaphore(Path.Combine(Path.GetTempPath(), "test-packages", ".lock"));

        private readonly Dictionary<string, ImmutableArray<MetadataReference>> _references
            = new Dictionary<string, ImmutableArray<MetadataReference>>();

        public ReferenceAssemblies(string targetFramework)
        {
            TargetFramework = targetFramework ?? throw new ArgumentNullException(nameof(targetFramework));
            AssemblyIdentityComparer = AssemblyIdentityComparer.Default;
            ReferenceAssemblyPath = null;
            Assemblies = ImmutableArray<string>.Empty;
            LanguageSpecificAssemblies = ImmutableDictionary<string, ImmutableArray<string>>.Empty;
            Packages = ImmutableArray<PackageIdentity>.Empty;
        }

        public ReferenceAssemblies(string targetFramework, PackageIdentity referenceAssemblyPackage, string referenceAssemblyPath)
        {
            TargetFramework = targetFramework ?? throw new ArgumentNullException(nameof(targetFramework));
            AssemblyIdentityComparer = AssemblyIdentityComparer.Default;
            ReferenceAssemblyPackage = referenceAssemblyPackage ?? throw new ArgumentNullException(nameof(referenceAssemblyPackage));
            ReferenceAssemblyPath = referenceAssemblyPath;
            Assemblies = ImmutableArray<string>.Empty;
            LanguageSpecificAssemblies = ImmutableDictionary<string, ImmutableArray<string>>.Empty;
            Packages = ImmutableArray<PackageIdentity>.Empty;
        }

        private ReferenceAssemblies(
            string targetFramework,
            AssemblyIdentityComparer assemblyIdentityComparer,
            PackageIdentity? referenceAssemblyPackage,
            string? referenceAssemblyPath,
            ImmutableArray<string> assemblies,
            ImmutableDictionary<string, ImmutableArray<string>> languageSpecificAssemblies,
            ImmutableArray<PackageIdentity> packages)
        {
            TargetFramework = targetFramework;
            AssemblyIdentityComparer = assemblyIdentityComparer;
            ReferenceAssemblyPackage = referenceAssemblyPackage;
            ReferenceAssemblyPath = referenceAssemblyPath;
            Assemblies = assemblies.IsDefault ? ImmutableArray<string>.Empty : assemblies;
            LanguageSpecificAssemblies = languageSpecificAssemblies;
            Packages = packages.IsDefault ? ImmutableArray<PackageIdentity>.Empty : packages;
        }

        public static ReferenceAssemblies Default
        {
            get
            {
#if NETSTANDARD1_5
                return NetStandard.NetStandard15;
#elif NETSTANDARD2_0
                return NetStandard.NetStandard20;
#elif NET452
                return NetFramework.Net452.Default;
#elif NET46
                return NetFramework.Net46.Default;
#elif NET472
                return NetFramework.Net472.Default;
#endif
            }
        }

        public string TargetFramework { get; }

        public AssemblyIdentityComparer AssemblyIdentityComparer { get; }

        public PackageIdentity? ReferenceAssemblyPackage { get; }

        public string? ReferenceAssemblyPath { get; }

        public ImmutableArray<string> Assemblies { get; }

        public ImmutableDictionary<string, ImmutableArray<string>> LanguageSpecificAssemblies { get; }

        public ImmutableArray<PackageIdentity> Packages { get; }

        public ReferenceAssemblies WithAssemblyIdentityComparer(AssemblyIdentityComparer assemblyIdentityComparer)
            => new ReferenceAssemblies(TargetFramework, assemblyIdentityComparer, ReferenceAssemblyPackage, ReferenceAssemblyPath, Assemblies, LanguageSpecificAssemblies, Packages);

        public ReferenceAssemblies WithAssemblies(ImmutableArray<string> assemblies)
            => new ReferenceAssemblies(TargetFramework, AssemblyIdentityComparer, ReferenceAssemblyPackage, ReferenceAssemblyPath, assemblies, LanguageSpecificAssemblies, Packages);

        public ReferenceAssemblies AddAssemblies(ImmutableArray<string> assemblies)
            => WithAssemblies(Assemblies.AddRange(assemblies));

        public ReferenceAssemblies WithLanguageSpecificAssemblies(ImmutableDictionary<string, ImmutableArray<string>> languageSpecificAssemblies)
            => new ReferenceAssemblies(TargetFramework, AssemblyIdentityComparer, ReferenceAssemblyPackage, ReferenceAssemblyPath, Assemblies, languageSpecificAssemblies, Packages);

        public ReferenceAssemblies WithLanguageSpecificAssemblies(string language, ImmutableArray<string> assemblies)
            => WithLanguageSpecificAssemblies(LanguageSpecificAssemblies.SetItem(language, assemblies));

        public ReferenceAssemblies AddLanguageSpecificAssemblies(string language, ImmutableArray<string> assemblies)
        {
            if (!LanguageSpecificAssemblies.TryGetValue(language, out var existing))
            {
                existing = ImmutableArray<string>.Empty;
            }

            return WithLanguageSpecificAssemblies(language, existing.AddRange(assemblies));
        }

        public ReferenceAssemblies WithPackages(ImmutableArray<PackageIdentity> packages)
            => new ReferenceAssemblies(TargetFramework, AssemblyIdentityComparer, ReferenceAssemblyPackage, ReferenceAssemblyPath, Assemblies, LanguageSpecificAssemblies, packages);

        public ReferenceAssemblies AddPackages(ImmutableArray<PackageIdentity> packages)
            => WithPackages(Packages.AddRange(packages));

        public async Task<ImmutableArray<MetadataReference>> ResolveAsync(string? language, CancellationToken cancellationToken)
        {
            if (language is object)
            {
                if (LanguageSpecificAssemblies.IsEmpty
                    || !LanguageSpecificAssemblies.TryGetValue(language, out var languageSpecificAssemblies)
                    || languageSpecificAssemblies.IsEmpty)
                {
                    return await ResolveAsync(null, cancellationToken);
                }
            }

            language ??= string.Empty;
            lock (_references)
            {
                if (_references.TryGetValue(language, out var references))
                {
                    return references;
                }
            }

            using (var releaser = await Semaphore.WaitAsync(cancellationToken))
            {
                lock (_references)
                {
                    if (_references.TryGetValue(language, out var references))
                    {
                        return references;
                    }
                }

                var computedReferences = await ResolveCoreAsync(language, cancellationToken);
                lock (_references)
                {
                    _references.Add(language, computedReferences);
                }

                return computedReferences;
            }
        }

        /// <seealso href="https://martinbjorkstrom.com/posts/2018-09-19-revisiting-nuget-client-libraries"/>
        private async Task<ImmutableArray<MetadataReference>> ResolveCoreAsync(string language, CancellationToken cancellationToken)
        {
            var settings = Settings.LoadDefaultSettings(root: null);
            var sourceRepositoryProvider = new SourceRepositoryProvider(new PackageSourceProvider(settings), Repository.Provider.GetCoreV3());
            var targetFramework = NuGetFramework.ParseFolder(TargetFramework);
            var logger = NullLogger.Instance;

            using (var cacheContext = new SourceCacheContext())
            {
                var repositories = sourceRepositoryProvider.GetRepositories().ToImmutableArray();
                var dependencies = ImmutableDictionary.CreateBuilder<PackageIdentity, SourcePackageDependencyInfo>(PackageIdentityComparer.Default);

                if (ReferenceAssemblyPackage is object)
                {
                    await GetPackageDependenciesAsync(ReferenceAssemblyPackage, targetFramework, repositories, cacheContext, logger, dependencies, cancellationToken);
                }

                foreach (var packageIdentity in Packages)
                {
                    await GetPackageDependenciesAsync(packageIdentity, targetFramework, repositories, cacheContext, logger, dependencies, cancellationToken);
                }

                var availablePackages = dependencies.ToImmutable();

                var packagesToInstall = new List<PackageIdentity>();
                if (ReferenceAssemblyPackage is object)
                {
                    packagesToInstall.Add(ReferenceAssemblyPackage);
                }

                if (!Packages.IsEmpty)
                {
                    var resolverContext = new PackageResolverContext(
                        DependencyBehavior.Lowest,
                        Packages.Select(package => package.Id),
                        Enumerable.Empty<string>(),
                        Enumerable.Empty<PackageReference>(),
                        Enumerable.Empty<PackageIdentity>(),
                        availablePackages.Values,
                        sourceRepositoryProvider.GetRepositories().Select(repository => repository.PackageSource),
                        logger);
                    var resolver = new PackageResolver();

                    packagesToInstall.AddRange(resolver.Resolve(resolverContext, cancellationToken));
                }

                var globalPathResolver = new PackagePathResolver(SettingsUtility.GetGlobalPackagesFolder(settings));
                var temporaryPackagesFolder = Path.Combine(Path.GetTempPath(), "test-packages");
                Directory.CreateDirectory(temporaryPackagesFolder);
                var localPathResolver = new PackagePathResolver(temporaryPackagesFolder);
#if NET452
                var packageExtractionContext = new PackageExtractionContext(logger);
#elif NET46 || NET472 || NETSTANDARD2_0
                var packageExtractionContext = new PackageExtractionContext(
                    PackageSaveMode.Defaultv3,
                    XmlDocFileSaveMode.None,
                    ClientPolicyContext.GetClientPolicy(settings, logger),
                    logger);
#elif NETSTANDARD1_5
                var packageExtractionContext = new PackageExtractionContext(
                    PackageSaveMode.Defaultv3,
                    XmlDocFileSaveMode.None,
                    logger,
                    new PackageSignatureVerifier(
                        SignatureVerificationProviderFactory.GetSignatureVerificationProviders(),
                        SignedPackageVerifierSettings.Default));
#else
#error The current target framework is not supported.
#endif

                var frameworkReducer = new FrameworkReducer();

                var resolvedAssemblies = new HashSet<string>();
                foreach (var packageToInstall in packagesToInstall)
                {
                    PackageReaderBase packageReader;
                    var installedPath = GetInstalledPath(localPathResolver, packageToInstall)
                        ?? GetInstalledPath(globalPathResolver, packageToInstall);
                    if (installedPath is null)
                    {
                        var downloadResource = await availablePackages[packageToInstall].Source.GetResourceAsync<DownloadResource>(cancellationToken);
                        var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                            packageToInstall,
                            new PackageDownloadContext(cacheContext),
                            SettingsUtility.GetGlobalPackagesFolder(settings),
                            logger,
                            cancellationToken);

                        if (!PackageIdentityComparer.Default.Equals(packageToInstall, ReferenceAssemblyPackage)
                            && !downloadResult.PackageReader.GetItems(PackagingConstants.Folders.Lib).Any()
                            && !downloadResult.PackageReader.GetItems(PackagingConstants.Folders.Ref).Any())
                        {
                            // This package has no compile time impact
                            continue;
                        }

                        await PackageExtractor.ExtractPackageAsync(
#if !NET452 && !NETSTANDARD1_5
#pragma warning disable SA1114 // Parameter list should follow declaration
                            downloadResult.PackageSource,
#pragma warning restore SA1114 // Parameter list should follow declaration
#endif
                            downloadResult.PackageStream,
                            localPathResolver,
                            packageExtractionContext,
                            cancellationToken);

                        installedPath = localPathResolver.GetInstalledPath(packageToInstall);
                        packageReader = downloadResult.PackageReader;
                    }
                    else
                    {
                        packageReader = new PackageFolderReader(installedPath);
                    }

                    var libItems = await packageReader.GetLibItemsAsync(cancellationToken);
                    var nearestLib = frameworkReducer.GetNearest(targetFramework, libItems.Select(x => x.TargetFramework));
                    var frameworkItems = await packageReader.GetFrameworkItemsAsync(cancellationToken);
                    var nearestFramework = frameworkReducer.GetNearest(targetFramework, frameworkItems.Select(x => x.TargetFramework));
                    var refItems = await packageReader.GetItemsAsync(PackagingConstants.Folders.Ref, cancellationToken);
                    var nearestRef = frameworkReducer.GetNearest(targetFramework, refItems.Select(x => x.TargetFramework));
                    if (nearestRef is object)
                    {
                        var nearestRefItems = refItems.Single(x => x.TargetFramework == nearestRef);
                        foreach (var item in nearestRefItems.Items)
                        {
                            if (!string.Equals(Path.GetExtension(item), ".dll", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            resolvedAssemblies.Add(Path.Combine(installedPath, item));
                        }
                    }
                    else if (nearestLib is object)
                    {
                        var nearestLibItems = libItems.Single(x => x.TargetFramework == nearestLib);
                        foreach (var item in nearestLibItems.Items)
                        {
                            if (!string.Equals(Path.GetExtension(item), ".dll", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            resolvedAssemblies.Add(Path.Combine(installedPath, item));
                        }
                    }

                    if (nearestFramework is object)
                    {
                        var nearestFrameworkItems = frameworkItems.Single(x => x.TargetFramework == nearestFramework);
                        foreach (var item in nearestFrameworkItems.Items)
                        {
                            var installedFrameworkPath = localPathResolver.GetInstalledPath(ReferenceAssemblyPackage)
                                ?? globalPathResolver.GetInstalledPath(ReferenceAssemblyPackage);
                            if (File.Exists(Path.Combine(installedFrameworkPath, ReferenceAssemblyPath, item + ".dll")))
                            {
                                resolvedAssemblies.Add(Path.GetFullPath(Path.Combine(installedFrameworkPath, ReferenceAssemblyPath, item + ".dll")));
                            }
                        }
                    }
                }

                foreach (var assembly in Assemblies)
                {
                    var installedPath = localPathResolver.GetInstalledPath(ReferenceAssemblyPackage)
                        ?? globalPathResolver.GetInstalledPath(ReferenceAssemblyPackage);
                    if (File.Exists(Path.Combine(installedPath, ReferenceAssemblyPath, assembly + ".dll")))
                    {
                        resolvedAssemblies.Add(Path.GetFullPath(Path.Combine(installedPath, ReferenceAssemblyPath, assembly + ".dll")));
                    }
                }

                if (LanguageSpecificAssemblies.TryGetValue(language, out var languageSpecificAssemblies))
                {
                    foreach (var assembly in languageSpecificAssemblies)
                    {
                        var installedPath = localPathResolver.GetInstalledPath(ReferenceAssemblyPackage)
                            ?? globalPathResolver.GetInstalledPath(ReferenceAssemblyPackage);
                        if (File.Exists(Path.Combine(installedPath, ReferenceAssemblyPath, assembly + ".dll")))
                        {
                            resolvedAssemblies.Add(Path.GetFullPath(Path.Combine(installedPath, ReferenceAssemblyPath, assembly + ".dll")));
                        }
                    }
                }

                // Add the facade assemblies
                if (ReferenceAssemblyPackage is object)
                {
                    var installedPath = localPathResolver.GetInstalledPath(ReferenceAssemblyPackage)
                        ?? globalPathResolver.GetInstalledPath(ReferenceAssemblyPackage);
                    var facadesPath = Path.Combine(installedPath, ReferenceAssemblyPath, "Facades");
                    if (Directory.Exists(facadesPath))
                    {
                        foreach (var path in Directory.GetFiles(facadesPath, "*.dll"))
                        {
                            resolvedAssemblies.Add(Path.GetFullPath(path));
                        }
                    }
                }

                return resolvedAssemblies.Select(MetadataReferences.CreateReferenceFromFile).ToImmutableArray();

                static string? GetInstalledPath(PackagePathResolver resolver, PackageIdentity id)
                {
                    try
                    {
                        return resolver.GetInstalledPath(id);
                    }
                    catch (PathTooLongException)
                    {
                        return null;
                    }
                }
            }
        }

        private static async Task GetPackageDependenciesAsync(
            PackageIdentity packageIdentity,
            NuGetFramework targetFramework,
            ImmutableArray<SourceRepository> repositories,
            SourceCacheContext cacheContext,
            ILogger logger,
            ImmutableDictionary<PackageIdentity, SourcePackageDependencyInfo>.Builder dependencies,
            CancellationToken cancellationToken)
        {
            if (dependencies.ContainsKey(packageIdentity))
            {
                return;
            }

            foreach (var sourceRepository in repositories)
            {
                var dependencyInfoResource = await sourceRepository.GetResourceAsync<DependencyInfoResource>(cancellationToken);
                var dependencyInfo = await dependencyInfoResource.ResolvePackage(
                    packageIdentity,
                    targetFramework,
#if !NET452
                    cacheContext,
#endif
                    logger,
                    cancellationToken);
                if (dependencyInfo is null)
                {
                    continue;
                }

                dependencies.Add(packageIdentity, dependencyInfo);
                foreach (var dependency in dependencyInfo.Dependencies)
                {
                    await GetPackageDependenciesAsync(new PackageIdentity(dependency.Id, dependency.VersionRange.MinVersion), targetFramework, repositories, cacheContext, logger, dependencies, cancellationToken);
                }

                break;
            }
        }

        public static class NetFramework
        {
            public static class Net20
            {
                public static ReferenceAssemblies Default { get; }
                    = new ReferenceAssemblies(
                        "net20",
                        new PackageIdentity(
                            "Microsoft.NETFramework.ReferenceAssemblies.net20",
                            NuGetVersion.Parse(ReferenceAssembliesPackageVersion)),
                        @"build\.NETFramework\v2.0")
                    .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                    .AddAssemblies(ImmutableArray.Create("mscorlib", "System", "System.Data", "System.Xml"))
                    .AddLanguageSpecificAssemblies(LanguageNames.VisualBasic, ImmutableArray.Create("Microsoft.VisualBasic"));

                public static ReferenceAssemblies WindowsForms { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("System.Drawing", "System.Windows.Forms"));
            }

            public static class Net40
            {
                public static ReferenceAssemblies Default { get; }
                    = new ReferenceAssemblies(
                        "net40",
                        new PackageIdentity(
                            "Microsoft.NETFramework.ReferenceAssemblies.net40",
                            NuGetVersion.Parse(ReferenceAssembliesPackageVersion)),
                        @"build\.NETFramework\v4.0")
                    .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                    .AddAssemblies(ImmutableArray.Create("mscorlib", "System", "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Xml", "System.Xml.Linq"))
                    .AddLanguageSpecificAssemblies(LanguageNames.CSharp, ImmutableArray.Create("Microsoft.CSharp"))
                    .AddLanguageSpecificAssemblies(LanguageNames.VisualBasic, ImmutableArray.Create("Microsoft.VisualBasic"));

                public static ReferenceAssemblies WindowsForms { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("System.Deployment", "System.Drawing", "System.Windows.Forms"));

                public static ReferenceAssemblies Wpf { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("PresentationCore", "PresentationFramework", "System.Xaml", "WindowsBase"));
            }

            public static class Net45
            {
                public static ReferenceAssemblies Default { get; }
                    = new ReferenceAssemblies(
                        "net45",
                        new PackageIdentity(
                            "Microsoft.NETFramework.ReferenceAssemblies.net45",
                            NuGetVersion.Parse(ReferenceAssembliesPackageVersion)),
                        @"build\.NETFramework\v4.5")
                    .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                    .AddAssemblies(ImmutableArray.Create("mscorlib", "System", "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Net.Http", "System.Xml", "System.Xml.Linq"))
                    .AddLanguageSpecificAssemblies(LanguageNames.CSharp, ImmutableArray.Create("Microsoft.CSharp"))
                    .AddLanguageSpecificAssemblies(LanguageNames.VisualBasic, ImmutableArray.Create("Microsoft.VisualBasic"));

                public static ReferenceAssemblies WindowsForms { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("System.Deployment", "System.Drawing", "System.Windows.Forms"));

                public static ReferenceAssemblies Wpf { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("PresentationCore", "PresentationFramework", "System.Xaml", "WindowsBase"));
            }

            public static class Net451
            {
                public static ReferenceAssemblies Default { get; }
                    = new ReferenceAssemblies(
                        "net451",
                        new PackageIdentity(
                            "Microsoft.NETFramework.ReferenceAssemblies.net451",
                            NuGetVersion.Parse(ReferenceAssembliesPackageVersion)),
                        @"build\.NETFramework\v4.5.1")
                    .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                    .AddAssemblies(ImmutableArray.Create("mscorlib", "System", "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Net.Http", "System.Xml", "System.Xml.Linq"))
                    .AddLanguageSpecificAssemblies(LanguageNames.CSharp, ImmutableArray.Create("Microsoft.CSharp"))
                    .AddLanguageSpecificAssemblies(LanguageNames.VisualBasic, ImmutableArray.Create("Microsoft.VisualBasic"));

                public static ReferenceAssemblies WindowsForms { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("System.Deployment", "System.Drawing", "System.Windows.Forms"));

                public static ReferenceAssemblies Wpf { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("PresentationCore", "PresentationFramework", "System.Xaml", "WindowsBase"));
            }

            public static class Net452
            {
                public static ReferenceAssemblies Default { get; }
                    = new ReferenceAssemblies(
                        "net452",
                        new PackageIdentity(
                            "Microsoft.NETFramework.ReferenceAssemblies.net452",
                            NuGetVersion.Parse(ReferenceAssembliesPackageVersion)),
                        @"build\.NETFramework\v4.5.2")
                    .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                    .AddAssemblies(ImmutableArray.Create("mscorlib", "System", "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Net.Http", "System.Xml", "System.Xml.Linq"))
                    .AddLanguageSpecificAssemblies(LanguageNames.CSharp, ImmutableArray.Create("Microsoft.CSharp"))
                    .AddLanguageSpecificAssemblies(LanguageNames.VisualBasic, ImmutableArray.Create("Microsoft.VisualBasic"));

                public static ReferenceAssemblies WindowsForms { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("System.Deployment", "System.Drawing", "System.Windows.Forms"));

                public static ReferenceAssemblies Wpf { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("PresentationCore", "PresentationFramework", "System.Xaml", "WindowsBase"));
            }

            public static class Net46
            {
                public static ReferenceAssemblies Default { get; }
                    = new ReferenceAssemblies(
                        "net46",
                        new PackageIdentity(
                            "Microsoft.NETFramework.ReferenceAssemblies.net46",
                            NuGetVersion.Parse(ReferenceAssembliesPackageVersion)),
                        @"build\.NETFramework\v4.6")
                    .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                    .AddAssemblies(ImmutableArray.Create("mscorlib", "System", "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Net.Http", "System.Xml", "System.Xml.Linq"))
                    .AddLanguageSpecificAssemblies(LanguageNames.CSharp, ImmutableArray.Create("Microsoft.CSharp"))
                    .AddLanguageSpecificAssemblies(LanguageNames.VisualBasic, ImmutableArray.Create("Microsoft.VisualBasic"));

                public static ReferenceAssemblies WindowsForms { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("System.Deployment", "System.Drawing", "System.Windows.Forms"));

                public static ReferenceAssemblies Wpf { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("PresentationCore", "PresentationFramework", "System.Xaml", "WindowsBase"));
            }

            public static class Net461
            {
                public static ReferenceAssemblies Default { get; }
                    = new ReferenceAssemblies(
                        "net461",
                        new PackageIdentity(
                            "Microsoft.NETFramework.ReferenceAssemblies.net461",
                            NuGetVersion.Parse(ReferenceAssembliesPackageVersion)),
                        @"build\.NETFramework\v4.6.1")
                    .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                    .AddAssemblies(ImmutableArray.Create("mscorlib", "System", "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Net.Http", "System.Xml", "System.Xml.Linq"))
                    .AddLanguageSpecificAssemblies(LanguageNames.CSharp, ImmutableArray.Create("Microsoft.CSharp"))
                    .AddLanguageSpecificAssemblies(LanguageNames.VisualBasic, ImmutableArray.Create("Microsoft.VisualBasic"));

                public static ReferenceAssemblies WindowsForms { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("System.Deployment", "System.Drawing", "System.Windows.Forms"));

                public static ReferenceAssemblies Wpf { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("PresentationCore", "PresentationFramework", "System.Xaml", "WindowsBase"));
            }

            public static class Net462
            {
                public static ReferenceAssemblies Default { get; }
                    = new ReferenceAssemblies(
                        "net462",
                        new PackageIdentity(
                            "Microsoft.NETFramework.ReferenceAssemblies.net462",
                            NuGetVersion.Parse(ReferenceAssembliesPackageVersion)),
                        @"build\.NETFramework\v4.6.2")
                    .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                    .AddAssemblies(ImmutableArray.Create("mscorlib", "System", "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Net.Http", "System.Xml", "System.Xml.Linq"))
                    .AddLanguageSpecificAssemblies(LanguageNames.CSharp, ImmutableArray.Create("Microsoft.CSharp"))
                    .AddLanguageSpecificAssemblies(LanguageNames.VisualBasic, ImmutableArray.Create("Microsoft.VisualBasic"));

                public static ReferenceAssemblies WindowsForms { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("System.Deployment", "System.Drawing", "System.Windows.Forms"));

                public static ReferenceAssemblies Wpf { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("PresentationCore", "PresentationFramework", "System.Xaml", "WindowsBase"));
            }

            public static class Net47
            {
                public static ReferenceAssemblies Default { get; }
                    = new ReferenceAssemblies(
                        "net47",
                        new PackageIdentity(
                            "Microsoft.NETFramework.ReferenceAssemblies.net47",
                            NuGetVersion.Parse(ReferenceAssembliesPackageVersion)),
                        @"build\.NETFramework\v4.7")
                    .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                    .AddAssemblies(ImmutableArray.Create("mscorlib", "System", "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Net.Http", "System.Xml", "System.Xml.Linq"))
                    .AddLanguageSpecificAssemblies(LanguageNames.CSharp, ImmutableArray.Create("Microsoft.CSharp"))
                    .AddLanguageSpecificAssemblies(LanguageNames.VisualBasic, ImmutableArray.Create("Microsoft.VisualBasic"));

                public static ReferenceAssemblies WindowsForms { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("System.Deployment", "System.Drawing", "System.Windows.Forms"));

                public static ReferenceAssemblies Wpf { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("PresentationCore", "PresentationFramework", "System.Xaml", "WindowsBase"));
            }

            public static class Net471
            {
                public static ReferenceAssemblies Default { get; }
                    = new ReferenceAssemblies(
                        "net471",
                        new PackageIdentity(
                            "Microsoft.NETFramework.ReferenceAssemblies.net471",
                            NuGetVersion.Parse(ReferenceAssembliesPackageVersion)),
                        @"build\.NETFramework\v4.7.1")
                    .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                    .AddAssemblies(ImmutableArray.Create("mscorlib", "System", "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Net.Http", "System.Xml", "System.Xml.Linq"))
                    .AddLanguageSpecificAssemblies(LanguageNames.CSharp, ImmutableArray.Create("Microsoft.CSharp"))
                    .AddLanguageSpecificAssemblies(LanguageNames.VisualBasic, ImmutableArray.Create("Microsoft.VisualBasic"));

                public static ReferenceAssemblies WindowsForms { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("System.Deployment", "System.Drawing", "System.Windows.Forms"));

                public static ReferenceAssemblies Wpf { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("PresentationCore", "PresentationFramework", "System.Xaml", "WindowsBase"));
            }

            public static class Net472
            {
                public static ReferenceAssemblies Default { get; }
                    = new ReferenceAssemblies(
                        "net472",
                        new PackageIdentity(
                            "Microsoft.NETFramework.ReferenceAssemblies.net472",
                            NuGetVersion.Parse(ReferenceAssembliesPackageVersion)),
                        @"build\.NETFramework\v4.7.2")
                    .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                    .AddAssemblies(ImmutableArray.Create("mscorlib", "System", "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Net.Http", "System.Xml", "System.Xml.Linq"))
                    .AddLanguageSpecificAssemblies(LanguageNames.CSharp, ImmutableArray.Create("Microsoft.CSharp"))
                    .AddLanguageSpecificAssemblies(LanguageNames.VisualBasic, ImmutableArray.Create("Microsoft.VisualBasic"));

                public static ReferenceAssemblies WindowsForms { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("System.Deployment", "System.Drawing", "System.Windows.Forms"));

                public static ReferenceAssemblies Wpf { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("PresentationCore", "PresentationFramework", "System.Xaml", "WindowsBase"));
            }

            public static class Net48
            {
                public static ReferenceAssemblies Default { get; }
                    = new ReferenceAssemblies(
                        "net48",
                        new PackageIdentity(
                            "Microsoft.NETFramework.ReferenceAssemblies.net48",
                            NuGetVersion.Parse(ReferenceAssembliesPackageVersion)),
                        @"build\.NETFramework\v4.8")
                    .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
                    .AddAssemblies(ImmutableArray.Create("mscorlib", "System", "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Net.Http", "System.Xml", "System.Xml.Linq"))
                    .AddLanguageSpecificAssemblies(LanguageNames.CSharp, ImmutableArray.Create("Microsoft.CSharp"))
                    .AddLanguageSpecificAssemblies(LanguageNames.VisualBasic, ImmutableArray.Create("Microsoft.VisualBasic"));

                public static ReferenceAssemblies WindowsForms { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("System.Deployment", "System.Drawing", "System.Windows.Forms"));

                public static ReferenceAssemblies Wpf { get; }
                    = Default.AddAssemblies(ImmutableArray.Create("PresentationCore", "PresentationFramework", "System.Xaml", "WindowsBase"));
            }
        }

        public static class NetCore
        {
            public static ReferenceAssemblies NetCoreApp10 { get; }
                = new ReferenceAssemblies("netcoreapp1.0")
                .AddPackages(ImmutableArray.Create(new PackageIdentity("Microsoft.NETCore.App", NuGetVersion.Parse("1.0.16"))));

            public static ReferenceAssemblies NetCoreApp11 { get; }
                = new ReferenceAssemblies("netcoreapp1.1")
                .AddPackages(ImmutableArray.Create(new PackageIdentity("Microsoft.NETCore.App", NuGetVersion.Parse("1.1.13"))));

            public static ReferenceAssemblies NetCoreApp20 { get; }
                = new ReferenceAssemblies("netcoreapp2.0")
                .AddPackages(ImmutableArray.Create(new PackageIdentity("Microsoft.NETCore.App", NuGetVersion.Parse("2.0.9"))));

            public static ReferenceAssemblies NetCoreApp21 { get; }
                = new ReferenceAssemblies("netcoreapp2.1")
                .AddPackages(ImmutableArray.Create(new PackageIdentity("Microsoft.NETCore.App", NuGetVersion.Parse("2.1.13"))));
        }

        public static class NetStandard
        {
            public static ReferenceAssemblies NetStandard10 { get; }
                = new ReferenceAssemblies("netstandard1.0")
                .AddPackages(ImmutableArray.Create(new PackageIdentity("NETStandard.Library", NuGetVersion.Parse("1.6.1"))));

            public static ReferenceAssemblies NetStandard11 { get; }
                = new ReferenceAssemblies("netstandard1.1")
                .AddPackages(ImmutableArray.Create(new PackageIdentity("NETStandard.Library", NuGetVersion.Parse("1.6.1"))));

            public static ReferenceAssemblies NetStandard12 { get; }
                = new ReferenceAssemblies("netstandard1.2")
                .AddPackages(ImmutableArray.Create(new PackageIdentity("NETStandard.Library", NuGetVersion.Parse("1.6.1"))));

            public static ReferenceAssemblies NetStandard13 { get; }
                = new ReferenceAssemblies("netstandard1.3")
                .AddPackages(ImmutableArray.Create(new PackageIdentity("NETStandard.Library", NuGetVersion.Parse("1.6.1"))));

            public static ReferenceAssemblies NetStandard14 { get; }
                = new ReferenceAssemblies("netstandard1.4")
                .AddPackages(ImmutableArray.Create(new PackageIdentity("NETStandard.Library", NuGetVersion.Parse("1.6.1"))));

            public static ReferenceAssemblies NetStandard15 { get; }
                = new ReferenceAssemblies("netstandard1.5")
                .AddPackages(ImmutableArray.Create(new PackageIdentity("NETStandard.Library", NuGetVersion.Parse("1.6.1"))));

            public static ReferenceAssemblies NetStandard16 { get; }
                = new ReferenceAssemblies("netstandard1.6")
                .AddPackages(ImmutableArray.Create(new PackageIdentity("NETStandard.Library", NuGetVersion.Parse("1.6.1"))));

            public static ReferenceAssemblies NetStandard20 { get; }
                = new ReferenceAssemblies(
                    "netstandard2.0",
                    new PackageIdentity(
                        "NETStandard.Library",
                        NuGetVersion.Parse("2.0.3")),
                    @"build\netstandard2.0\ref")
                .AddAssemblies(ImmutableArray.Create("netstandard"));
        }
    }
}