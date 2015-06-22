﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using NuGet.Configuration;
using NuGet.ContentModel;
using NuGet.DependencyResolver;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Logging;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.Core.v3;
using NuGet.Repositories;
using NuGet.RuntimeModel;
using NuGet.Versioning;

namespace NuGet.Commands
{
    public class RestoreCommand
    {
        private readonly ILogger _log;
        private readonly RestoreRequest _request;

        private bool _success = true;

        private readonly Dictionary<NuGetFramework, RuntimeGraph> _runtimeGraphCache = new Dictionary<NuGetFramework, RuntimeGraph>();

        public RestoreCommand(ILogger logger, RestoreRequest request)
        {
            _log = logger;
            _request = request;
        }

        public async Task<RestoreResult> ExecuteAsync()
        {
            var localRepository = _request.PackagesDirectory.GetLocalRepository();
            var projectLockFilePath = string.IsNullOrEmpty(_request.LockFilePath) ?
                Path.Combine(_request.Project.BaseDirectory, LockFileFormat.LockFileName) :
                _request.LockFilePath;

            bool relockFile = false;
            if (_request.ExistingLockFile != null && _request.ExistingLockFile.IsLocked && !_request.ExistingLockFile.IsValidForPackageSpec(_request.Project))
            {
                // The lock file was locked, but the project.json is out of date
                relockFile = true;
                _request.ExistingLockFile.IsLocked = false;
                _log.LogInformation(Strings.Log_LockFileOutOfDate);
            }

            var graphs = await ExecuteRestore(localRepository);

            // Build the lock file
            LockFile lockFile;
            if (_request.ExistingLockFile != null && _request.ExistingLockFile.IsLocked)
            {
                // No lock file to write!
                _request.WriteLockFile = false; // Force WriteLockFile to false
                lockFile = _request.ExistingLockFile;
            }
            else
            {
                lockFile = CreateLockFile(_request.Project, graphs, localRepository);

                // If the lock file was locked originally but we are re-locking it, well... re-lock it :)
                lockFile.IsLocked = relockFile;
            }

            // Scan every graph for compatibility
            var checkResults = new List<CompatibilityCheckResult>();
            var checker = new CompatibilityChecker(localRepository, lockFile, _log);
            foreach (var graph in graphs)
            {
                _log.LogVerbose(Strings.FormatLog_CheckingCompatibility(graph.Name));
                var res = checker.Check(graph);
                _success &= res.Success;
                checkResults.Add(res);
                if (res.Success)
                {
                    _log.LogInformation(Strings.FormatLog_PackagesAreCompatible(graph.Name));
                }
                else
                {
                    _log.LogError(Strings.FormatLog_PackagesIncompatible(graph.Name));
                }
            }

            // Write the lock file
            var lockFileFormat = new LockFileFormat();
            if (_request.WriteLockFile)
            {
                lockFileFormat.Write(projectLockFilePath, lockFile);
            }

            // Generate Targets/Props files
            if (_request.WriteMSBuildFiles)
            {
                WriteTargetsAndProps(_request.Project, graphs, localRepository);
            }

            return new RestoreResult(_success, graphs, checkResults, lockFile);
        }

