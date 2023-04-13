using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Autofac;
using ChinhDo.Transactions.FileManager;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Zip;
using log4net;
using Newtonsoft.Json;

using CKAN.GameVersionProviders;
using CKAN.Versioning;
using CKAN.Extensions;

namespace CKAN
{
    public enum RepoUpdateResult
    {
        Failed,
        Updated,
        NoChanges
    }

    /// <summary>
    /// Class for downloading the CKAN meta-info
    /// </summary>
    public static class Repo
    {
        /// <summary>
        /// Download and update the local CKAN meta-info.
        /// Optionally takes a URL to the zipfile repo to download.
        /// </summary>
        public static RepoUpdateResult UpdateAllRepositories(RegistryManager registry_manager, GameInstance ksp, NetAsyncDownloader downloader, NetModuleCache cache, IUser user)
        {
            var repos = registry_manager.registry.Repositories.Values
                .DistinctBy(r => r.uri)
                .ToArray();

            // Get latest copy of the game versions data (remote build map)
            user.RaiseMessage(Properties.Resources.NetRepoUpdatingBuildMap);
            ksp.game.RefreshVersions();

            // Check if the ETags have changed, quit if not
            user.RaiseProgress(Properties.Resources.NetRepoCheckingForUpdates, 0);
            if (repos.All(repo => !string.IsNullOrEmpty(repo.last_server_etag)
                                  && repo.last_server_etag == Net.CurrentETag(repo.uri)))
            {
                user.RaiseProgress(Properties.Resources.NetRepoAlreadyUpToDate, 100);
                user.RaiseMessage(Properties.Resources.NetRepoNoChanges);
                return RepoUpdateResult.NoChanges;
            }

            // Capture repo etags to be set once we're done
            var savedEtags = new Dictionary<Uri, string>();
            downloader.onOneCompleted += (url, filename, error, etag) => savedEtags.Add(url, etag);

            // Download metadata from all repos
            var targets = repos.Select(r => new Net.DownloadTarget(r.uri)).ToArray();
            downloader.DownloadAndWait(targets);

            // If we get to this point, the downloads were successful
            // Load them
            var files    = targets.Select(t => t.filename).ToArray();
            var dlCounts = new SortedDictionary<string, int>();
            var modules  = repos
                .ZipMany(files, (r, f) => ModulesFromFile(r, f, ref dlCounts, user))
                .ToArray();

            // Loading done, commit etags
            foreach (var kvp in savedEtags)
            {
                var repo = repos.Where(r => r.uri == kvp.Key).FirstOrDefault();
                var etag = kvp.Value;
                if (repo != null)
                {
                    log.DebugFormat("Setting etag for {0}: {1}", repo.name, etag);
                    repo.last_server_etag = etag;
                }
            }

            // Clean up temp files
            foreach (var f in files)
            {
                File.Delete(f);
            }

            if (modules.Length > 0)
            {
                // Save our changes
                registry_manager.registry.SetAllAvailable(modules);
                registry_manager.registry.SetDownloadCounts(dlCounts);
                registry_manager.Save(enforce_consistency: false);
                ShowUserInconsistencies(registry_manager.registry, user);
            }

            // Report success
            return RepoUpdateResult.Updated;
        }

        private static IEnumerable<CkanModule> ModulesFromFile(Repository repo, string filename, ref SortedDictionary<string, int> downloadCounts, IUser user)
        {
            if (!File.Exists(filename))
            {
                throw new FileNotFoundKraken(filename);
            }
            switch (FileIdentifier.IdentifyFile(filename))
            {
                case FileType.TarGz:
                    return ModulesFromTarGz(repo, filename, ref downloadCounts, user);
                case FileType.Zip:
                    return ModulesFromZip(repo, filename, user);
                default:
                    throw new UnsupportedKraken($"Not a .tar.gz or .zip, cannot process: {filename}");
            }
        }

        /// <summary>
        /// Returns available modules from the supplied tar.gz file.
        /// </summary>
        private static List<CkanModule> ModulesFromTarGz(Repository repo, string path, ref SortedDictionary<string, int> downloadCounts, IUser user)
        {
            log.DebugFormat("Starting registry update from tar.gz file: \"{0}\".", path);

            List<CkanModule> modules = new List<CkanModule>();

            // Open the gzip'ed file
            using (Stream inputStream = File.OpenRead(path))
            // Create a gzip stream
            using (GZipInputStream gzipStream = new GZipInputStream(inputStream))
            // Create a handle for the tar stream
            using (TarInputStream tarStream = new TarInputStream(gzipStream, Encoding.UTF8))
            {
                user.RaiseMessage(Properties.Resources.NetRepoLoadingModulesFromRepo, repo.name);
                TarEntry entry;
                int prevPercent = 0;
                while ((entry = tarStream.GetNextEntry()) != null)
                {
                    string filename = entry.Name;

                    if (filename.EndsWith("download_counts.json"))
                    {
                        downloadCounts = JsonConvert.DeserializeObject<SortedDictionary<string, int>>(
                            tarStreamString(tarStream, entry));
                        user.RaiseMessage(Properties.Resources.NetRepoLoadedDownloadCounts, repo.name);
                    }
                    else if (filename.EndsWith(".ckan"))
                    {
                        log.DebugFormat("Reading CKAN data from {0}", filename);
                        var percent = (int)(100 * inputStream.Position / inputStream.Length);
                        if (percent > prevPercent)
                        {
                            user.RaiseProgress(
                                string.Format(Properties.Resources.NetRepoLoadingModulesFromRepo, repo.name),
                                percent);
                            prevPercent = percent;
                        }

                        // Read each file into a buffer
                        string metadata_json = tarStreamString(tarStream, entry);

                        CkanModule module = ProcessRegistryMetadataFromJSON(metadata_json, filename);
                        if (module != null)
                        {
                            modules.Add(module);
                        }
                    }
                    else
                    {
                        // Skip things we don't want
                        log.DebugFormat("Skipping archive entry {0}", filename);
                    }
                }
            }
            return modules;
        }

