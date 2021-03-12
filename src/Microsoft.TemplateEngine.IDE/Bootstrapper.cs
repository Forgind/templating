// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Abstractions.Installer;
using Microsoft.TemplateEngine.Abstractions.TemplatesSources;
using Microsoft.TemplateEngine.Edge;
using Microsoft.TemplateEngine.Edge.Settings;
using Microsoft.TemplateEngine.Edge.Template;
using Microsoft.TemplateEngine.Utils;

namespace Microsoft.TemplateEngine.IDE
{
    public enum InstallationScope
    {
        Global = 0,
//TODO: enable when providers are enabled
//        Host = 1,
//        Version = 2
    }

    public class Bootstrapper
    {
        private readonly ITemplateEngineHost _host;
        private readonly Action<IEngineEnvironmentSettings> _onFirstRun;
        private readonly Paths _paths;
        private readonly TemplateCreator _templateCreator;

        private EngineEnvironmentSettings EnvironmentSettings { get; }

        public Bootstrapper(ITemplateEngineHost host, Action<IEngineEnvironmentSettings> onFirstRun, bool virtualizeConfiguration)
        {
            _host = host;
            EnvironmentSettings = new EngineEnvironmentSettings(host, x => new SettingsLoader(x));
            _onFirstRun = onFirstRun;
            _paths = new Paths(EnvironmentSettings);
            _templateCreator = new TemplateCreator(EnvironmentSettings);

            if (virtualizeConfiguration)
            {
                EnvironmentSettings.Host.VirtualizeDirectory(EnvironmentSettings.Paths.TemplateEngineRootDir);
            }
        }

        private void EnsureInitialized()
        {
            if (!_paths.Exists(_paths.User.BaseDir) || !_paths.Exists(_paths.User.FirstRunCookie))
            {
                _onFirstRun?.Invoke(EnvironmentSettings);
                _paths.WriteAllText(_paths.User.FirstRunCookie, "");
            }
        }

        public void Register(Type type)
        {
            EnvironmentSettings.SettingsLoader.Components.Register(type);
        }

        public void Register(Assembly assembly)
        {
            EnvironmentSettings.SettingsLoader.Components.RegisterMany(assembly.GetTypes());
        }

        public async Task<IReadOnlyCollection<IFilteredTemplateInfo>> ListTemplates(bool exactMatchesOnly, params Func<ITemplateInfo, MatchInfo?>[] filters)
        {
            EnsureInitialized();
            return TemplateListFilter.FilterTemplates(await EnvironmentSettings.SettingsLoader.GetTemplatesAsync(default).ConfigureAwait(false), exactMatchesOnly, filters);
        }

        public async Task<ICreationResult> CreateAsync(ITemplateInfo info, string name, string outputPath, IReadOnlyDictionary<string, string> parameters, bool skipUpdateCheck, string baselineName)
        {
            TemplateCreationResult instantiateResult = await _templateCreator.InstantiateAsync(info, name, name, outputPath, parameters, skipUpdateCheck, false, baselineName).ConfigureAwait(false);
            return instantiateResult.ResultInfo;
        }

        public async Task<ICreationEffects> GetCreationEffectsAsync(ITemplateInfo info, string name, string outputPath, IReadOnlyDictionary<string, string> parameters, string baselineName)
        {
            TemplateCreationResult instantiateResult = await _templateCreator.InstantiateAsync(info, name, name, outputPath, parameters, true, false, baselineName, true).ConfigureAwait(false);
            return instantiateResult.CreationEffects;
        }

        #region Template Package Management

        /// <summary>
        /// Get the list of available template packages
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns>the list of the template packages</returns>
        public Task<IReadOnlyList<ITemplatesSource>> GetTemplatesSources(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();
            return EnvironmentSettings.SettingsLoader.TemplatesSourcesManager.GetTemplatesSources();
        }

        /// <summary>
        /// Get the list of available managed template packages
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns>the list of managed template packages</returns>
        public Task<IReadOnlyList<IManagedTemplatesSource>> GetManagedTemplatesSources(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();
            return EnvironmentSettings.SettingsLoader.TemplatesSourcesManager.GetManagedTemplatesSources();
        }