        private async Task<IEnumerable<RestoreTargetGraph>> ExecuteRestore(NuGetv3LocalRepository localRepository)
        {
            if (_request.Project.TargetFrameworks.Count == 0)
            {
                _log.LogError(Strings.Log_ProjectDoesNotSpecifyTargetFrameworks);
                _success = false;
                return Enumerable.Empty<RestoreTargetGraph>();
            }

            _log.LogInformation(Strings.FormatLog_RestoringPackages(_request.Project.FilePath));

            // Load repositories
            var projectResolver = _request.ProjectResolver ?? new PackageSpecResolver(_request.Project);

            var context = new RemoteWalkContext();

            ExternalProjectReference externalProjectReference = null;
            if (_request.ExternalProjects.Any())
            {
                externalProjectReference = new ExternalProjectReference(
                    _request.Project.Name,
                    _request.Project.FilePath,
                    _request.ExternalProjects.Select(p => p.UniqueName));
            }

            context.ProjectLibraryProviders.Add(
                new LocalDependencyProvider(
                    new PackageSpecReferenceDependencyProvider(projectResolver, externalProjectReference)));

            if (_request.ExternalProjects != null)
            {
                context.ProjectLibraryProviders.Add(
                    new LocalDependencyProvider(
                        new ExternalProjectReferenceDependencyProvider(_request.ExternalProjects)));
            }

            context.LocalLibraryProviders.Add(
                new SourceRepositoryDependencyProvider(_request.PackagesDirectory.GetSourceRepository(), _log));

            foreach (var provider in _request.Sources)
            {
                context.RemoteLibraryProviders.Add(provider);
            }

            var remoteWalker = new RemoteDependencyWalker(context);

            var projectRange = new LibraryRange()
            {
                Name = _request.Project.Name,
                VersionRange = new VersionRange(_request.Project.Version),
                TypeConstraint = LibraryTypes.Project
            };

            // Resolve dependency graphs
            var frameworks = new HashSet<NuGetFramework>(_request.Project.TargetFrameworks.Select(f => f.FrameworkName));
            var graphs = new List<RestoreTargetGraph>();
            var frameworkTasks = new List<Task<RestoreTargetGraph>>();

            foreach (var framework in frameworks)
            {
                _log.LogInformation(Strings.FormatLog_RestoringPackages(framework.DotNetFrameworkName));
                frameworkTasks.Add(WalkDependencies(projectRange, framework, remoteWalker, context, writeToLockFile: true));
            }

            graphs.AddRange(await Task.WhenAll(frameworkTasks));

            if (!ResolutionSucceeded(graphs))
            {
                _success = false;
                return graphs;
            }

            // Install the runtime-agnostic packages
            var allInstalledPackages = new HashSet<LibraryIdentity>();
            await InstallPackages(graphs, _request.PackagesDirectory, allInstalledPackages, _request.MaxDegreeOfConcurrency);

            // Load runtime specs
            var runtimes = RuntimeGraph.Empty;
            foreach (var graph in graphs)
            {
                runtimes = RuntimeGraph.Merge(
                    runtimes,
                    GetRuntimeGraph(graph, localRepository));
            }

            // Resolve runtime dependencies
            var runtimeGraphs = new List<RestoreTargetGraph>();
            var runtimeProfiles = new HashSet<FrameworkRuntimePair>();
            if (_request.Project.RuntimeGraph.Runtimes.Count > 0)
            {
                var runtimeTasks = new List<Task<RestoreTargetGraph[]>>();
                foreach (var graph in graphs.Where(g => g.WriteToLockFile))
                {
                    runtimeTasks.Add(WalkRuntimeDependencies(projectRange, graph, _request.Project.RuntimeGraph, remoteWalker, context, localRepository, runtimes, writeToLockFile: true));
                }

                foreach (var runtimeSpecificGraph in (await Task.WhenAll(runtimeTasks)).SelectMany(g => g))
                {
                    runtimeGraphs.Add(runtimeSpecificGraph);
                }

                graphs.AddRange(runtimeGraphs);

                if (!ResolutionSucceeded(graphs))
                {
                    _success = false;
                    return graphs;
                }

                // Install runtime-specific packages
                await InstallPackages(runtimeGraphs, _request.PackagesDirectory, allInstalledPackages, _request.MaxDegreeOfConcurrency);
            }

            // Calculate compatibility profiles to check by merging those defined in the project with any from the command line
            foreach (var profile in _request.Project.RuntimeGraph.Supports)
            {
                CompatibilityProfile compatProfile;
                if (profile.Value.RestoreContexts.Any())
                {
                    // Just use the contexts from the project definition
                    compatProfile = profile.Value;
                }
                else if (!runtimes.Supports.TryGetValue(profile.Value.Name, out compatProfile))
                {
                    // No definition of this profile found, so just continue to the next one
                    _log.LogWarning(Strings.FormatLog_UnknownCompatibilityProfile(profile.Key));
                    continue;
                }

                foreach (var pair in compatProfile.RestoreContexts)
                {
                    _log.LogDebug($" {profile.Value.Name} -> +{pair}");
                    _request.CompatibilityProfiles.Add(pair);
                }
            }

            // Walk additional runtime graphs for supports checks
            if (_request.CompatibilityProfiles.Any())
            {
                var checkTasks = new List<Task<RestoreTargetGraph>>();
                foreach (var profile in _request.CompatibilityProfiles.Where(p => !runtimeProfiles.Contains(p)))
                {
                    _log.LogInformation(Strings.FormatLog_RestoringPackagesForCompat(profile.Name));
                    var graph = graphs.SingleOrDefault(g => g.Framework.Equals(profile.Framework) && string.IsNullOrEmpty(g.RuntimeIdentifier));
                    checkTasks.Add(WalkDependencies(projectRange, profile.Framework, profile.RuntimeIdentifier, runtimes, remoteWalker, context, writeToLockFile: false));
                }

                var checkGraphs = (await Task.WhenAll(checkTasks)).ToList();
                graphs.AddRange(checkGraphs);

                if (!ResolutionSucceeded(graphs))
                {
                    _success = false;
                    return graphs;
                }

                // Install packages for supports check
                await InstallPackages(checkGraphs, _request.PackagesDirectory, allInstalledPackages, _request.MaxDegreeOfConcurrency);
            }

            return graphs;
        }

