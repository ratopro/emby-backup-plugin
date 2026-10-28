using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace EmbyBackup
{
    /// <summary>
    /// Emby discovers this class from the plugin assembly. The GUID is also hard-coded in Web/configPage.js.
    /// Keep both in sync; changing it makes Emby treat the plugin as a different one.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage, IServerEntryPoint
    {
        private readonly ITaskManager _taskManager;
        private readonly ILogger _logger;

        public static Plugin Instance { get; private set; }

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ITaskManager taskManager,
            ILogManager logManager)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _taskManager = taskManager;
            _logger = logManager.GetLogger("EmbyBackup");

            var assemblyVersion = typeof(Plugin).Assembly.GetName().Version;
            BackupRuntime.Configure(
                applicationPaths.ProgramDataPath,
                Path.Combine(applicationPaths.PluginsPath, "EmbyBackup"),
                assemblyVersion == null ? "1.4.1" : assemblyVersion.ToString());
        }

        public void Run()
        {
            ApplySchedule();
        }

        public void Dispose()
        {
        }

        /// <summary>
        /// Emby calls this from the plugin configuration endpoint. Normalizing here means an invalid or
        /// partial value can never reach the trigger, and the task is re-scheduled right after saving.
        /// </summary>
        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            var typed = configuration as PluginConfiguration;
            if (typed != null)
            {
                typed.Normalize();
            }

            base.UpdateConfiguration(configuration);
            ApplySchedule();
        }

        private void ApplySchedule()
        {
            if (Configuration == null)
            {
                return;
            }

            Configuration.Normalize();
            BackupSchedule.Apply(_taskManager, Configuration, _logger);
        }

        public override string Name
        {
            get { return "Emby Backup"; }
        }

        public override string Description
        {
            get
            {
                return "Full backups of Emby's server data and databases, without metadata, artwork or media.";
            }
        }

        public override Guid Id
        {
            get { return new Guid("5a3c44fa-8c66-4f07-b75e-4a8c6123852f"); }
        }

        public string ProgramDataPath
        {
            get { return ApplicationPaths.ProgramDataPath; }
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    // Emby matches page names case-insensitively across all plugins, and the built-in
                    // "Backup & Restore" plugin already owns "embybackup". Keep these names unique.
                    Name = "EmbyServerBackup",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html",
                    IsMainConfigPage = true,
                },
                new PluginPageInfo
                {
                    Name = "EmbyServerBackupJS",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.js",
                },
            };
        }

        public ImageFormat ThumbImageFormat
        {
            get { return ImageFormat.Png; }
        }

        public Stream GetThumbImage()
        {
            return GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".Images.thumb.png");
        }
    }
}