        /// <summary>
        /// Installs the template packages
        /// The following templates packages are supported by default:
        /// - the NuGet package from NuGet feed
        /// - the NuGet package available at the path
        /// - the folder containing the template
        /// </summary>
        /// <param name="installRequests">the list of <see cref="InstallRequest"/> to install</param>
        /// <param name="scope"><see cref="InstallationScope"/> to use</param>
        /// <param name="cancellationToken"></param>
        /// <returns>the list of <see cref="InstallResult"/> containing installation result for each <see cref="InstallRequest"/></returns>
        public Task<IReadOnlyList<InstallResult>> InstallAsync(IEnumerable<InstallRequest> installRequests, InstallationScope scope = InstallationScope.Global, CancellationToken cancellationToken = default)
        {
            _ = installRequests ?? throw new ArgumentNullException(nameof(installRequests));
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();

            if (!installRequests.Any())
            {
                return Task.FromResult((IReadOnlyList<InstallResult>)new List<InstallResult>());
            }

            IManagedTemplatesSourcesProvider managedSourceProvider;
            switch (scope)
            {
                case InstallationScope.Global:
                default:
                    {
                        managedSourceProvider = EnvironmentSettings.SettingsLoader.TemplatesSourcesManager.GetManagedProvider(GlobalSettingsTemplatesSourcesProviderFactory.FactoryId);
                        break;
                    }
            };

            return managedSourceProvider.InstallAsync(installRequests, cancellationToken);
        }

        /// <summary>
        /// Gets the latest template package version for <paramref name="managedSources"/>
        /// </summary>
        /// <param name="managedSources">the template packages to check the version for</param>
        /// <param name="cancellationToken"></param>
        /// <returns>the list of <see cref="CheckUpdateResult"/> containing the result for each <see cref="IManagedTemplatesSource"/></returns>
        public async Task<IReadOnlyList<CheckUpdateResult>> GetLatestVersionsAsync(IEnumerable<IManagedTemplatesSource> managedSources, CancellationToken cancellationToken = default)
        {
            _ = managedSources ?? throw new ArgumentNullException(nameof(managedSources));
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();

            if (!managedSources.Any())
            {
                return new List<CheckUpdateResult>();
            }

            IEnumerable<IGrouping<IManagedTemplatesSourcesProvider, IManagedTemplatesSource>> requestsGroupedByProvider = managedSources.GroupBy(source => source.ManagedProvider, source => source);
            IReadOnlyList<CheckUpdateResult>[] results = await Task.WhenAll(requestsGroupedByProvider.Select(sources => sources.Key.GetLatestVersionsAsync(sources, cancellationToken))).ConfigureAwait(false);

            return results.SelectMany(result => result).ToList();
        }

        /// <summary>
        /// Updates the template packages to version specified in <see cref="UpdateRequest"/>
        /// </summary>
        /// <param name="updateRequests">the list of <see cref="UpdateRequest"/> to perform</param>
        /// <param name="cancellationToken"></param>
        /// <returns>the list of <see cref="UpdateResult"/> containing the result for each <see cref="UpdateRequest"/></returns>
        public async Task<IReadOnlyList<UpdateResult>> UpdateAsync(IEnumerable<UpdateRequest> updateRequests, CancellationToken cancellationToken = default)
        {
            _ = updateRequests ?? throw new ArgumentNullException(nameof(updateRequests));
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();

            if (!updateRequests.Any())
            {
                return new List<UpdateResult>();
            }

            IEnumerable<IGrouping<IManagedTemplatesSourcesProvider, UpdateRequest>> requestsGroupedByProvider = updateRequests.GroupBy(request => request.Source.ManagedProvider, request => request);
            IReadOnlyList<UpdateResult>[] updateResults = await Task.WhenAll(requestsGroupedByProvider.Select(requests => requests.Key.UpdateAsync(requests, cancellationToken))).ConfigureAwait(false);

            return updateResults.SelectMany(result => result).ToList();
        }