        private bool ResolutionSucceeded(List<RestoreTargetGraph> graphs)
        {
            var success = true;
            foreach (var graph in graphs)
            {
                if (graph.Conflicts.Any())
                {
                    success = false;
                    _log.LogError(Strings.FormatLog_FailedToResolveConflicts(graph.Name));
                    foreach (var conflict in graph.Conflicts)
                    {
                        _log.LogError(Strings.FormatLog_ResolverConflict(
                            conflict.Name,
                            string.Join(", ", conflict.Requests)));
                    }
                }
                if (graph.Unresolved.Any())
                {
                    success = false;
                    foreach (var unresolved in graph.Unresolved)
                    {
                        _log.LogError(Strings.FormatLog_UnresolvedDependency(unresolved.Name, unresolved.VersionRange.PrettyPrint(), graph.Name));
                    }
                }
            }
            return success;
        }

        private void WriteTargetsAndProps(PackageSpec project, IEnumerable<RestoreTargetGraph> targetGraphs, NuGetv3LocalRepository repository)
        {
            // Get the project graph
            var projectFrameworks = project.TargetFrameworks.Select(f => f.FrameworkName).ToList();
            if (projectFrameworks.Count > 1)
            {
                var name = $"{project.Name}.nuget.targets";
                var path = Path.Combine(project.BaseDirectory, name);
                _log.LogInformation(Strings.FormatLog_GeneratingMsBuildFile(name));

                GenerateMSBuildErrorFile(path);
                return;
            }
            var graph = targetGraphs.Single(g => g.Framework.Equals(projectFrameworks[0]) && string.IsNullOrEmpty(g.RuntimeIdentifier));

            var pathResolver = new VersionFolderPathResolver(repository.RepositoryRoot);

            var targets = new List<string>();
            var props = new List<string>();
            foreach (var library in graph.Flattened.Distinct().OrderBy(g => g.Data.Match.Library))
            {
                var package = repository.FindPackagesById(library.Key.Name).FirstOrDefault(p => p.Version == library.Key.Version);
                if (package != null)
                {
                    var criteria = graph.Conventions.Criteria.ForFramework(graph.Framework);
                    var contentItemCollection = new ContentItemCollection();
                    using (var nupkgStream = File.OpenRead(package.ZipPath))
                    {
                        var reader = new PackageReader(nupkgStream);
                        contentItemCollection.Load(reader.GetFiles());
                    }

                    // Find MSBuild thingies
                    var buildItems = contentItemCollection.FindBestItemGroup(criteria, graph.Conventions.Patterns.MSBuildFiles);
                    if (buildItems != null)
                    {
                        // We need to additionally filter to items that are named "{packageId}.targets" and "{packageId}.props"
                        // Filter by file name here and we'll filter by extension when we add things to the lists.
                        var items = buildItems.Items
                            .Where(item => Path.GetFileNameWithoutExtension(item.Path).Equals(package.Id, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        targets.AddRange(items
                            .Select(c => c.Path)
                            .Where(path => Path.GetExtension(path).Equals(".targets", StringComparison.OrdinalIgnoreCase))
                            .Select(path => Path.Combine(pathResolver.GetPackageDirectory(package.Id, package.Version), path.Replace('/', Path.DirectorySeparatorChar))));
                        props.AddRange(items
                            .Select(c => c.Path)
                            .Where(path => Path.GetExtension(path).Equals(".props", StringComparison.OrdinalIgnoreCase))
                            .Select(path => Path.Combine(pathResolver.GetPackageDirectory(package.Id, package.Version), path.Replace('/', Path.DirectorySeparatorChar))));
                    }
                }
            }

            // Generate the files as needed
            var targetsName = $"{project.Name}.nuget.targets";
            var propsName = $"{project.Name}.nuget.props";
            var targetsPath = Path.Combine(project.BaseDirectory, targetsName);
            var propsPath = Path.Combine(project.BaseDirectory, propsName);

            if (targets.Any())
            {
                _log.LogInformation(Strings.FormatLog_GeneratingMsBuildFile(targetsName));

                GenerateImportsFile(repository, targetsPath, targets);
            }
            else if (File.Exists(targetsPath))
            {
                File.Delete(targetsPath);
            }

            if (props.Any())
            {
                _log.LogInformation(Strings.FormatLog_GeneratingMsBuildFile(propsName));

                GenerateImportsFile(repository, propsPath, props);
            }
            else if (File.Exists(propsPath))
            {
                File.Delete(propsPath);
            }
        }

        private void GenerateMSBuildErrorFile(string path)
        {
            var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "no"),

                new XElement(ns + "Project",
                    new XAttribute("ToolsVersion", "14.0"),

                    new XElement(ns + "Target",
                        new XAttribute("Name", "EmitMSBuildWarning"),
                        new XAttribute("BeforeTargets", "Build"),

                        new XElement(ns + "Warning",
                            new XAttribute("Text", Strings.MSBuildWarning_MultiTarget)))));

            using (var output = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                doc.Save(output);
            }
        }

