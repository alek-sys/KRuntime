// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Framework.Runtime;
using Microsoft.Framework.PackageManager.Restore.NuGet;
using NuGet;

namespace Microsoft.Framework.PackageManager
{
    public class InstallCommand
    {
        private AddCommand _addCommand;
        private RestoreCommand _restoreCommand;

        public InstallCommand(AddCommand addCmd, RestoreCommand restoreCmd)
        {
            _addCommand = addCmd;
            _restoreCommand = restoreCmd;
        }

        public IReport Report;

        public bool ExecuteCommand()
        {
            if (string.IsNullOrEmpty(_addCommand.Name))
            {
                Report.WriteLine("Name of dependency to install is required.");
                return false;
            }

            SemanticVersion version = null;
            if (!string.IsNullOrEmpty(_addCommand.Version) && !SemanticVersion.TryParse(_addCommand.Version, out version))
            {
                Report.WriteLine("Version of dependency to install is invalid.");
                return false;
            }

            // Load package sources from solution settings
            _addCommand.ProjectDir = _addCommand.ProjectDir ?? Directory.GetCurrentDirectory();
            var rootDir = ProjectResolver.ResolveRootDirectory(_addCommand.ProjectDir);
            var fileSystem = new PhysicalFileSystem(Directory.GetCurrentDirectory());
            var settings = SettingsUtils.ReadSettings(solutionDir: rootDir,
                nugetConfigFile: null,
                fileSystem: fileSystem,
                machineWideSettings: new CommandLineMachineWideSettings());
            var sourceProvider = PackageSourceBuilder.CreateSourceProvider(settings);

            var allSources = sourceProvider.LoadPackageSources();
            var enabledSources = _restoreCommand.Sources.Any() ?
                Enumerable.Empty<PackageSource>() :
                allSources.Where(s => s.IsEnabled);

            var addedSources = _restoreCommand.Sources.Concat(_restoreCommand.FallbackSources).Select(
                value => allSources.FirstOrDefault(source => CorrectName(value, source)) ?? new PackageSource(value));

            var effectiveSources = enabledSources.Concat(addedSources).Distinct().ToList();

            var packageFeeds = new List<IPackageFeed>();
            foreach (var source in effectiveSources)
            {
                if (new Uri(source.Source).IsFile)
                {
                    packageFeeds.Add( new PackageFolder(source.Source, Report));
                }
                else
                {
#if NET45
                    packageFeeds.Add(new PackageFeed(
                        source.Source, source.UserName, source.Password, _restoreCommand.NoCache, Report));
#endif
                }
            }

            PackageInfo result = null;
            if (version == null)
            {
                result = FindLatestVersion(packageFeeds, _addCommand.Name);
            }
            else
            {
                result = FindBestMatch(packageFeeds, _addCommand.Name, version);
            }

            if (result == null)
            {
                Report.WriteLine("Unable to locate {0} >= {1}", _addCommand.Name, _addCommand.Version);
                return false;
            }

            return _addCommand.ExecuteCommand() && _restoreCommand.ExecuteCommand();
        }

        private static PackageInfo FindLatestVersion(IEnumerable<IPackageFeed> packageFeeds, string packageName)
        {
            PackageInfo latest = null;
            foreach (var feed in packageFeeds)
            {
                var results = feed.FindPackagesByIdAsync(packageName).Result;
                foreach (var result in results)
                {
                    if (latest == null)
                    {
                        latest = result;
                        continue;
                    }

                    latest = latest.Version > result.Version ? latest: result;
                }
            }
            return latest;
        }

        private static PackageInfo FindBestMatch(IEnumerable<IPackageFeed> packageFeeds, string packageName,
            SemanticVersion idealVersion)
        {
            PackageInfo bestResult = null;
            foreach (var feed in packageFeeds)
            {
                var results = feed.FindPackagesByIdAsync(packageName).Result;
                foreach (var result in results)
                {
                    if (VersionUtility.ShouldUseConsidering(
                        current: bestResult == null ? null : bestResult.Version,
                        considering: result.Version,
                        ideal: idealVersion))
                    {
                        bestResult = result;
                    }
                }
            }
            return bestResult;
        }

        private static bool CorrectName(string value, PackageSource source)
        {
            return source.Name.Equals(value, StringComparison.CurrentCultureIgnoreCase) ||
                source.Source.Equals(value, StringComparison.OrdinalIgnoreCase);
        }

    }
}