        /// <summary>
        /// Uninstalls the <paramref name="managedSources"/>
        /// </summary>
        /// <param name="managedSources">the list of <see cref="IManagedTemplatesSource"/> to uninstall</param>
        /// <param name="cancellationToken"></param>
        /// <returns>the list of <see cref="UninstallResult"/> containing the result for each <see cref="IManagedTemplatesSource"/></returns>
        public async Task<IReadOnlyList<UninstallResult>> UninstallAsync(IEnumerable<IManagedTemplatesSource> managedSources, CancellationToken cancellationToken = default)
        {
            _ = managedSources ?? throw new ArgumentNullException(nameof(managedSources));
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();

            if (!managedSources.Any())
            {
                return new List<UninstallResult>();
            }

            IEnumerable<IGrouping<IManagedTemplatesSourcesProvider, IManagedTemplatesSource>> requestsGroupedByProvider = managedSources.GroupBy(source => source.ManagedProvider, source => source);
            IReadOnlyList<UninstallResult>[] uninstallResults = await Task.WhenAll(requestsGroupedByProvider.Select(sources => sources.Key.UninstallAsync(sources, cancellationToken))).ConfigureAwait(false);

            return uninstallResults.SelectMany(result => result).ToList();
        }

        #endregion

        #region Obsolete
        [Obsolete("use Task<IReadOnlyList<InstallResult>> InstallAsync(IEnumerable<InstallRequest> installRequests, InstallationScope scope = InstallationScope.Global, CancellationToken cancellationToken = default) instead")]
        public void Install(string path)
        {
            Install(new[] { path });
        }

        [Obsolete("use Task<IReadOnlyList<InstallResult>> InstallAsync(IEnumerable<InstallRequest> installRequests, InstallationScope scope = InstallationScope.Global, CancellationToken cancellationToken = default) instead")]
        public void Install(params string[] paths)
        {
            Install((IEnumerable<string>)paths);
        }

        [Obsolete("use Task<IReadOnlyList<InstallResult>> InstallAsync(IEnumerable<InstallRequest> installRequests, InstallationScope scope = InstallationScope.Global, CancellationToken cancellationToken = default) instead")]
        public void Install(IEnumerable<string> paths)
        {
            _ = paths ?? throw new ArgumentNullException(nameof(paths));
            EnsureInitialized();

            if (!paths.Any())
            {
                return;
            }

            var installRequests = paths.Select(path => new InstallRequest() { Identifier = path }).ToList();
            Task<IReadOnlyList<InstallResult>> t = InstallAsync(installRequests);
            t.Wait();
        }

        [Obsolete("use Task<IReadOnlyList<UninstallResult>> UninstallAsync(IEnumerable<IManagedTemplatesSource> managedSources, CancellationToken cancellationToken = default) instead")]
        public IEnumerable<string> Uninstall(string path)
        {
            return Uninstall(new[] { path });
        }

        [Obsolete("use Task<IReadOnlyList<UninstallResult>> UninstallAsync(IEnumerable<IManagedTemplatesSource> managedSources, CancellationToken cancellationToken = default) instead")]
        public IEnumerable<string> Uninstall(params string[] paths)
        {
            return Uninstall((IEnumerable<string>)paths);
        }

        [Obsolete("use Task<IReadOnlyList<UninstallResult>> UninstallAsync(IEnumerable<IManagedTemplatesSource> managedSources, CancellationToken cancellationToken = default) instead")]
        public IEnumerable<string> Uninstall(IEnumerable<string> paths)
        {
            _ = paths ?? throw new ArgumentNullException(nameof(paths));
            EnsureInitialized();

            if (!paths.Any())
            {
                return Array.Empty<string>();
            }

            var task = GetManagedTemplatesSources();
            task.Wait();
            var templateSources = task.Result;

            var sourcesToUninstall = new List<IManagedTemplatesSource>();
            foreach (string path in paths)
            {
                sourcesToUninstall.AddRange(templateSources.Where(source => source.Identifier.Equals(path, StringComparison.OrdinalIgnoreCase)));
            }

            Task<IReadOnlyList<UninstallResult>> uninstallTask = UninstallAsync(sourcesToUninstall);
            uninstallTask.Wait();
            return uninstallTask.Result.Select(result => result.Source.Identifier);
        }
        #endregion
    }
}