        private static string tarStreamString(TarInputStream stream, TarEntry entry)
        {
            // Read each file into a buffer.
            int buffer_size;

            try
            {
                buffer_size = Convert.ToInt32(entry.Size);
            }
            catch (OverflowException)
            {
                log.ErrorFormat("Error processing {0}: Metadata size too large.", entry.Name);
                return null;
            }

            byte[] buffer = new byte[buffer_size];

            stream.Read(buffer, 0, buffer_size);

            // Convert the buffer data to a string.
            return Encoding.ASCII.GetString(buffer);
        }

        /// <summary>
        /// Returns available modules from the supplied zip file.
        /// </summary>
        private static List<CkanModule> ModulesFromZip(Repository repo, string path, IUser user)
        {
            log.DebugFormat("Starting registry update from zip file: \"{0}\".", path);

            List<CkanModule> modules = new List<CkanModule>();
            using (var zipfile = new ZipFile(path))
            {
                user.RaiseMessage("Loading modules from {0} repository...", repo.name);
                int index = 0;
                int prevPercent = 0;
                foreach (ZipEntry entry in zipfile)
                {
                    string filename = entry.Name;

                    if (filename.EndsWith(".ckan"))
                    {
                        log.DebugFormat("Reading CKAN data from {0}", filename);
                        var percent = (int)(100 * index / zipfile.Count);
                        if (percent > prevPercent)
                        {
                            user.RaiseProgress(
                                string.Format(Properties.Resources.NetRepoLoadingModulesFromRepo, repo.name),
                                percent);
                        }

                        // Read each file into a string.
                        string metadata_json;
                        using (var stream = new StreamReader(zipfile.GetInputStream(entry)))
                        {
                            metadata_json = stream.ReadToEnd();
                            stream.Close();
                        }

                        CkanModule module = ProcessRegistryMetadataFromJSON(metadata_json, filename);
                        if (module != null)
                        {
                            modules.Add(module);
                        }
                    }
                    else
                    {
                        // Skip things we don't want.
                        log.DebugFormat("Skipping archive entry {0}", filename);
                    }
                    ++index;
                }

                zipfile.Close();
            }
            return modules;
        }

        private static CkanModule ProcessRegistryMetadataFromJSON(string metadata, string filename)
        {
            try
            {
                CkanModule module = CkanModule.FromJson(metadata);
                // FromJson can return null for the empty string
                if (module != null)
                {
                    log.DebugFormat("Module parsed: {0}", module.ToString());
                }
                return module;
            }
            catch (Exception exception)
            {
                // Alas, we can get exceptions which *wrap* our exceptions,
                // because json.net seems to enjoy wrapping rather than propagating.
                // See KSP-CKAN/CKAN-meta#182 as to why we need to walk the whole
                // exception stack.

                bool handled = false;

                while (exception != null)
                {
                    if (exception is UnsupportedKraken || exception is BadMetadataKraken)
                    {
                        // Either of these can be caused by data meant for future
                        // clients, so they're not really warnings, they're just
                        // informational.

                        log.InfoFormat("Skipping {0} : {1}", filename, exception.Message);

                        // I'd *love a way to "return" from the catch block.
                        handled = true;
                        break;
                    }

                    // Look further down the stack.
                    exception = exception.InnerException;
                }

                // If we haven't handled our exception, then it really was exceptional.
                if (handled == false)
                {
                    if (exception == null)
                    {
                        // Had exception, walked exception tree, reached leaf, got stuck.
                        log.ErrorFormat("Error processing {0} (exception tree leaf)", filename);
                    }
                    else
                    {
                        // In case whatever's calling us is lazy in error reporting, we'll
                        // report that we've got an issue here.
                        log.ErrorFormat("Error processing {0} : {1}", filename, exception.Message);
                    }

                    throw;
                }
                return null;
            }
        }

        private static void ShowUserInconsistencies(Registry registry, IUser user)
        {
            // However, if there are any sanity errors let's show them to the user so at least they're aware
            var sanityErrors = registry.GetSanityErrors();
            if (sanityErrors.Any())
            {
                var sanityMessage = new StringBuilder();

                sanityMessage.AppendLine(Properties.Resources.NetRepoInconsistenciesHeader);
                foreach (var sanityError in sanityErrors)
                {
                    sanityMessage.Append("- ");
                    sanityMessage.AppendLine(sanityError);
                }

                user.RaiseMessage(sanityMessage.ToString());
            }
        }

        private static readonly ILog log = LogManager.GetLogger(typeof(Repo));
    }
}
