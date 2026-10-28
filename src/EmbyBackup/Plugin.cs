using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace EmbyBackup
{
    /// <summary>
    /// Emby discovers this class from the plugin assembly. The GUID is also hard-coded in Web/configPage.js.
    /// Keep both in sync; changing it makes Emby treat the plugin as a different one.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;

            var assemblyVersion = typeof(Plugin).Assembly.GetName().Version;
            BackupRuntime.Configure(
                applicationPaths.ProgramDataPath,
                Path.Combine(applicationPaths.PluginsPath, "EmbyBackup"),
                assemblyVersion == null ? "1.1.1" : assemblyVersion.ToString());
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