        private void GenerateImportsFile(NuGetv3LocalRepository repository, string path, List<string> imports)
        {
            var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "no"),

                new XElement(ns + "Project",
                    new XAttribute("ToolsVersion", "14.0"),

                    new XElement(ns + "PropertyGroup",
                        new XAttribute("Condition", "'$(NuGetPackageRoot)' == ''"),

                        new XElement(ns + "NuGetPackageRoot", repository.RepositoryRoot)),
                    new XElement(ns + "ImportGroup", imports.Select(i =>
                        new XElement(ns + "Import",
                            new XAttribute("Project", Path.Combine("$(NuGetPackageRoot)", i)),
                            new XAttribute("Condition", $"Exists('{Path.Combine("$(NuGetPackageRoot)", i)}')"))))));

            using (var output = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                doc.Save(output);
            }
        }

        private LockFile CreateLockFile(PackageSpec project, IEnumerable<RestoreTargetGraph> targetGraphs, NuGetv3LocalRepository repository)
        {
            var lockFile = new LockFile();

            using (var sha512 = SHA512.Create())
            {
                foreach (var item in targetGraphs.SelectMany(g => g.Flattened).Distinct().OrderBy(x => x.Data.Match.Library))
                {
                    var library = item.Data.Match.Library;
                    var packageInfo = repository.FindPackagesById(library.Name)
                        .FirstOrDefault(p => p.Version == library.Version);

                    if (packageInfo == null)
                    {
                        continue;
                    }

                    var lockFileLib = CreateLockFileLibrary(
                        packageInfo,
                        sha512,
                        correctedPackageName: library.Name);

                    lockFile.Libraries.Add(lockFileLib);
                }
            }

            // Use empty string as the key of dependencies shared by all frameworks
            lockFile.ProjectFileDependencyGroups.Add(new ProjectFileDependencyGroup(
                string.Empty,
                project.Dependencies.Select(x => x.LibraryRange.ToString())));

            foreach (var frameworkInfo in project.TargetFrameworks)
            {
                lockFile.ProjectFileDependencyGroups.Add(new ProjectFileDependencyGroup(
                    frameworkInfo.FrameworkName.ToString(),
                    frameworkInfo.Dependencies.Select(x => x.LibraryRange.ToString())));
            }

            // Add the targets
            foreach (var targetGraph in targetGraphs)
            {
                var target = new LockFileTarget();
                target.TargetFramework = targetGraph.Framework;
                target.RuntimeIdentifier = targetGraph.RuntimeIdentifier;

                foreach (var library in targetGraph.Flattened.Select(g => g.Key).OrderBy(x => x))
                {
                    var packageInfo = repository.FindPackagesById(library.Name)
                        .FirstOrDefault(p => p.Version == library.Version);

                    if (packageInfo == null)
                    {
                        continue;
                    }

                    var targetLibrary = LockFileUtils.CreateLockFileTargetLibrary(
                        packageInfo,
                        targetGraph,
                        new VersionFolderPathResolver(repository.RepositoryRoot),
                        correctedPackageName: library.Name);

                    target.Libraries.Add(targetLibrary);
                }

                lockFile.Targets.Add(target);
            }

            return lockFile;
        }

        private LockFileLibrary CreateLockFileLibrary(LocalPackageInfo package, SHA512 sha512, string correctedPackageName)
        {
            var lockFileLib = new LockFileLibrary();

            // package.Id is read from nuspec and it might be in wrong casing.
            // correctedPackageName should be the package name used by dependency graph and
            // it has the correct casing that runtime needs during dependency resolution.
            lockFileLib.Name = correctedPackageName ?? package.Id;
            lockFileLib.Version = package.Version;
            lockFileLib.Type = LibraryTypes.Package; // Right now, lock file libraries are always packages

            using (var nupkgStream = File.OpenRead(package.ZipPath))
            {
                lockFileLib.Sha512 = Convert.ToBase64String(sha512.ComputeHash(nupkgStream));
                nupkgStream.Seek(0, SeekOrigin.Begin);

                var packageReader = new PackageReader(nupkgStream);

                // Get package files, excluding directory entries
                lockFileLib.Files = packageReader.GetFiles().Where(x => !x.EndsWith("/")).ToList();
            }

            return lockFileLib;
        }

        private Task<RestoreTargetGraph> WalkDependencies(LibraryRange projectRange, NuGetFramework framework, RemoteDependencyWalker walker, RemoteWalkContext context, bool writeToLockFile)
        {
            return WalkDependencies(projectRange, framework, null, RuntimeGraph.Empty, walker, context, writeToLockFile);
        }

        private async Task<RestoreTargetGraph> WalkDependencies(LibraryRange projectRange, NuGetFramework framework, string runtimeIdentifier, RuntimeGraph runtimeGraph, RemoteDependencyWalker walker, RemoteWalkContext context, bool writeToLockFile)
        {
            var name = FrameworkRuntimePair.GetName(framework, runtimeIdentifier);
            var graphs = new List<GraphNode<RemoteResolveResult>>();
            if (_request.ExistingLockFile != null && _request.ExistingLockFile.IsLocked)
            {
                // Walk all the items in the lock file target and just synthesize the outer graph
                var target = _request.ExistingLockFile.GetTarget(framework, runtimeIdentifier);

                if (target != null)
                {
                    foreach (var targetLibrary in target.Libraries)
                    {
                        var library = _request.ExistingLockFile.GetLibrary(targetLibrary.Name, targetLibrary.Version);
                        if (library == null)
                        {
                            _log.LogWarning(Strings.FormatLog_LockFileMissingLibraryForTargetLibrary(
                                targetLibrary.Name,
                                targetLibrary.Version,
                                target.Name));
                            continue; // This library is not in the main lockfile?
                        }

                        var range = new LibraryRange()
                        {
                            Name = library.Name,
                            TypeConstraint = library.Type, // Lockfile contains only Package libraries.
                            VersionRange = new VersionRange(
                                minVersion: library.Version,
                                includeMinVersion: true,
                                maxVersion: library.Version,
                                includeMaxVersion: true)
                        };
                        graphs.Add(await walker.WalkAsync(
                            range,
                            framework,
                            runtimeIdentifier,
                            runtimeGraph,
                            recursive: false));
                    }
                }
            }
            else
            {
                graphs.Add(await walker.WalkAsync(
                    projectRange,
                    framework,
                    runtimeIdentifier,
                    runtimeGraph,
                    recursive: true));
            }

            // Resolve conflicts
            _log.LogVerbose(Strings.FormatLog_ResolvingConflicts(name));

            // NOTE(anurse): We are OK with throwing away the result here. The Create call below will be checking for conflicts
            foreach (var graph in graphs)
            {
                graph.TryResolveConflicts();
            }

            // Flatten and create the RestoreTargetGraph to hold the packages
            return RestoreTargetGraph.Create(writeToLockFile, runtimeGraph, graphs, context, _log, framework, runtimeIdentifier);
        }

        private Task<RestoreTargetGraph[]> WalkRuntimeDependencies(LibraryRange projectRange, RestoreTargetGraph graph, RuntimeGraph projectRuntimeGraph, RemoteDependencyWalker walker, RemoteWalkContext context, NuGetv3LocalRepository localRepository, RuntimeGraph runtimes, bool writeToLockFile)
        {
            var resultGraphs = new List<Task<RestoreTargetGraph>>();
            foreach (var runtimeName in projectRuntimeGraph.Runtimes.Keys)
            {
                _log.LogInformation(Strings.FormatLog_RestoringPackages(FrameworkRuntimePair.GetName(graph.Framework, runtimeName)));
                resultGraphs.Add(WalkDependencies(projectRange, graph.Framework, runtimeName, runtimes, walker, context, writeToLockFile));
            }

            return Task.WhenAll(resultGraphs);
        }

        private RuntimeGraph GetRuntimeGraph(RestoreTargetGraph graph, NuGetv3LocalRepository localRepository)
        {
            // TODO: Caching!
            RuntimeGraph runtimeGraph;
            if (_runtimeGraphCache.TryGetValue(graph.Framework, out runtimeGraph))
            {
                return runtimeGraph;
            }

            _log.LogVerbose(Strings.Log_ScanningForRuntimeJson);
            runtimeGraph = RuntimeGraph.Empty;
            graph.Graphs.ForEach(node =>
            {
                var match = node?.Item?.Data?.Match;
                if (match == null)
                {
                    return;
                }

                // Locate the package in the local repository
                var package = localRepository.FindPackagesById(match.Library.Name).FirstOrDefault(p => p.Version == match.Library.Version);
                if (package != null)
                {
                    var nextGraph = LoadRuntimeGraph(package);
                    if (nextGraph != null)
                    {
                        _log.LogVerbose(Strings.FormatLog_MergingRuntimes(match.Library));
                        runtimeGraph = RuntimeGraph.Merge(runtimeGraph, nextGraph);
                    }
                }
            });
            _runtimeGraphCache[graph.Framework] = runtimeGraph;
            return runtimeGraph;
        }

        private RuntimeGraph LoadRuntimeGraph(LocalPackageInfo package)
        {
            var runtimeGraphFile = Path.Combine(package.ExpandedPath, RuntimeGraph.RuntimeGraphFileName);
            if (File.Exists(runtimeGraphFile))
            {
                using (var stream = File.OpenRead(runtimeGraphFile))
                {
                    return JsonRuntimeFormat.ReadRuntimeGraph(stream);
                }
            }
            return null;
        }

        private async Task InstallPackages(IEnumerable<RestoreTargetGraph> graphs, PackagesDirectory packagesDirectory, HashSet<LibraryIdentity> allInstalledPackages, int maxDegreeOfConcurrency)
        {
            var packagesToInstall = graphs.SelectMany(g => g.Install.Where(match => allInstalledPackages.Add(match.Library)));
            if (maxDegreeOfConcurrency <= 1)
            {
                foreach (var match in packagesToInstall)
                {
                    await InstallPackage(match, packagesDirectory);
                }
            }
            else
            {
                var bag = new ConcurrentBag<RemoteMatch>(packagesToInstall);
                var tasks = Enumerable.Range(0, maxDegreeOfConcurrency)
                    .Select(async _ =>
                        {
                            RemoteMatch match;
                            while (bag.TryTake(out match))
                            {
                                await InstallPackage(match, packagesDirectory);
                            }
                        });
                await Task.WhenAll(tasks);
            }
        }

        private Task InstallPackage(RemoteMatch installItem, PackagesDirectory packagesDirectory)
        {
            var packageIdentity = new PackageIdentity(installItem.Library.Name, installItem.Library.Version);
            return packagesDirectory.InstallPackage(
                stream => installItem.Provider.CopyToAsync(installItem.Library, stream, CancellationToken.None),
                packageIdentity,
                _log);
        }
    }
}
